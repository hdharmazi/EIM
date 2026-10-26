using ARM.Integration.EIM;
using ARM.Integration.EIM.Retry;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wilson.Integration.Automation.Outbound;
using Wilson.Integration.Orchestrations;
using Xunit;

namespace Wilson.Integration.Tests;

/// <summary>
/// Address create/update messages take the address location id as their SourceId, and any
/// fallback to the operation id is logged.
/// </summary>
public class CustomerAddressSourceIdTests
{
    private const string Entity = "mserp_wtc_custpostaladdressv2entity";
    private const string KeyField = "mserp_addresslocationid";
    private const string LocationId = "000146087";
    private const string MessageId = "test-message";

    private static JsonNode Fixture() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "TestData", "SourceEntities", $"{Entity}.json")))!;

    private static JsonNode Target(JsonNode node) =>
        node["InputParameters"]!.AsArray().First(p => (string?)p!["key"] == "Target")!;

    private static JsonArray TargetAttributes(JsonNode node) =>
        Target(node)["value"]!["Attributes"]!.AsArray();

    private static EventContext Context(JsonNode node) =>
        JsonSerializer.Deserialize<EventContext>(node.ToJsonString())!;

    [Fact]
    public void Create_SourceIdIsAddressLocationId_NotOperationId()
    {
        var context = Context(Fixture());

        var sourceId = DataEventProcessor.ResolveSourceId(context, new RecordingLogger(), MessageId);

        Assert.Equal("OnExternalCreated", context.MessageName);
        Assert.Equal(LocationId, sourceId);
        Assert.NotEqual(context.OperationId, sourceId);
    }

    [Fact]
    public void Update_SourceIdIsAddressLocationId_NotOperationId()
    {
        var node = Fixture();
        node["MessageName"] = "OnExternalUpdated";
        var context = Context(node);

        var sourceId = DataEventProcessor.ResolveSourceId(context, new RecordingLogger(), MessageId);

        Assert.Equal(LocationId, sourceId);
        Assert.NotEqual(context.OperationId, sourceId);
    }

    [Fact]
    public void ForwardedEnvelope_SourceIdEqualsAddressLocationId()
    {
        var node = Fixture();
        var sourceId = DataEventProcessor.ResolveSourceId(Context(node), new RecordingLogger(), MessageId);

        var sut = new TestableUpsert();
        var (action, _, data, ts, _) = sut.Parse(node.ToJsonString());
        var envelope = Assert.IsType<CaptureMessageEnvelope<CaptureCustomerAddressMessageContract>>(
            sut.Envelope(data, sourceId, action, ts));

        Assert.Equal(LocationId, envelope.SourceId);
        Assert.Equal(envelope.SourceId, envelope.Data.AddressLocationId);
    }

    [Fact]
    public void DistinctOperationsOnOneAddress_ShareSourceId_ButNotDedupeKey()
    {
        var contexts = new[]
        {
            "00000000-0000-0000-0000-0000000000a1",
            "00000000-0000-0000-0000-0000000000a2",
            "00000000-0000-0000-0000-0000000000a3",
        }.Select(operationId =>
        {
            var node = Fixture();
            node["OperationId"] = operationId;
            return Context(node);
        }).ToList();

        var sourceIds = contexts
            .Select(c => DataEventProcessor.ResolveSourceId(c, new RecordingLogger(), MessageId))
            .ToList();

        // The handler's DedupeKey is context.OperationId: distinct per change, so none is a duplicate.
        Assert.All(sourceIds, id => Assert.Equal(LocationId, id));
        Assert.Equal(contexts.Count, contexts.Select(c => c.OperationId).Distinct().Count());
    }

    [Fact]
    public void MissingLocationId_FallsBackToOperationId_AndLogsWarning()
    {
        var node = Fixture();
        var attrs = TargetAttributes(node);
        attrs.Remove(attrs.First(a => (string?)a!["key"] == KeyField));

        AssertFallsBackWithWarning(Context(node));
    }

    [Fact]
    public void BlankLocationId_FallsBackToOperationId_AndLogsWarning()
    {
        var node = Fixture();
        TargetAttributes(node).First(a => (string?)a!["key"] == KeyField)!["value"] = "";

        AssertFallsBackWithWarning(Context(node));
    }

    [Fact]
    public void NoTargetParameter_FallsBackToOperationId_AndLogsWarning()
    {
        var node = Fixture();
        var inputs = node["InputParameters"]!.AsArray();
        inputs.Remove(inputs.First(p => (string?)p!["key"] == "Target"));

        AssertFallsBackWithWarning(Context(node));
    }

    [Fact]
    public void TargetValueNull_FallsBackToOperationId_AndLogsWarning()
    {
        var node = Fixture();
        Target(node)["value"] = null;

        AssertFallsBackWithWarning(Context(node));
    }

    [Fact]
    public void TargetWithoutAttributes_FallsBackToOperationId_AndLogsWarning()
    {
        var node = Fixture();
        Target(node)["value"]!.AsObject().Remove("Attributes");

        AssertFallsBackWithWarning(Context(node));
    }

    [Fact]
    public void ResolvedKey_LogsNoWarning()
    {
        var logger = new RecordingLogger();

        DataEventProcessor.ResolveSourceId(Context(Fixture()), logger, MessageId);

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void ParseAndMap_NoTargetAttributes_ThrowsJsonException()
    {
        var node = Fixture();
        var inputs = node["InputParameters"]!.AsArray();
        inputs.Remove(inputs.First(p => (string?)p!["key"] == "Target"));

        var ex = Assert.Throws<JsonException>(() => new TestableUpsert().Parse(node.ToJsonString()));

        // Terminal and positively classified, so the row fails visibly without a retry.
        var verdict = new DefaultFailureClassifier(new RetryOptions()).ClassifyVerdict(ex);
        Assert.True(verdict.IsTerminal);
        Assert.Equal(TerminalReason.Rejected, verdict.Reason);
    }

    private static void AssertFallsBackWithWarning(EventContext context)
    {
        var logger = new RecordingLogger();

        var sourceId = DataEventProcessor.ResolveSourceId(context, logger, MessageId);

        Assert.Equal(context.OperationId, sourceId);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(Entity, warning.Message);
        Assert.Contains(KeyField, warning.Message);
        Assert.Contains(context.OperationId, warning.Message);
    }

    private sealed class TestableUpsert : CustomerAddress_To_Capture
    {
        public (string, bool, CaptureCustomerAddressMessageContract, string?, string?) Parse(string payload)
            => ParseAndMap(payload);

        public object Envelope(CaptureCustomerAddressMessageContract data, string sourceId, string action, string? ts)
            => CreateEnvelope(data, sourceId, action, ts);
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
