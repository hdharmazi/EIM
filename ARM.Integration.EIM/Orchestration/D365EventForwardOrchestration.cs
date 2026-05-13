using ARM.Integration.Core.Model;
using ARM.Integration.EIM.Utilities;
using System.Text.Json;

namespace ARM.Integration.EIM.Orchestration;

/// <summary>
/// D365 FSC-specific forward orchestration that parses D365 Business Event payloads (EventContext)
/// and provides the familiar <c>MapFields(getField, action)</c> pattern with change tracking.
/// 
/// Use this as the base class when the SOURCE system is D365 Finance &amp; Supply Chain
/// and you want to forward mapped data to another system's queue.
/// 
/// For non-D365 source systems (e.g., Salesforce webhooks), extend <see cref="BaseForwardOrchestration{TDest}"/> directly.
/// </summary>
/// <typeparam name="TDest">The destination message contract type.</typeparam>
public abstract class D365EventForwardOrchestration<TDest> : BaseForwardOrchestration<TDest>
    where TDest : class
{
    // Holds the parsed context during ParseAndMap so GetSourceId can access it
    private EventContext? _currentContext;

    /// <summary>
    /// Maps D365 FSC event attributes to the destination contract.
    /// Use <paramref name="getField"/> to retrieve values — it respects change tracking automatically.
    /// </summary>
    /// <param name="getField">
    /// A function that retrieves a field value by D365 attribute name.
    /// Pass <c>force: true</c> for key/identity fields that should always be included.
    /// Returns null if the field wasn't changed (for Update actions) and force is false.
    /// </param>
    /// <param name="action">The determined action string: "New", "Update", or "Delete".</param>
    protected abstract TDest MapFields(Func<string, bool, string?> getField, string action);

    /// <summary>
    /// Returns the source ID for the message envelope given the queue message and parsed event context.
    /// Override to customize (e.g., use MessageId instead of SourceId).
    /// </summary>
    protected virtual string GetSourceId(QueueMessageContract data, EventContext context)
        => data.SourceId;

    // Override the base GetSourceId to delegate to the context-aware version
    protected sealed override string GetSourceId(QueueMessageContract message)
        => GetSourceId(message, _currentContext!);

    protected override (string action, bool hasTrackedChanges, TDest mappedData, string? sourceTimestamp)
        ParseAndMap(string payload)
    {
        var context = JsonSerializer.Deserialize<EventContext>(payload)
            ?? throw new JsonException("Event context is null.");

        _currentContext = context;

        var targetAttrs = D365FSCEventParser.GetTargetAttributes(context.InputParameters);

        var actiontype = context.MessageName;
        string actionString;
        List<string> changedFields;

        switch (actiontype)
        {
            case "OnExternalUpdated":
                changedFields = D365FSCEventParser.GetChangedFields(context.InputParameters) ?? [];
                actionString = "Update";
                break;
            case "OnExternalCreated":
                changedFields = targetAttrs.Keys.ToList();
                actionString = "New";
                break;
            case "OnExternalDeleted":
                changedFields = [];
                actionString = "Delete";
                break;
            default:
                changedFields = [];
                actionString = actiontype ?? "Unknown";
                break;
        }

        bool flagTrack = false;

        string? GetFieldValue(string fieldName, bool force = false)
        {
            if (actionString == "Update" && !changedFields.Contains(fieldName))
            {
                if (!force) return null;
            }
            else
            {
                flagTrack = true;
            }

            targetAttrs.TryGetValue(fieldName, out var value);
            return value?.ToString()?.Trim();
        }

        var mappedData = MapFields(GetFieldValue, actionString);

        return (actionString, flagTrack, mappedData, context.OperationCreatedOn);
    }
}
