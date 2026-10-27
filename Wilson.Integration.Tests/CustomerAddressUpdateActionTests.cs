using ARM.Integration.EIM;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wilson.Integration.Automation.Outbound;
using Wilson.Integration.Orchestrations;
using Xunit;

namespace Wilson.Integration.Tests;

/// <summary>
/// D365 edits a customer address by creating a new date-effective version, which Dataverse reports
/// as <c>OnExternalCreated</c>. The entity's <c>mserp_wtc_haspriorversion</c> marker turns such a
/// creation into an <c>Update</c> for Capture; without it the action stays <c>New</c>.
/// </summary>
public class CustomerAddressUpdateActionTests
{
    private const string Marker = "mserp_wtc_haspriorversion";
    private const string LocationId = "000312031";
    private const int Yes = 200000001;
    private const int No = 200000000;

    private static JsonNode Fixture() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "TestData", "CustomerAddress.NewVersion.OnExternalCreated.json")))!;

    private static JsonNode DeleteFixture() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "TestData", "CustomerAddressDeleted.OnExternalUpdated.json")))!;

    private static JsonArray Attributes(JsonNode node) =>
        node["InputParameters"]!.AsArray()
            .First(p => (string?)p!["key"] == "Target")!["value"]!["Attributes"]!.AsArray();

    private static JsonNode WithMarker(JsonNode node, JsonNode? value)
    {
        var attrs = Attributes(node);
        var existing = attrs.FirstOrDefault(a => (string?)a!["key"] == Marker);
        if (existing is null)
            attrs.Add(new JsonObject { ["key"] = Marker, ["value"] = value });
        else
            existing["value"] = value;
        return node;
    }

    private static JsonNode WithoutMarker(JsonNode node)
    {
        var attrs = Attributes(node);
        attrs.Remove(attrs.First(a => (string?)a!["key"] == Marker));
        return node;
    }

    private static JsonNode AsUpdate(JsonNode node, params string[] changedFields)
    {
        node["MessageName"] = "OnExternalUpdated";
        node["InputParameters"]!.AsArray().Add(new JsonObject
        {
            ["key"] = "ChangedFields",
            ["value"] = new JsonArray(changedFields.Select(f => (JsonNode?)f).ToArray()),
        });
        return node;
    }

    private static JsonNode OptionSet(int value) => new JsonObject
    {
        ["__type"] = "OptionSetValue:http://schemas.microsoft.com/xrm/2011/Contracts",
        ["Value"] = value,
    };

    private static string ActionFor(JsonNode node) => new TestableUpsert().Parse(node.ToJsonString()).Item1;

    [Fact]
    public void PriorVersionField_IsTheDataverseAttributeName()
    {
        Assert.Equal(Marker, CustomerAddress_To_Capture.PriorVersionField);
    }

    [Fact]
    public void SupersedingVersion_IsForwardedAsUpdate_OnTheLocationId()
    {
        var payload = Fixture().ToJsonString();
        var sourceId = DataEventProcessor.ResolveSourceId(
            JsonSerializer.Deserialize<EventContext>(payload)!, NullLogger.Instance, "test-message");

        var sut = new TestableUpsert();
        var (action, _, data, ts, skip) = sut.Parse(payload);

        var envelope = Assert.IsType<CaptureMessageEnvelope<CaptureCustomerAddressMessageContract>>(
            sut.Envelope(data, sourceId, action, ts));

        Assert.Equal("Update", envelope.Action);
        Assert.Equal("CustomerAddress", envelope.Entity);
        Assert.Equal(LocationId, envelope.SourceId);
        Assert.Equal(LocationId, envelope.Data.AddressLocationId);
        Assert.Null(skip);
    }

    [Fact]
    public void MarkerAsInteger_IsUpdate()
    {
        Assert.Equal("Update", ActionFor(WithMarker(Fixture(), JsonValue.Create(Yes))));
    }

    [Fact]
    public void MarkerAsString_IsUpdate()
    {
        Assert.Equal("Update", ActionFor(WithMarker(Fixture(), JsonValue.Create(Yes.ToString()))));
    }

    [Fact]
    public void MarkerNo_StaysNew()
    {
        Assert.Equal("New", ActionFor(WithMarker(Fixture(), OptionSet(No))));
    }

    [Fact]
    public void MarkerAbsent_StaysNew()
    {
        var sut = new TestableUpsert();

        var (action, hasTracked, _, _, skip) = sut.Parse(WithoutMarker(Fixture()).ToJsonString());

        Assert.Equal("New", action);
        Assert.True(hasTracked);
        Assert.Null(skip);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("maybe")]
    public void UnexpectedMarker_StaysNew_WithoutFailing(string? value)
    {
        var node = WithMarker(Fixture(), value is null ? null : JsonValue.Create(value));

        var (action, hasTracked, _, _, skip) = new TestableUpsert().Parse(node.ToJsonString());

        Assert.Equal("New", action);
        Assert.True(hasTracked);
        Assert.Null(skip);
    }

    [Theory]
    [InlineData(Yes)]
    [InlineData(No)]
    public void GenuineUpdate_IsUnaffectedByMarker(int marker)
    {
        var node = AsUpdate(WithMarker(Fixture(), OptionSet(marker)), "mserp_addresslocationroles");

        var (action, hasTracked, _, _, _) = new TestableUpsert().Parse(node.ToJsonString());

        Assert.Equal("Update", action);
        Assert.True(hasTracked);
    }

    [Fact]
    public void VersionBasedUpdate_CarriesTheFullAddress_AndIsTracked()
    {
        var (action, hasTracked, data, ts, _) = new TestableUpsert().Parse(Fixture().ToJsonString());

        Assert.Equal("Update", action);
        // Not completed as "not tracked fields": every attribute of a creation counts as changed.
        Assert.True(hasTracked);
        Assert.Equal("wtc", data.DataAreaId);
        Assert.Equal("100300", data.CustomerAccount);
        Assert.Equal(LocationId, data.AddressLocationId);
        Assert.Equal("No", data.IsPrimary);
        Assert.Equal("Business", data.AddressLocationRoles);
        Assert.Equal("New Address", data.AddressDescription);
        Assert.Equal("Holtsville", data.AddressCity);
        Assert.Equal("00501", data.AddressZipCode);
        Assert.Equal("123 New Address # 2", data.AddressStreet);
        Assert.Equal("USA", data.AddressCountryRegionId);
        Assert.Equal("NY", data.AddressState);
        Assert.Equal("/Date(1793027444000+0000)/", ts);
    }

    [Theory]
    [InlineData("OnExternalCreated")]
    [InlineData("OnExternalUpdated")]
    public void DeleteEntity_IgnoresMarker(string messageName)
    {
        var node = WithMarker(DeleteFixture(), OptionSet(Yes));
        node["MessageName"] = messageName;

        var (action, _, _, _, skip) = new TestableDelete().Parse(node.ToJsonString());

        Assert.Equal("Delete", action);
        Assert.Null(skip);
    }

    [Fact]
    public void Engine_DoesNotNameTheMarker()
    {
        var root = RepositoryPaths.Root();
        var engine = Path.Combine(root, "ARM.Integration.EIM");
        var sources = Directory.EnumerateFiles(engine, "*.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(engine, f).Split(Path.DirectorySeparatorChar)
                .Any(part => RepositoryPaths.SkippedDirectories.Contains(part, StringComparer.OrdinalIgnoreCase)))
            .ToList();

        Assert.NotEmpty(sources);
        Assert.DoesNotContain(sources, f => File.ReadAllText(f).Contains(Marker, StringComparison.OrdinalIgnoreCase));
        // The rule lives with the client orchestration instead.
        Assert.Contains(Marker, File.ReadAllText(Path.Combine(
            root, "Wilson.Integration.Orchestrations", "Orchestrations", "CustomerAddress_To_Capture.cs")));
    }

    private sealed class TestableUpsert : CustomerAddress_To_Capture
    {
        public (string, bool, CaptureCustomerAddressMessageContract, string?, string?) Parse(string payload)
            => ParseAndMap(payload);

        public object Envelope(CaptureCustomerAddressMessageContract data, string sourceId, string action, string? ts)
            => CreateEnvelope(data, sourceId, action, ts);
    }

    private sealed class TestableDelete : CustomerAddressDelete_To_Capture
    {
        public (string, bool, CaptureCustomerAddressMessageContract, string?, string?) Parse(string payload)
            => ParseAndMap(payload);
    }
}
