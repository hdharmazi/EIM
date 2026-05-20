using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using ARM.Integration.EIM.Orchestration;
using System.Text.Json;

namespace ARM.Integration.EIM.Customer;

/// <summary>
/// Shared handler for all Capture → D365FSC queue functions.
/// Routes messages to the correct orchestration based on the "Entity" field in the message envelope.
/// Each Azure Function trigger delegates to this handler with its specific configuration.
/// </summary>
public static class QueueCaptureHandler
{
    /// <summary>
    /// Maps the envelope "Entity" value to the orchestration type name.
    /// Add new entities here as integrations are added.
    /// </summary>
    private static readonly Dictionary<string, string> EntityToOrchestration = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Customer"] = "Customer_To_D365FSC",
        ["Contact"] = "Contact_To_D365FSC",
        ["PartyContact"] = "PartyContact_To_D365FSC",
        ["CustomerAddress"] = "CustomerAddress_To_Capture",
    };

    public static async Task ProcessAsync(
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        ILogger logger,
        IReadOnlyDictionary<string, IOrchestration> orchestrations,
        string activeFlag,
        string responseQueueSetting)
    {
        var active = Environment.GetEnvironmentVariable(activeFlag);
        if (string.IsNullOrEmpty(active) || string.Equals(active, "False", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

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

        if (string.IsNullOrWhiteSpace(entity) || !EntityToOrchestration.TryGetValue(entity, out var orchestrationType))
        {
            logger.LogWarning("Message {id} has unsupported Entity '{entity}', skipping.", message.MessageId, entity);
            await messageActions.CompleteMessageAsync(message);
            return;
        }

        logger.LogInformation("Message {id}: Entity={entity}, Orchestration={type}", message.MessageId, entity, orchestrationType);

        var controller = new EIMController(connectionString, logger, orchestrations);

        var messageId = controller.InsertMessage(new MessageRequest
        {
            SourceId = sourceId ?? message.MessageId,
            Environment = defaultEnvironment,
            DestinationSystem = "D365FSC",
            SourceSystem = "CAPTURE",
            Payload = body,
            Type = orchestrationType,
            ResponseQueue = responsequeue,
            SourceEntity = entity,
            DestinationEntity = "",
        });
        logger.LogInformation("Inserted Events: {id}", message.MessageId);

        try
        {
            await controller.ProcessMessageAsync(messageId);
            await messageActions.CompleteMessageAsync(message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Processing failed for message {id}. Message will be retried or dead-lettered.", message.MessageId);
            throw;
        }
    }
}
