using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using ARM.Integration.EIM.Orchestration;
using ARM.Integration.EIM.Retry;
using ARM.Integration.EIM.Routing;
using System.Text.Json;

namespace ARM.Integration.EIM.Customer;

/// <summary>
/// Shared handler for all Capture → D365FSC queue functions.
/// Routes messages to the correct orchestration based on the "Entity" field in the message envelope.
/// Each Azure Function trigger delegates to this handler with its specific configuration.
/// </summary>
public static class QueueCaptureHandler
{
    public static async Task ProcessAsync(
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        ILogger logger,
        IReadOnlyDictionary<string, IOrchestration> orchestrations,
        IngestGate gate,
        IFailureClassifier failureClassifier,
        RetryScheduleCalculator retrySchedule,
        string responseQueueSetting)
    {
        var connectionString = Environment.GetEnvironmentVariable("EIMDB")
            ?? throw new InvalidOperationException("EIMDB environment variable is not set.");
        var defaultEnvironment = Environment.GetEnvironmentVariable("DefaultEnvironment");
        var responsequeue = Environment.GetEnvironmentVariable(responseQueueSetting);

        logger.LogInformation("Message ID: {id}", message.MessageId);

        // ── Read the Entity field from the envelope to determine orchestration ──
        var body = message.Body.ToString();
        string entity;
        string? sourceId;
        try
        {
            using var doc = JsonDocument.Parse(body);
            entity = doc.RootElement.TryGetProperty("Entity", out var entityProp)
                ? entityProp.GetString() ?? ""
                : "";
            sourceId = doc.RootElement.TryGetProperty("SourceId", out var sidProp)
                ? sidProp.GetString()
                : null;
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to parse message {id}, cannot determine Entity.", message.MessageId);
            await messageActions.CompleteMessageAsync(message);
            return;
        }

        var controller = new EIMController(connectionString, logger, orchestrations, failureClassifier, retrySchedule);

        var request = new MessageRequest
        {
            SourceId = sourceId ?? message.MessageId,
            Environment = defaultEnvironment ?? "",
            DestinationSystem = "D365FSC",
            SourceSystem = "CAPTURE",
            Payload = body,
            // Replaced by the route's orchestration type; the gate resolves it.
            Type = "",
            ResponseQueue = responsequeue,
            SourceEntity = entity,
            DestinationEntity = "",
            // Service Bus redelivers a message under the same id, so this catches redelivery
            // without treating a second change to the same record as a duplicate.
            DedupeKey = message.MessageId,
        };

        try
        {
            var (messageId, decision) = await controller.InsertAndProcessAsync(request, gate);

            logger.LogInformation("Message {id}: Entity={entity}, {reason}, EIM id {messageId}",
                message.MessageId, entity, decision.Reason, messageId);

            await messageActions.CompleteMessageAsync(message);
        }
        catch (RouteNotFoundException ex)
        {
            // Dead-lettered rather than completed. Routes are seeded from live traffic, so an
            // unroutable message means a seeding gap - which must be visible, not discarded.
            logger.LogError(ex, "Message {id} has no route for entity '{entity}'.", message.MessageId, entity);
            await messageActions.DeadLetterMessageAsync(
                message, deadLetterReason: "NoRoute", deadLetterErrorDescription: ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Processing failed for message {id}. Message will be retried or dead-lettered.", message.MessageId);
            throw;
        }
    }
}
