using ARM.Integration.EIM;
using ARM.Integration.EIM.Orchestration;
using ARM.Integration.EIM.Utilities;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ARM.Integration.EIM.Customer;

/// <summary>
/// Processes D365 FSC data events from Service Bus.
/// Routes each event to the appropriate _To_Capture orchestration based on entity name.
/// For update events, only processes messages where tracked fields have changed.
/// </summary>
public class QueueDataEventsHandler(
    ILoggerFactory loggerFactory,
    IReadOnlyDictionary<string, IOrchestration> orchestrations)
{
    private readonly ILogger logger = loggerFactory.CreateLogger<QueueDataEventsHandler>();

    /// <summary>
    /// Maps D365 entity names to their orchestration type and destination queue setting.
    /// To add a new entity, add a single entry here.
    /// </summary>
    private static readonly Dictionary<string, (string Type, string QueueSetting)> EntityRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mserp_custcustomerv3entity"]      = ("Customer_To_Capture","D365ToCaptureQueue"),
        ["mserp_smmcontactpersonv2entity"]   = ("Contact_To_Capture","D365ToCaptureQueue"),
        ["mserp_custpostaladdressv2entity"]  = ("CustomerAddress_To_Capture","D365ToCaptureQueue"),
        ["mserp_dirpartycontactv3entity"]    = ("PartyContact_To_Capture","D365ToCaptureQueue"),
    };

    /// <summary>
    /// Tracked fields per entity for OnExternalUpdated events.
    /// If an entity is not listed here, all update fields are processed.
    /// If listed, at least one tracked field must be in the changed set.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> TrackedFieldsByEntity = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mserp_custcustomerv3entity"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "mserp_organizationname",
            "mserp_namealias",
            "mserp_dataareaid",
            "mserp_customeraccount",
            "mserp_customergroupid",
            "mserp_employeeresponsiblenumber",
            "mserp_blocked",
            "mserp_depositrequired",
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
        }
    };

    /// <summary>
    /// Required non-empty fields per entity. If the field is missing or blank,
    /// the message is discarded before DB insert.
    /// </summary>
    private static readonly Dictionary<string, string> EntityRequiredFields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mserp_smmcontactpersonv2entity"] = "mserp_wtc_associatedcustomeraccount",
    };

    /// <summary>
    /// At least one field in the set must be non-empty per entity, otherwise
    /// the message is discarded before DB insert.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> EntityRequiredAnyFields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mserp_dirpartycontactv3entity"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "mserp_wtc_contactperson",
            "mserp_wtc_custaccount",
        },
    };

    [Function(nameof(QueueDataEventsHandler))]
    public async Task Run(
        [ServiceBusTrigger("%D365DataEventQueue%", Connection = "ServiceBus")]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions)
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
        if (context.MessageName == "OnExternalUpdated"
            && TrackedFieldsByEntity.TryGetValue(context.PrimaryEntityName, out var trackedFields))
        {
            var changedFields = D365FSCEventParser.GetChangedFields(context.InputParameters) ?? [];
            if (!changedFields.Any(trackedFields.Contains))
            {
                logger.LogInformation("Message {id} has no tracked field changes, skipping.", message.MessageId);
                await messageActions.CompleteMessageAsync(message);
                return;
            }
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
                logger.LogInformation("Message {id} skipped: none of the required fields [{fields}] are populated.",
                    message.MessageId, string.Join(", ", anyFields));
                await messageActions.CompleteMessageAsync(message);
                return;
            }
        }

        // Process
        var queue = Environment.GetEnvironmentVariable(route.QueueSetting);
        var connectionString = Environment.GetEnvironmentVariable("EIMDB")
            ?? throw new InvalidOperationException("EIMDB environment variable is not set.");

        // Resolve business key for SourceId (e.g., CustomerAccount, ContactPersonId)
        var targetAttrs = D365FSCEventParser.GetTargetAttributes(context.InputParameters);
        var businessKey = BusinessKeyResolver.GetBusinessKey(context.PrimaryEntityName, targetAttrs);

        var controller = new EIMController(connectionString, logger, orchestrations);

        var messageId = controller.InsertMessage(new MessageRequest
        {
            SourceId = businessKey ?? context.OperationId,
            Environment = Environment.GetEnvironmentVariable("DefaultEnvironment"),
            DestinationSystem = "CAPTURE",
            SourceSystem = "D365FSC",
            Payload = payload,
            Type = route.Type,
            SourceEntity = context.PrimaryEntityName,
            DestinationEntity = "",
            Queue = queue,
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
            throw; // Let the Functions host handle retry/dead-letter
        }
    }
}