using ARM.Integration.EIM.Orchestration;
using ARM.Integration.EIM.Utilities;
using ARM.Integration.Core.Helpers;
using ARM.Integration.Core.Model;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Wilson.Integration.Automation.Inbound;

/// <summary>
/// Reconciles the status of previously submitted asynchronous messages from D365 F&amp;SC business
/// events received by <c>QueueMessageQueueBusinessEventHandler</c>.
///
/// Routes by <c>BusinessEventId</c>. For
/// <c>SysMessageProcessorMessageProcessedBusinessEvent</c>, it correlates the event's
/// <c>CreatedTransactionId</c> to <c>MESSAGEQUEUETABLE.DESTINATIONID</c> and updates the existing row
/// from <c>MessageProcessingResult</c>: Processed → 2 (Completed), Failed/Cancelled/Error → 3
/// (Failed, with <c>MessageInfolog</c> logged). It does NOT insert a new message row. Updates are
/// monotonic, so late or duplicate delivery cannot rewrite a row that already reached a terminal
/// status. Both outcomes are reported back to the source system on its response queue.
/// </summary>
internal sealed class BusinessEventReconciler(
    ILogger logger,
    IReadOnlyDictionary<string, IOrchestration> orchestrations)
{
    private const string MessageProcessedEventId = "SysMessageProcessorMessageProcessedBusinessEvent";

    // The real payload is ~11 KB: MessageContent alone is ~9 KB and the message-processor deep link
    // is another ~1 KB. Logging it whole is not viable.
    private const int MaxLoggedBodyLength = 2048;

    public async Task ProcessAsync(ServiceBusReceivedMessage message, ServiceBusMessageActions messageActions)
    {
        var body = message.Body.ToString();
        var queueName = Environment.GetEnvironmentVariable("D365BusinessEventQueue");

        logger.LogInformation(
            "[BusinessEventTrace] Handler={handler} Queue={queue} MessageId={id} Body={body}",
            nameof(Functions.QueueMessageQueueBusinessEventHandler), queueName, message.MessageId, Truncate(body));

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
            logger.LogWarning(ex,
                "Business event {id} could not be parsed as JSON; dead-lettering. Body={body}",
                message.MessageId, Truncate(body));
            await messageActions.DeadLetterMessageAsync(message,
                deadLetterReason: "UnparseableBody",
                deadLetterErrorDescription: Truncate(ExceptionHelper.GetMessages(ex), 4096));
            return;
        }

        if (!string.Equals(businessEventId, MessageProcessedEventId, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation(
                "Business event {id} has unhandled BusinessEventId '{eventId}', completing without action.",
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
            logger.LogWarning(ex,
                "Reconciliation event {id} ({eventId}) failed to deserialize; dead-lettering. Body={body}",
                message.MessageId, businessEventId, Truncate(body));
            await messageActions.DeadLetterMessageAsync(message,
                deadLetterReason: "DeserializationFailed",
                deadLetterErrorDescription: Truncate(ExceptionHelper.GetMessages(ex), 4096));
            return;
        }

        if (evt?.CreatedTransactionId is null or 0)
        {
            logger.LogWarning(
                "Reconciliation event {id} ({eventId}) has no CreatedTransactionId; dead-lettering. Body={body}",
                message.MessageId, businessEventId, Truncate(body));
            await messageActions.DeadLetterMessageAsync(message,
                deadLetterReason: "MissingCreatedTransactionId",
                deadLetterErrorDescription: "The event carries no non-zero CreatedTransactionId to correlate on.");
            return;
        }

        var destinationId = evt.CreatedTransactionId.Value.ToString();

        // MessageProcessingResult is the terminal outcome and is populated on both success and
        // failure. MessageState is the queue lifecycle position and reads "Queued" on success.
        var (status, reason) = BusinessEventOutcome.Map(evt.MessageProcessingResult);

        logger.LogInformation(
            "[BusinessEventTrace] Correlating BusinessEventId={eventId} CreatedTransactionId={dest} "
            + "MessageProcessingResult={result} MessageState={state} -> STATUS={status}",
            businessEventId, destinationId, evt.MessageProcessingResult, evt.MessageState, status);

        var connectionString = Environment.GetEnvironmentVariable("EIMDB")
            ?? throw new InvalidOperationException("EIMDB environment variable is not set.");

        try
        {
            var controller = new EIMController(connectionString, logger, orchestrations);

            // Resolve the awaiting transaction row so reconciliation steps attach to it
            var messageId = controller.GetMessageIdByDestinationId(destinationId);

            if (messageId is null)
            {
                logger.LogWarning(
                    "Reconciliation event {id}: no MESSAGEQUEUETABLE row matched DESTINATIONID={dest}; "
                    + "dead-lettering. Body={body}",
                    message.MessageId, destinationId, Truncate(body));
                await messageActions.DeadLetterMessageAsync(message,
                    deadLetterReason: "UnmatchedCorrelationId",
                    deadLetterErrorDescription:
                        $"No MESSAGEQUEUETABLE row has DESTINATIONID={destinationId}.");
                return;
            }

            var awaiting = controller.GetMessage(messageId);

            // Record major decision steps against the original transaction (visible in Processing Steps)
            var stepLog = new OrchestrationLogger(connectionString, long.Parse(messageId), logger);
            stepLog.Log("Reconciling",
                $"BusinessEvent={businessEventId}, EventId={evt.EventId}, CreatedTransactionId={destinationId}");

            if (status is null)
            {
                await HandleNonTerminalAsync(message, messageActions, evt, stepLog, destinationId, reason);
                return;
            }

            var responseKey = BusinessEventResponse.DescribeResponseKey(evt);

            var log = status == MessageStatus.Completed
                ? $"D365 Processed. TransactionId={destinationId}"
                : $"D365 {evt.MessageProcessingResult}. {evt.MessageInfolog}";

            stepLog.Log("MessageProcessingResult",
                $"{evt.MessageProcessingResult} (MessageState={evt.MessageState}) -> STATUS={status}");

            var rows = controller.UpdateStatusByDestinationId(destinationId, status.Value, log, responseKey);

            if (rows == 0)
            {
                // The row exists but is already Completed or Failed; monotonicity blocked the write.
                stepLog.LogWarning("AlreadyReconciled",
                    $"Row is already terminal; ignored {evt.MessageProcessingResult} -> STATUS={status}.");
                logger.LogWarning(
                    "Reconciliation event {id}: DESTINATIONID={dest} is already terminal; "
                    + "ignored {result} -> STATUS={status}.",
                    message.MessageId, destinationId, evt.MessageProcessingResult, status);
                await messageActions.CompleteMessageAsync(message);
                return;
            }

            if (status == MessageStatus.Completed)
                stepLog.Log("Reconciled", $"D365 Processed. {responseKey} ({rows} row(s) updated).");
            else
                stepLog.LogWarning("Reconciled", $"D365 {evt.MessageProcessingResult}. {evt.MessageInfolog}");

            logger.LogInformation(
                "Reconciliation event {id}: set STATUS={status} for DESTINATIONID={dest} ({rows} row(s)).",
                message.MessageId, status, destinationId, rows);

            await SendResponseAsync(awaiting, evt, status.Value, destinationId, connectionString, stepLog);

            await messageActions.CompleteMessageAsync(message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Reconciliation failed for event {id}. Message will be retried or dead-lettered.",
                message.MessageId);
            throw;
        }
    }

    /// <summary>
    /// Acknowledges a progress event that carries no terminal outcome. The row stays at
    /// AwaitingD365; the message is completed because redelivery would not change the outcome.
    /// </summary>
    private async Task HandleNonTerminalAsync(
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        SysMessageBusinessEventsContract evt,
        IOrchestrationLogger stepLog,
        string destinationId,
        BusinessEventOutcome.NonTerminalReason reason)
    {
        var detail = $"MessageProcessingResult='{evt.MessageProcessingResult}', "
            + $"MessageState='{evt.MessageState}'. Status unchanged.";

        if (reason == BusinessEventOutcome.NonTerminalReason.Unrecognised)
        {
            stepLog.LogWarning("NotYetTerminal", $"Unrecognised outcome. {detail}");
            logger.LogWarning(
                "Reconciliation event {id}: unrecognised MessageProcessingResult '{result}' for "
                + "DESTINATIONID={dest}. Status left unchanged.",
                message.MessageId, evt.MessageProcessingResult, destinationId);
        }
        else
        {
            stepLog.Log("NotYetTerminal", detail);
        }

        await messageActions.CompleteMessageAsync(message);
    }

    /// <summary>
    /// Reports the outcome to the source system. Best-effort: the row is the system of record and
    /// keeps its reconciled status even if the send fails.
    /// </summary>
    private static async Task SendResponseAsync(
        QueueMessageContract awaiting,
        SysMessageBusinessEventsContract evt,
        int status,
        string destinationId,
        string connectionString,
        IOrchestrationLogger stepLog)
    {
        if (string.IsNullOrWhiteSpace(awaiting.ResponseQueue))
            return;

        var outcome = status == MessageStatus.Completed ? "Success" : "Failed";

        try
        {
            var sourceConnection = EIMUtilities.GetEnvironment(
                awaiting.SourceSystem, awaiting.Environment, connectionString);

            var responseEnvelope = BusinessEventResponse.Build(awaiting, evt, status, destinationId);

            stepLog.Log("SendResponse", $"Queue={awaiting.ResponseQueue}, Status={outcome}");
            await using var sb = new ServiceBusHelper(sourceConnection);
            await sb.SendAsync(awaiting.ResponseQueue, JsonSerializer.Serialize(responseEnvelope));
            stepLog.Log("SendResponse", "Succeeded");
        }
        catch (Exception responseEx)
        {
            stepLog.LogError("SendResponse",
                $"Failed to send {outcome} response: {ExceptionHelper.GetMessages(responseEx)}");
        }
    }

    private static string Truncate(string value, int max = MaxLoggedBodyLength) =>
        value.Length <= max ? value : $"{value[..max]}… [{value.Length} chars total]";
}
