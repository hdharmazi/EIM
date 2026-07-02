using ARM.Integration.Core.Helpers;
using ARM.Integration.Core.Model;
using ARM.Integration.EIM.Utilities;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ARM.Integration.EIM.Orchestration;

/// <summary>
/// Base orchestration for Capture → D365 F&amp;SC integrations that submit to a D365 message queue
/// via a custom service action (e.g., <c>WTC_SysMessageService/SendMessageWithResponse</c>) instead
/// of an OData entity CRUD call.
///
/// The order body is forwarded as pass-through JSON: the envelope <c>Data</c> object is wrapped into a
/// <see cref="MessageQueueContract"/> without field-level mapping. <c>dataAreaId</c> is lifted out of
/// <c>Data</c> to <c>_companyId</c> and removed from <c>_messageContent</c> (Option A).
///
/// D365 processes the message asynchronously in a batch, so this orchestration returns a
/// Submitted / Awaiting D365 result (status 5) carrying the returned <c>CreatedTransactionId</c> as the
/// destination correlation id. Final status is reconciled later from a D365 business event.
/// </summary>
public abstract class D365ServiceActionOrchestration : IOrchestration
{
    /// <summary>The full D365 service action path (e.g., "api/services/WTC_Integration/WTC_SysMessageService/SendMessageWithResponse").</summary>
    protected abstract string ServiceMethod { get; }

    /// <summary>The D365 message queue name (maps to <c>_messageQueue</c>).</summary>
    protected abstract string MessageQueue { get; }

    /// <summary>The D365 message type (maps to <c>_messageType</c>).</summary>
    protected abstract string MessageType { get; }

    public async Task<OrchestrationResult> ProcessAsync(
        QueueMessageContract data,
        string connectionString,
        IOrchestrationLogger stepLog,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(stepLog);

        stepLog.Log("Processing", "Processing Messages");

        stepLog.Log("ResolveEnvironments", $"Dest={data.DestinationSystem}, Env={data.Environment}");
        var destinationEnvironmentConnection = EIMUtilities.GetEnvironment(
            data.DestinationSystem, data.Environment, connectionString);

        // ── Parse envelope and extract the pass-through Data object ──
        var root = JsonNode.Parse(data.Payload)?.AsObject()
            ?? throw new JsonException("Payload is not a JSON object.");
        var action = root["Action"]?.GetValue<string>() ?? "New";
        var dataNode = root["Data"]?.AsObject()
            ?? throw new JsonException("Envelope 'Data' is missing.");

        // ── Option A: lift dataAreaId → _companyId, then strip from content ──
        var companyId = dataNode["dataAreaId"]?.GetValue<string>();
        dataNode.Remove("dataAreaId");

        var contract = new MessageQueueContract
        {
            CompanyId = companyId,
            MessageQueue = MessageQueue,
            MessageType = MessageType,
            MessageContent = dataNode.ToJsonString(),
        };

        stepLog.Log("BuildMessage", $"CompanyId={companyId}, Type={MessageType}, Queue={MessageQueue}");

        var d365Client = D365FSCServiceCollectionExtensions.CreateStandaloneClient(destinationEnvironmentConnection);

        string response;
        try
        {
            stepLog.Log("CallService", ServiceMethod);
            response = await d365Client.CallServiceAsync(
                ServiceMethod, JsonSerializer.Serialize(contract), ct);
            stepLog.Log("CallService", "Succeeded");
        }
        catch (Exception ex)
        {
            stepLog.LogError("CallService", ExceptionHelper.GetMessages(ex));
            throw;
        }

        var createdTransactionId = ExtractCreatedTransactionId(response);
        stepLog.Log("Submitted", $"CreatedTransactionId={createdTransactionId}");

        return OrchestrationResult.Submitted(response, createdTransactionId);
    }

    /// <summary>Extracts the numeric <c>CreatedTransactionId</c> from the service response as a string.</summary>
    private static string? ExtractCreatedTransactionId(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return null;

        try
        {
            var node = JsonNode.Parse(response)?.AsObject();
            var id = node?["CreatedTransactionId"];
            return id?.ToString();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
