using ARM.Integration.Core.Model;
using ARM.Integration.EIM.Orchestration;
using System.Text.Json.Nodes;

namespace ARM.Integration.EIM;

/// <summary>
/// Builds the message sent back to the source system when a D365 business event reconciles an
/// asynchronously submitted message. Kept separate from the Functions handler so the envelope
/// shape is testable without Service Bus.
/// </summary>
public static class BusinessEventResponse
{
    /// <summary>
    /// Mirrors the envelope <see cref="BaseCrudOrchestration{TSource, TDest}"/> sends on the
    /// synchronous path, so a source system sees one shape across both flows.
    /// </summary>
    public static OutboundMessageEnvelope<JsonObject> Build(
        QueueMessageContract awaiting,
        SysMessageBusinessEventsContract evt,
        int status,
        string destinationId)
    {
        var data = new JsonObject { ["CreatedTransactionId"] = destinationId };

        // Keyed by the field name D365 supplies, so the source system receives {"SalesId": "200007"}
        // rather than a generic key it has to interpret.
        if (HasResponseKey(evt))
            data[ResponseKeyName(evt)] = evt.MessageResponseKey;

        return new OutboundMessageEnvelope<JsonObject>
        {
            SourceId = awaiting.SourceId,
            Action = "Reconcile",
            Entity = string.IsNullOrWhiteSpace(awaiting.SourceEntity)
                ? awaiting.DestinationEntity
                : awaiting.SourceEntity,
            Status = status == MessageStatus.Completed ? "Success" : "Failed",
            StatusMessage = evt.MessageInfolog,
            Data = data,
        };
    }

    /// <summary>Renders the response key for a message log, e.g. "SalesId=200007", or null.</summary>
    public static string? DescribeResponseKey(SysMessageBusinessEventsContract evt) =>
        HasResponseKey(evt) ? $"{ResponseKeyName(evt)}={evt.MessageResponseKey}" : null;

    // Failure events send these as empty strings rather than omitting them.
    private static bool HasResponseKey(SysMessageBusinessEventsContract evt) =>
        !string.IsNullOrWhiteSpace(evt.MessageResponseKey);

    private static string ResponseKeyName(SysMessageBusinessEventsContract evt) =>
        string.IsNullOrWhiteSpace(evt.MessageResponseKeyFieldName)
            ? "ResponseKey"
            : evt.MessageResponseKeyFieldName;
}
