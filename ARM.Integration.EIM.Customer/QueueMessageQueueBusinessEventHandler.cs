using ARM.Integration.EIM;
using ARM.Integration.EIM.Orchestration;
using ARM.Integration.EIM.Utilities;
using ARM.Integration.Core.Helpers;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ARM.Integration.EIM.Customer;

/// <summary>
/// Consumes D365 F&amp;SC business events from the generic business-event queue and reconciles the
/// status of previously submitted asynchronous messages.
///
/// Routes by <c>BusinessEventId</c>. For
/// <c>SysMessageProcessorMessageProcessedBusinessEvent</c>, it correlates the event's
/// <c>CreatedTransactionId</c> to <c>MESSAGEQUEUETABLE.DESTINATIONID</c> and updates the existing row:
/// Processed → 2 (Completed), Failed/Cancelled → 3 (Failed, with <c>MessageInfolog</c> logged).
/// It does NOT insert a new message row. Updates are idempotent and tolerate late/duplicate delivery.
/// </summary>
public class QueueMessageQueueBusinessEventHandler(
    ILoggerFactory loggerFactory,
    IReadOnlyDictionary<string, IOrchestration> orchestrations)
{
    private readonly ILogger logger = loggerFactory.CreateLogger<QueueMessageQueueBusinessEventHandler>();

    private const string MessageProcessedEventId = "SysMessageProcessorMessageProcessedBusinessEvent";

    [Function(nameof(QueueMessageQueueBusinessEventHandler))]
    public async Task Run(
        [ServiceBusTrigger("%D365BusinessEventQueue%", Connection = "ServiceBus")]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions)
    {
        logger.LogInformation("Business event message ID: {id}", message.MessageId);

        var body = message.Body.ToString();

        // ── Trace: prove the event reached THIS handler, and on which queue ──
        var queueName = Environment.GetEnvironmentVariable("D365BusinessEventQueue");
        logger.LogInformation(
            "[BusinessEventTrace] Handler={handler} Queue={queue} MessageId={id} Body={body}",
            nameof(QueueMessageQueueBusinessEventHandler), queueName, message.MessageId, body);

        // ── Route by BusinessEventId ──
        string? businessEventId;
        try
        {
            using var doc = JsonDocument.Parse(body);
            businessEventId = doc.RootElement.TryGetProperty("BusinessEventId", out var p)
                ? p.GetString()
                : null;
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to parse business event {id}, skipping.", message.MessageId);
            await messageActions.CompleteMessageAsync(message);
            return;
        }

        if (!string.Equals(businessEventId, MessageProcessedEventId, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Business event {id} has unregistered BusinessEventId '{eventId}', skipping.",
                message.MessageId, businessEventId);
            await messageActions.CompleteMessageAsync(message);
            return;
        }

        // ── Parse the reconciliation event ──
        SysMessageBusinessEventsContract? evt;
        try
        {
            evt = JsonSerializer.Deserialize<SysMessageBusinessEventsContract>(body);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to deserialize reconciliation event {id}, skipping.", message.MessageId);
            await messageActions.CompleteMessageAsync(message);
            return;
        }

        if (evt?.CreatedTransactionId is null or 0)
        {
            logger.LogWarning("Reconciliation event {id} has no CreatedTransactionId, skipping.", message.MessageId);
            await messageActions.CompleteMessageAsync(message);
            return;
        }

        // ── Map terminal state → status ──
        var status = evt.MessageState?.Trim().ToLowerInvariant() switch
        {
            "processed" => 2,
            "failed" => 3,
            "cancelled" => 3,
            _ => (int?)null,
        };

        if (status is null)
        {
            logger.LogInformation("Reconciliation event {id} has non-terminal state '{state}', skipping.",
                message.MessageId, evt.MessageState);
            await messageActions.CompleteMessageAsync(message);
            return;
        }

        var destinationId = evt.CreatedTransactionId.Value.ToString();
        var log = status == 2
            ? $"D365 Processed. TransactionId={destinationId}"
            : $"D365 {evt.MessageState}. {evt.MessageInfolog}";

        // ── Trace: the correlation key and the status we are about to apply ──
        logger.LogInformation(
            "[BusinessEventTrace] Correlating BusinessEventId={eventId} CreatedTransactionId={dest} MessageState={state} -> STATUS={status}",
            businessEventId, destinationId, evt.MessageState, status);

        var connectionString = Environment.GetEnvironmentVariable("EIMDB")
            ?? throw new InvalidOperationException("EIMDB environment variable is not set.");

        try
        {
            var controller = new EIMController(connectionString, logger, orchestrations);

            // Resolve the awaiting transaction row so reconciliation steps attach to it
            var messageId = controller.GetMessageIdByDestinationId(destinationId);

            if (messageId is null)
            {
                logger.LogWarning("Reconciliation event {id}: no MESSAGEQUEUETABLE row matched DESTINATIONID={dest}.",
                    message.MessageId, destinationId);
                await messageActions.CompleteMessageAsync(message);
                return;
            }

            var awaiting = controller.GetMessage(messageId);

            // Record major decision steps against the original transaction (visible in Processing Steps)
            var stepLog = new OrchestrationLogger(connectionString, long.Parse(messageId), logger);
            stepLog.Log("Reconciling", $"BusinessEvent={businessEventId}, CreatedTransactionId={destinationId}");
            stepLog.Log("MessageState", $"{evt.MessageState} -> STATUS={status}");

            var rows = controller.UpdateStatusByDestinationId(destinationId, status.Value, log);

            if (status == 2)
                stepLog.Log("Reconciled", $"D365 Processed. {rows} row(s) updated.");
            else
                stepLog.LogWarning("Reconciled", $"D365 {evt.MessageState}. {evt.MessageInfolog}");

            logger.LogInformation("Reconciliation event {id}: set STATUS={status} for DESTINATIONID={dest} ({rows} row(s)).",
                message.MessageId, status, destinationId, rows);

            // On failure, notify the source system on its response queue with the failure detail
            if (status == 3 && !string.IsNullOrWhiteSpace(awaiting.ResponseQueue))
            {
                try
                {
                    var sourceConnection = EIMUtilities.GetEnvironment(
                        awaiting.SourceSystem, awaiting.Environment, connectionString);

                    var responseEnvelope = new OutboundMessageEnvelope<JsonObject>
                    {
                        SourceId = awaiting.SourceId,
                        Entity = "SalesOrder",
                        Status = "Failed",
                        StatusMessage = evt.MessageInfolog,
                        Data = new JsonObject { ["CreatedTransactionId"] = destinationId },
                    };

                    var responsePayload = JsonSerializer.Serialize(responseEnvelope);
                    stepLog.Log("SendResponse", $"Queue={awaiting.ResponseQueue}, Status=Failed");
                    await using var sb = new ServiceBusHelper(sourceConnection);
                    await sb.SendAsync(awaiting.ResponseQueue, responsePayload);
                    stepLog.Log("SendResponse", "Succeeded");
                }
                catch (Exception responseEx)
                {
                    stepLog.LogError("SendResponse", $"Failed to send failure response: {ExceptionHelper.GetMessages(responseEx)}");
                }
            }

            await messageActions.CompleteMessageAsync(message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Reconciliation failed for event {id}. Message will be retried or dead-lettered.", message.MessageId);
            throw;
        }
    }
}
