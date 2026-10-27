using ARM.Integration.EIM;
using ARM.Integration.EIM.Utilities;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wilson.Integration.Automation.Functions;
using Wilson.Integration.Automation.Outbound;
using Wilson.Integration.Orchestrations;
using Xunit;

namespace Wilson.Integration.Tests;

/// <summary>
/// D365 re-raises OnExternalUpdated on the soft-delete address entity for addresses that are
/// already deleted (address version flips on a customer save, backfill touches). Only the event
/// whose ChangedFields contain the deleted flag is the deletion itself; the rest are completed
/// before the DB insert.
/// </summary>
public class CustomerAddressDeleteReemissionTests
{
    private const string Entity = "mserp_wtc_custpostaladdressdeletedv2entity";
    private const string AddressEntity = "mserp_wtc_custpostaladdressv2entity";

    private static JsonNode Load(params string[] path) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(
            new[] { AppContext.BaseDirectory, "TestData" }.Concat(path).ToArray())))!;

    /// <summary>Genuine deletion: ChangedFields = [mserp_wtc_isdeleted].</summary>
    private static JsonNode Genuine() => Load("CustomerAddressDeleted.OnExternalUpdated.json");

    /// <summary>Re-notification shaped like BAT message 78833: version fields changed, flag already Yes.</summary>
    private static JsonNode Reemission() => Load("CustomerAddressDeleted.Reemission.OnExternalUpdated.json");

    private static JsonArray Inputs(JsonNode node) => node["InputParameters"]!.AsArray();

    private static JsonNode WithChangedFields(JsonNode node, params string[] fields)
    {
        var inputs = Inputs(node);
        var value = new JsonArray(fields.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray());
        var existing = inputs.FirstOrDefault(p => (string?)p!["key"] == "ChangedFields");
        if (existing is null)
            inputs.Add(new JsonObject { ["key"] = "ChangedFields", ["value"] = value });
        else
            existing["value"] = value;
        return node;
    }

    private static JsonNode WithoutParameter(JsonNode node, string key)
    {
        var inputs = Inputs(node);
        inputs.Remove(inputs.First(p => (string?)p!["key"] == key));
        return node;
    }

    private static EventContext Context(JsonNode node) =>
        JsonSerializer.Deserialize<EventContext>(node.ToJsonString())!;

    private static bool HasTrackedChange(JsonNode node) =>
        DataEventProcessor.HasTrackedChange(Context(node), out _);

    // --- The deletion itself is forwarded / An update-shaped event yields a delete ---

    [Fact]
    public void GenuineDeletion_HasTrackedChange()
    {
        Assert.True(HasTrackedChange(Genuine()));
    }

    [Fact]
    public void FirstEntryShapedLike78800_EveryFieldPlusFlag_PreImageNull_HasTrackedChange()
    {
        var node = Genuine();
        Inputs(node).Add(new JsonObject { ["key"] = "PreImage", ["value"] = null });
        var attributeKeys = Inputs(node).First(p => (string?)p!["key"] == "Target")!["value"]!["Attributes"]!
            .AsArray().Select(a => (string)a!["key"]!).ToArray();
        WithChangedFields(node, attributeKeys);

        Assert.Contains("mserp_wtc_isdeleted", attributeKeys);
        Assert.True(HasTrackedChange(node));
    }

    // --- A customer edit re-notifies a deleted address / Any update without the flag is withheld ---

    [Fact]
    public void Reemission_HasNoTrackedChange()
    {
        var context = Context(Reemission());

        var result = DataEventProcessor.HasTrackedChange(context, out var changedFields);

        Assert.False(result);
        Assert.Equal(
            new[] { "mserp_addressstreet", "mserp_addressvalidfrom", "mserp_addressvalidto", "mserp_formatteddeliveryaddress" },
            changedFields);
    }

    [Fact]
    public void EmptyChangedFields_HasNoTrackedChange()
    {
        Assert.False(HasTrackedChange(WithChangedFields(Reemission())));
    }

    [Fact]
    public void OnlyUntrackedFieldChanged_HasNoTrackedChange()
    {
        Assert.False(HasTrackedChange(WithChangedFields(Reemission(), "mserp_addresslocationroles")));
    }

    // --- Any other message name yields a delete ---

    [Fact]
    public void Reemission_AsOnExternalCreated_IsNotFiltered()
    {
        var node = Reemission();
        node["MessageName"] = "OnExternalCreated";

        Assert.True(HasTrackedChange(node));
    }

    [Fact]
    public async Task Handler_CompletesReemission_WithoutReachingInsert()
    {
        var (actions, _) = await RunHandler(Reemission(), "test-reemission");

        Assert.True(actions.Completed);
    }

    // --- Withheld re-notifications are logged for diagnosis ---

    [Fact]
    public async Task Handler_LogsWithheldReemission_AtInformation()
    {
        var (_, logger) = await RunHandler(Reemission(), "test-reemission-log");

        var entry = Assert.Single(logger.Entries, e => e.Message.Contains("has no tracked field changes"));
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("test-reemission-log", entry.Message);
        Assert.Contains(Entity, entry.Message);
        Assert.Contains("OnExternalUpdated", entry.Message);
        Assert.Contains("mserp_addressvalidfrom", entry.Message);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    // --- Missing changed-field list is withheld with a warning ---

    [Fact]
    public void ChangedFields_MissingParameter_ReturnsEmpty()
    {
        var context = Context(WithoutParameter(Reemission(), "ChangedFields"));

        Assert.Empty(D365FSCEventParser.GetChangedFields(context.InputParameters));
    }

    [Fact]
    public void ChangedFields_NullValue_ReturnsEmpty()
    {
        var node = Reemission();
        Inputs(node).First(p => (string?)p!["key"] == "ChangedFields")!["value"] = null;

        Assert.Empty(D365FSCEventParser.GetChangedFields(Context(node).InputParameters));
    }

    [Fact]
    public void MissingChangedFields_HasNoTrackedChange()
    {
        Assert.False(HasTrackedChange(WithoutParameter(Reemission(), "ChangedFields")));
    }

    [Fact]
    public async Task Handler_MissingChangedFields_CompletesWithWarningNamingEntity()
    {
        var (actions, logger) = await RunHandler(
            WithoutParameter(Reemission(), "ChangedFields"), "test-missing-changed-fields");

        Assert.True(actions.Completed);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("test-missing-changed-fields", warning.Message);
        Assert.Contains(Entity, warning.Message);
    }

    // --- The create/update address route is unaffected ---

    [Fact]
    public void AddressRoute_TrackedFieldChanged_HasTrackedChange()
    {
        var node = Load("SourceEntities", $"{AddressEntity}.json");
        node["MessageName"] = "OnExternalUpdated";

        Assert.True(HasTrackedChange(WithChangedFields(node, "mserp_addressstreet")));
    }

    [Fact]
    public void AddressRoute_OnlyValidToChanged_HasNoTrackedChange()
    {
        var node = Load("SourceEntities", $"{AddressEntity}.json");
        node["MessageName"] = "OnExternalUpdated";

        Assert.False(HasTrackedChange(WithChangedFields(node, "mserp_addressvalidto")));
    }

    private static async Task<(RecordingMessageActions Actions, RecordingLogger Logger)> RunHandler(
        JsonNode payload, string messageId)
    {
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString(payload.ToJsonString()), messageId: messageId);
        var actions = new RecordingMessageActions();
        var logger = new RecordingLogger();

        // Collaborators are null: reaching the insert path would throw rather than pass silently.
        var handler = new QueueDataEventsHandler(
            new RecordingLoggerFactory(logger), OrchestrationRegistry.CreateDefault(), null!, null!, null!);

        await handler.Run(message, actions);

        return (actions, logger);
    }

    private sealed class RecordingMessageActions : ServiceBusMessageActions
    {
        public bool Completed { get; private set; }

        public override Task CompleteMessageAsync(ServiceBusReceivedMessage message, CancellationToken cancellationToken = default)
        {
            Completed = true;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLoggerFactory(RecordingLogger logger) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => logger;

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
