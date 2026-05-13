using ARM.Integration.Core.Helpers;
using ARM.Integration.Core.Model;
using ARM.Integration.EIM.Utilities;
using System.Text.Json;

namespace ARM.Integration.EIM.Orchestration;

/// <summary>
/// Generic base class for orchestrations that parse a source message, map it to a target format,
/// execute a CRUD action on the target system, and optionally send a response.
/// 
/// This class is system-agnostic — it doesn't know about D365, Shopify, SAP, etc.
/// System-specific API calls are handled by subclasses (e.g., D365CrudOrchestration).
/// </summary>
/// <typeparam name="TSource">The source system message contract type.</typeparam>
/// <typeparam name="TDest">The target system entity/payload type.</typeparam>
public abstract class BaseCrudOrchestration<TSource, TDest> : IOrchestration
    where TSource : class
    where TDest : class
{
    /// <summary>The entity name used in response messages (e.g., "Customer", "Contact").</summary>
    protected abstract string ResponseEntityName { get; }

    /// <summary>
    /// Deserializes the raw payload into the source data, along with envelope metadata.
    /// Override to handle different envelope formats (e.g., CaptureMessageEnvelope vs generic).
    /// </summary>
    protected abstract (string? SourceId, string? Action, TSource Data) DeserializePayload(string payload);

    /// <summary>Maps from the source system contract to the target system entity.</summary>
    protected abstract TDest MapToTarget(TSource source);

    /// <summary>
    /// Executes the CRUD action on the target system and returns the result (if any).
    /// This is where system-specific API calls live (D365 OData, Shopify REST, SAP RFC, etc.).
    /// </summary>
    /// <param name="action">The action: "New", "Update", "Delete".</param>
    /// <param name="target">The mapped target entity.</param>
    /// <param name="destinationConnection">The target system connection string/config.</param>
    /// <param name="stepLog">Logger for step tracking.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The result entity from the target system (e.g., created entity with generated keys), or null.</returns>
    protected abstract Task<TDest?> ExecuteActionAsync(
        string action, TDest target, string destinationConnection,
        IOrchestrationLogger stepLog, CancellationToken ct);

    /// <summary>Builds the response data from either the target result or the source context.</summary>
    protected abstract TSource BuildResponseData(TSource source, TDest? result, string action);

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
            stepLog.Log("ResolveEnvironments", $"Source={data.SourceSystem}, Dest={data.DestinationSystem}, Env={data.Environment}");
            var sourceEnvironmentConnection = EIMUtilities.GetEnvironment(
                data.SourceSystem, data.Environment, connectionString);
            var destinationEnvironmentConnection = EIMUtilities.GetEnvironment(
                data.DestinationSystem, data.Environment, connectionString);

            var (sourceId, actionString, sourceData) = DeserializePayload(data.Payload);
            actionString ??= "Unknown";

            stepLog.Log("DeserializePayload", $"Action={actionString}");

            var targetEntity = MapToTarget(sourceData);

            var targetResult = await ExecuteActionAsync(
                actionString, targetEntity, destinationEnvironmentConnection, stepLog, ct);

            var responseEnvelope = new OutboundMessageEnvelope<TSource>
            {
                SourceId = sourceId,
                Action = actionString,
                Entity = ResponseEntityName,
                Status = "Success",
                Data = BuildResponseData(sourceData, targetResult, actionString)
            };

            var payload = JsonSerializer.Serialize(responseEnvelope);

            if (!string.IsNullOrWhiteSpace(data.ResponseQueue))
            {
                stepLog.Log("SendResponse", $"Queue={data.ResponseQueue}");
                await using var sb = new ServiceBusHelper(sourceEnvironmentConnection);
                await sb.SendAsync(data.ResponseQueue, payload, cancellationToken: ct);
                stepLog.Log("SendResponse", "Succeeded");
            }

            result = payload;
            stepLog.Log("Result", result);
        }
        catch (Exception ex)
        {
            var errorMessage = ExceptionHelper.GetMessages(ex);
            stepLog.LogError("Error", errorMessage);

            // Send failure response back to source system via response queue
            try
            {
                if (!string.IsNullOrWhiteSpace(data.ResponseQueue))
                {
                    var srcConn = EIMUtilities.GetEnvironment(
                        data.SourceSystem, data.Environment, connectionString);

                    var (sourceId, actionString, sourceData) = DeserializePayload(data.Payload);

                    var failureEnvelope = new OutboundMessageEnvelope<TSource>
                    {
                        SourceId = sourceId,
                        Action = actionString ?? "Unknown",
                        Entity = ResponseEntityName,
                        Status = "Failed",
                        StatusMessage = errorMessage,
                        Data = sourceData
                    };

                    var failurePayload = JsonSerializer.Serialize(failureEnvelope);

                    stepLog.Log("SendFailureResponse", $"Queue={data.ResponseQueue}");
                    await using var sb = new ServiceBusHelper(srcConn);
                    await sb.SendAsync(data.ResponseQueue, failurePayload, cancellationToken: ct);
                    stepLog.Log("SendFailureResponse", "Succeeded");

                    result = failurePayload;
                }
            }
            catch (Exception responseEx)
            {
                stepLog.LogError("SendFailureResponse", $"Failed to send failure response: {ExceptionHelper.GetMessages(responseEx)}");
            }

            throw;
        }

        return result;
    }
}
