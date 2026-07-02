using ARM.Integration.EIM;
using ARM.Integration.EIM.Orchestration;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text.Json;

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
public class QueueBusinessEventHandler(
    ILoggerFactory loggerFactory,
    IReadOnlyDictionary<string, IOrchestration> orchestrations)
{
    private readonly ILogger logger = loggerFactory.CreateLogger<QueueBusinessEventHandler>();

    private const string MessageProcessedEventId = "SysMessageProcessorMessageProcessedBusinessEvent";

    [Function(nameof(QueueBusinessEventHandler))]
    public async Task Run(
        [ServiceBusTrigger("%D365BusinessEventQueue%", Connection = "ServiceBus")]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions)
    {
        logger.LogInformation("Business event message ID: {id}", message.MessageId);

        var body = message.Body.ToString();

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
        SysMessageBusinessEventContract? evt;
        try
        {
            evt = JsonSerializer.Deserialize<SysMessageBusinessEventContract>(body);
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

        var connectionString = Environment.GetEnvironmentVariable("EIMDB")
            ?? throw new InvalidOperationException("EIMDB environment variable is not set.");

        try
        {
            var controller = new EIMController(connectionString, logger, orchestrations);
            var rows = controller.UpdateStatusByDestinationId(destinationId, status.Value, log);

            if (rows == 0)
            {
                logger.LogWarning("Reconciliation event {id}: no MESSAGEQUEUETABLE row matched DESTINATIONID={dest}.",
                    message.MessageId, destinationId);
            }
            else
            {
                logger.LogInformation("Reconciliation event {id}: set STATUS={status} for DESTINATIONID={dest} ({rows} row(s)).",
                    message.MessageId, status, destinationId, rows);
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
