using ARM.Integration.Core.Helpers;
using ARM.Integration.Core.Model;
using ARM.Integration.EIM.Utilities;
using System.Text.Json;

namespace ARM.Integration.EIM.Orchestration;

/// <summary>
/// Generic base class for orchestrations that parse a source payload, map it to a destination format,
/// and forward the result to a Service Bus queue.
/// 
/// This class is system-agnostic — it doesn't know about D365, Salesforce, SAP, etc.
/// System-specific parsing is handled by subclasses (e.g., D365EventForwardOrchestration).
/// </summary>
/// <typeparam name="TDest">The destination message contract type.</typeparam>
public abstract class BaseForwardOrchestration<TDest> : IOrchestration where TDest : class
{
    /// <summary>The entity name used in the message envelope (e.g., "Customer", "Contact").</summary>
    protected abstract string EntityName { get; }

    /// <summary>
    /// Parses the raw payload and returns the action, a flag indicating whether tracked fields changed,
    /// and the mapped destination data.
    /// </summary>
    /// <param name="payload">Raw payload string from the queue message.</param>
    /// <returns>
    /// action: The action string (e.g., "New", "Update", "Delete").
    /// hasTrackedChanges: Whether the message contains relevant changes that warrant forwarding.
    /// mappedData: The mapped destination contract.
    /// sourceTimestamp: Optional timestamp from the source event.
    /// </returns>
    protected abstract (string action, bool hasTrackedChanges, TDest mappedData, string? sourceTimestamp)
        ParseAndMap(string payload);

    /// <summary>
    /// Returns the source ID for the forwarded message envelope.
    /// Override to customize (e.g., use MessageId instead of SourceId).
    /// </summary>
    protected virtual string GetSourceId(QueueMessageContract message) => message.SourceId;

    /// <summary>
    /// Creates the message envelope to be serialized and sent to the destination queue.
    /// Override to use a custom envelope type.
    /// </summary>
    protected virtual object CreateEnvelope(TDest data, string sourceId, string action, string? timestamp)
    {
        return new OutboundMessageEnvelope<TDest>
        {
            SourceId = sourceId,
            Action = action,
            Entity = EntityName,
            CreatedDateTime = timestamp,
            Data = data
        };
    }

    public async Task<string> ProcessAsync(
        QueueMessageContract data,
        string connectionString,
        IOrchestrationLogger stepLog,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(stepLog);

        stepLog.Log("Processing", "Processing Messages");

        string result;

        try
        {
            stepLog.Log("ResolveEnvironments", $"Dest={data.DestinationSystem}, Env={data.Environment}");
            var destinationEnvironmentConnection = EIMUtilities.GetEnvironment(
                data.DestinationSystem, data.Environment, connectionString);
            var destinationQueue = data.Queue;

            var (action, hasTrackedChanges, mappedData, sourceTimestamp) = ParseAndMap(data.Payload);

            stepLog.Log("DetermineAction", action);
            stepLog.Log("MappedPayload", JsonSerializer.Serialize(mappedData));

            if (!hasTrackedChanges)
            {
                result = "not tracked fields";
                stepLog.Log("Result", result);
                return result;
            }

            var envelope = CreateEnvelope(mappedData, GetSourceId(data), action, sourceTimestamp);
            var payload = JsonSerializer.Serialize(envelope);

            stepLog.Log("SendToQueue", $"Queue={destinationQueue}");
            await using var sb = new ServiceBusHelper(destinationEnvironmentConnection);
            await sb.SendAsync(destinationQueue, payload, cancellationToken: ct);
            stepLog.Log("SendToQueue", "Succeeded");

            result = payload;
            stepLog.Log("Result", result);
        }
        catch (Exception ex)
        {
            var message = ExceptionHelper.GetMessages(ex);
            stepLog.LogError("Error", message);
            throw;
        }

        return result;
    }
}
