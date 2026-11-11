using ARM.Integration.EIM.Orchestration;
using ARM.Integration.EIM.Retry;
using ARM.Integration.EIM.Routing;
using ARM.Integration.EIM.Utilities;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Wilson.Integration.Automation.Outbound;

/// <summary>
/// Processes D365 FSC data events received by <c>QueueDataEventsHandler</c>.
/// Routes each event to the appropriate _To_Capture orchestration based on entity name.
/// For update events on an entity with tracked fields, only processes messages where a tracked
/// field has changed (see <see cref="HasTrackedChange"/>); the soft-delete address entity tracks
/// only its deleted flag, so re-notifications of an already-deleted address are not forwarded.
/// </summary>
internal sealed class DataEventProcessor(
    ILogger logger,
    IReadOnlyDictionary<string, IOrchestration> orchestrations,
    IngestGate gate,
    IFailureClassifier failureClassifier,
    RetryScheduleCalculator retrySchedule)
{
    /// <summary>
    /// Maps D365 entity names to their orchestration type and destination queue setting.
    /// A new routed entity also needs a <see cref="BusinessKeyResolver"/> <c>EntityKeyFields</c> entry
    /// and a <c>TestData/SourceEntities</c> fixture in the tests; <c>BusinessKeyRegistrationTests</c>
    /// fails until both exist.
    /// </summary>
    internal static readonly Dictionary<string, (string Type, string QueueSetting)> EntityRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mserp_wtc_custcustomerentity"]     = ("Customer_To_Capture", "D365ToCaptureQueue"),
        ["mserp_smmcontactpersonv2entity"]   = ("Contact_To_Capture","D365ToCaptureQueue"),
        ["mserp_wtc_custpostaladdressv2entity"]  = ("CustomerAddress_To_Capture","D365ToCaptureQueue"),
        ["mserp_wtc_custpostaladdressdeletedv2entity"] = ("CustomerAddressDelete_To_Capture", "D365ToCaptureQueue"),
        ["mserp_dirpartycontactv3entity"]    = ("PartyContact_To_Capture","D365ToCaptureQueue"),
    };

    /// <summary>
    /// Tracked fields per entity for OnExternalUpdated events, checked by <see cref="HasTrackedChange"/>.
    /// If an entity is not listed here, all update fields are processed.
    /// If listed, at least one tracked field must be in the changed set; otherwise the message is
    /// completed before DB insert.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> TrackedFieldsByEntity = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mserp_wtc_custcustomerentity"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "mserp_organizationname",
            "mserp_namealias",
            "mserp_dataareaid",
            "mserp_customeraccount",
            "mserp_customergroupid",
            "mserp_employeeresponsiblenumber",
            "mserp_onholdstatus",
            "mserp_dotnumber",
            "mserp_taxstatus",
            "mserp_partynumber"
        },
        ["mserp_smmcontactpersonv2entity"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "mserp_contactpersonid",
            "mserp_firstname",
            "mserp_dataareaid",
            "mserp_lastname",
            "mserp_middlename",
            "mserp_associatedpartynumber",
            "mserp_wtc_associatedcustomeraccount",
            "mserp_associatedcontactnumber",
            "mserp_contactpersonpartynumber"
        },
        ["mserp_wtc_custpostaladdressv2entity"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "mserp_isprimary",
            "mserp_addresslocationroles",
            "mserp_addressdescription",
            "mserp_addresscity",
            "mserp_addresszipcode",
            "mserp_addressstreet",
            "mserp_addresscountryregionid",
            "mserp_addressstate",
        },
        // Soft-delete rows only ever have the flag set (WTC_IsDeleted is one-way). The flag is among the
        // changed fields only when the location first enters the entity, i.e. the deletion itself.
        // Later updates are D365 re-notifications (address version flips, backfill touches) and must
        // not be forwarded as further deletes.
        ["mserp_wtc_custpostaladdressdeletedv2entity"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "mserp_wtc_isdeleted",
        },
    };

    /// <summary>
    /// Required non-empty fields per entity. If the field is missing or blank,
    /// the message is discarded before DB insert.
    /// </summary>
    private static readonly Dictionary<string, string> EntityRequiredFields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mserp_smmcontactpersonv2entity"] = "mserp_wtc_associatedcustomeraccount",
        ["mserp_wtc_custpostaladdressdeletedv2entity"] = "mserp_addresslocationid",
    };

    /// <summary>
    /// At least one field in the set must be non-empty per entity, otherwise
    /// the message is discarded before DB insert.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> EntityRequiredAnyFields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mserp_dirpartycontactv3entity"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "mserp_wtc_partyrole",
        },
    };

    public async Task ProcessAsync(ServiceBusReceivedMessage message, ServiceBusMessageActions messageActions)
    {
        logger.LogInformation("Message ID: {id}", message.MessageId);

        var payload = message.Body.ToString();

        EventContext context;
        try
        {
            context = JsonSerializer.Deserialize<EventContext>(payload)
                ?? throw new JsonException("Event context is null.");
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to deserialize message {id}, skipping.", message.MessageId);
            await messageActions.CompleteMessageAsync(message);
            return;
        }

        // Skip test endpoint messages
        using var doc = JsonDocument.Parse(payload);
        if (doc.RootElement.TryGetProperty("BusinessEventId", out var bizEventId)
            && string.Equals(bizEventId.GetString(), "BusinessEventsTestEndpointContract", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Message {id} is a test endpoint event, skipping.", message.MessageId);
            await messageActions.CompleteMessageAsync(message);
            return;
        }

        // Check excluded users
        if (context.UserAzureActiveDirectoryObjectId is not null)
        {
            var excludedUsers = Environment.GetEnvironmentVariable("ExcludedUsers")?.Split(';') ?? [];
            if (excludedUsers.Contains(context.UserAzureActiveDirectoryObjectId, StringComparer.OrdinalIgnoreCase))
            {
                logger.LogInformation("Message {id} is from excluded user {user}, skipping.",
                    message.MessageId, context.UserAzureActiveDirectoryObjectId);
                await messageActions.CompleteMessageAsync(message);
                return;
            }
        }


        // Route by entity name
        if (!EntityRoutes.TryGetValue(context.PrimaryEntityName, out var route))
        {
            logger.LogInformation("Message {id} has unsupported entity '{entity}', skipping.",
                message.MessageId, context.PrimaryEntityName);
            await messageActions.CompleteMessageAsync(message);
            return;
        }


        // For updates, skip if no tracked fields were changed
        if (!HasTrackedChange(context, out var changedFields))
        {
            // An absent ChangedFields parameter is unexpected from D365; a present but
            // untracked list is the normal re-notification case.
            var hasChangedFieldsParameter = context.InputParameters?.Any(p => p.Key == "ChangedFields") == true;
            logger.Log(hasChangedFieldsParameter ? LogLevel.Information : LogLevel.Warning,
                "Message {id} has no tracked field changes, skipping. Entity={entity}, Event={eventName}, ChangedFields=[{changedFields}]",
                message.MessageId, context.PrimaryEntityName, context.MessageName, string.Join(", ", changedFields));
            await messageActions.CompleteMessageAsync(message);
            return;
        }

        // Skip if a required field is missing or blank (before DB insert)
        if (EntityRequiredFields.TryGetValue(context.PrimaryEntityName, out var requiredField))
        {
            var attrs = D365FSCEventParser.GetTargetAttributes(context.InputParameters);
            if (attrs is null || !attrs.TryGetValue(requiredField, out var fieldVal)
                || string.IsNullOrWhiteSpace(fieldVal?.ToString()))
            {
                logger.LogInformation("Message {id} skipped: required field '{field}' is empty or missing.",
                    message.MessageId, requiredField);
                await messageActions.CompleteMessageAsync(message);
                return;
            }
        }

        // Skip if none of the required-any fields are populated (before DB insert)
        if (EntityRequiredAnyFields.TryGetValue(context.PrimaryEntityName, out var anyFields))
        {
            var attrs = D365FSCEventParser.GetTargetAttributes(context.InputParameters);
            var hasAny = attrs is not null
                && anyFields.Any(f => attrs.TryGetValue(f, out var v) && !string.IsNullOrWhiteSpace(v?.ToString()));
            if (!hasAny)
            {
                object? partyNumber = null;
                attrs?.TryGetValue("mserp_partynumber", out partyNumber);
                var skippedKey = attrs is null ? null : BusinessKeyResolver.GetBusinessKey(context.PrimaryEntityName, attrs);
                // Blank means a non-customer/contact party (normal); absent means the column was lost in D365/Dataverse.
                var level = attrs is not null && anyFields.Any(attrs.ContainsKey) ? LogLevel.Information : LogLevel.Warning;
                logger.Log(level,
                    "Message {id} skipped: none of the required fields [{fields}] are populated. Entity={entity}, Event={eventName}, BusinessKey={businessKey}, PartyNumber={partyNumber}",
                    message.MessageId, string.Join(", ", anyFields), context.PrimaryEntityName, context.MessageName,
                    skippedKey, partyNumber);
                await messageActions.CompleteMessageAsync(message);
                return;
            }
        }

        // Process
        var queue = Environment.GetEnvironmentVariable(route.QueueSetting);
        var connectionString = Environment.GetEnvironmentVariable("EIMDB")
            ?? throw new InvalidOperationException("EIMDB environment variable is not set.");

        var controller = new EIMController(connectionString, logger, orchestrations, failureClassifier, retrySchedule);

        var request = new MessageRequest
        {
            // Business key (e.g., CustomerAccount, ContactPersonId), else the operation id with a warning.
            SourceId = ResolveSourceId(context, logger, message.MessageId),
            Environment = Environment.GetEnvironmentVariable("DefaultEnvironment") ?? "",
            DestinationSystem = "CAPTURE",
            SourceSystem = "D365FSC",
            Payload = payload,
            // Replaced by the route's orchestration type; the gate resolves it.
            Type = "",
            SourceEntity = context.PrimaryEntityName,
            DestinationEntity = "",
            Queue = queue,
            // The D365 operation, not the record: a second change to the same customer is a new
            // operation, while a redelivery of this one repeats it.
            DedupeKey = context.OperationId,
        };

        try
        {
            var (messageId, decision) = await controller.InsertAndProcessAsync(request, gate);

            logger.LogInformation("Message {id}: Entity={entity}, {reason}, EIM id {messageId}",
                message.MessageId, context.PrimaryEntityName, decision.Reason, messageId);

            await messageActions.CompleteMessageAsync(message);
        }
        catch (RouteNotFoundException ex)
        {
            // Dead-lettered rather than completed: an unroutable message means a seeding gap.
            logger.LogError(ex, "Message {id} has no route for entity '{entity}'.",
                message.MessageId, context.PrimaryEntityName);
            await messageActions.DeadLetterMessageAsync(
                message, deadLetterReason: "NoRoute", deadLetterErrorDescription: ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Processing failed for message {id}. Message will be retried or dead-lettered.", message.MessageId);
            throw; // Let the Functions host handle retry/dead-letter
        }
    }

    /// <summary>
    /// True when the event should continue: it is not an OnExternalUpdated, its entity has no tracked
    /// fields, or at least one tracked field is among its ChangedFields.
    /// <paramref name="changedFields"/> is the event's changed-field list (empty when the
    /// ChangedFields parameter is absent or not an update), for logging.
    /// </summary>
    internal static bool HasTrackedChange(EventContext context, out IReadOnlyList<string> changedFields)
    {
        if (context.MessageName != "OnExternalUpdated")
        {
            changedFields = [];
            return true;
        }

        changedFields = D365FSCEventParser.GetChangedFields(context.InputParameters);

        return !TrackedFieldsByEntity.TryGetValue(context.PrimaryEntityName, out var trackedFields)
            || changedFields.Any(trackedFields.Contains);
    }

    /// <summary>
    /// Returns the entity's business key as the message SourceId. When it cannot be resolved
    /// (no Target attributes, key attribute absent, or blank), logs a warning and falls back to
    /// the D365 operation id.
    /// </summary>
    internal static string ResolveSourceId(EventContext context, ILogger logger, string messageId)
    {
        var targetAttrs = D365FSCEventParser.GetTargetAttributes(context.InputParameters);
        var businessKey = BusinessKeyResolver.GetBusinessKey(context.PrimaryEntityName, targetAttrs);
        if (businessKey is not null)
            return businessKey;

        logger.LogWarning(
            "Message {MessageId}: business key field {KeyField} is missing or blank for entity {Entity}; SourceId falls back to operation id {FallbackSourceId}.",
            messageId,
            BusinessKeyResolver.GetKeyField(context.PrimaryEntityName) ?? "(none registered)",
            context.PrimaryEntityName,
            context.OperationId);

        return context.OperationId;
    }
}
