using ARM.Integration.EIM.Customer;
using ARM.Integration.EIM.Orchestration;
using ARM.Integration.EIM.Routing;
using ARM.Integration.EIM.Utilities;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Wilson.Integration.Orchestrations;
using Xunit;

namespace ARM.Integration.EIM.Tests;

public class CustomerAddressDeleteToCaptureTests
{
    private const string Entity = "mserp_wtc_custpostaladdressdeletedv2entity";
    private const string OrchestrationType = "CustomerAddressDelete_To_Capture";

    private static string Fixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "CustomerAddressDeleted.OnExternalUpdated.json"));

    private static string WithMessageName(string payload, string messageName)
    {
        var node = JsonNode.Parse(payload)!;
        node["MessageName"] = messageName;
        return node.ToJsonString();
    }

    private static string WithAttribute(string payload, string key, JsonNode? value)
    {
        var node = JsonNode.Parse(payload)!;
        var attrs = node["InputParameters"]!.AsArray()
            .First(p => (string?)p!["key"] == "Target")!["value"]!["Attributes"]!.AsArray();
        attrs.First(a => (string?)a!["key"] == key)!["value"] = value;
        return node.ToJsonString();
    }

    private static JsonNode OptionSet(int value) => new JsonObject
    {
        ["__type"] = "OptionSetValue:http://schemas.microsoft.com/xrm/2011/Contracts",
        ["Value"] = value,
    };

    [Fact]
    public void UpdateShapedEvent_ProducesDeleteEnvelopeOnCustomerAddress()
    {
        var sut = new TestableDelete();
        var (action, hasTracked, data, ts, skip) = sut.Parse(Fixture());

        var envelope = Assert.IsType<CaptureMessageEnvelope<CaptureCustomerAddressMessageContract>>(
            sut.Envelope(data, "000146089", action, ts));

        Assert.Equal("Delete", envelope.Action);
        Assert.Equal("CustomerAddress", envelope.Entity);
        Assert.True(hasTracked);
        Assert.Null(skip);
    }

    [Fact]
    public void MapsEveryAddressField_EvenWhenChangedFieldsOmitsThem()
    {
        var (_, _, data, ts, _) = new TestableDelete().Parse(Fixture());

        Assert.Equal("wtc", data.DataAreaId);
        Assert.Equal("C000123", data.CustomerAccount);
        Assert.Equal("000146089", data.AddressLocationId);
        Assert.Equal("Yes", data.IsPrimary);
        Assert.Equal("Business", data.AddressLocationRoles);
        Assert.Equal("Main yard", data.AddressDescription);
        Assert.Equal("Sioux City", data.AddressCity);
        Assert.Equal("51101", data.AddressZipCode);
        Assert.Equal("100 Example St", data.AddressStreet);
        Assert.Equal("USA", data.AddressCountryRegionId);
        Assert.Equal("IA", data.AddressState);
        Assert.Equal("/Date(1760889600000+0000)/", ts);
    }

    [Fact]
    public void SourceId_IsAddressLocationId_NotOperationId()
    {
        var payload = Fixture();
        var context = JsonSerializer.Deserialize<EventContext>(payload)!;
        var attrs = D365FSCEventParser.GetTargetAttributes(context.InputParameters);

        var sourceId = BusinessKeyResolver.GetBusinessKey(context.PrimaryEntityName, attrs);

        Assert.Equal("000146089", sourceId);
        Assert.NotEqual(context.OperationId, sourceId);
    }

    [Fact]
    public void IsDeletedNo_IsSkippedWithReasonNamingObservedValue()
    {
        var payload = WithAttribute(Fixture(), "mserp_wtc_isdeleted", OptionSet(200000000));

        var (_, _, _, _, skip) = new TestableDelete().Parse(payload);

        Assert.NotNull(skip);
        Assert.Contains("200000000", skip);
    }

    [Theory]
    [InlineData("OnExternalCreated")]
    [InlineData("OnExternalDeleted")]
    [InlineData("OnExternalUpdated")]
    public void AnyMessageName_YieldsDelete(string messageName)
    {
        var (action, _, _, _, _) = new TestableDelete().Parse(WithMessageName(Fixture(), messageName));

        Assert.Equal("Delete", action);
    }

    [Fact]
    public void ExistingAddressRoute_StillDerivesActionFromMessageName()
    {
        var payload = WithMessageName(Fixture(), "OnExternalUpdated");

        var (action, _, _, _, skip) = new TestableUpsert().Parse(payload);

        Assert.Equal("Update", action);
        Assert.Null(skip);
    }

    [Fact]
    public async Task Handler_RejectsBlankAddressLocationId_BeforeInsert()
    {
        var payload = WithAttribute(Fixture(), "mserp_addresslocationid", JsonValue.Create(""));
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString(payload), messageId: "test-blank-key");
        var actions = new RecordingMessageActions();

        // Collaborators are null: reaching the insert path would throw rather than pass silently.
        var handler = new QueueDataEventsHandler(
            NullLoggerFactory.Instance, OrchestrationRegistry.CreateDefault(), null!, null!, null!);

        await handler.Run(message, actions);

        Assert.True(actions.Completed);
    }

    [Fact]
    public void HandlerRoute_MatchesMigration014Seed()
    {
        var sql = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "TestData", "Migrations", "014_seed_customer_address_delete_route.sql"));

        var seededEntity = Regex.Match(sql, @"N'(mserp_[a-z0-9_]+)'\s+AS SOURCEENTITY").Groups[1].Value;
        var seededType = Regex.Match(sql, @"N'([A-Za-z_]+)'\s+AS ORCHESTRATIONTYPE").Groups[1].Value;

        Assert.Equal(Entity, seededEntity);
        Assert.Equal(OrchestrationType, seededType);
        Assert.True(QueueDataEventsHandler.EntityRoutes.TryGetValue(seededEntity, out var route));
        Assert.Equal(seededType, route.Type);
        Assert.True(seededEntity.Length <= 50);
    }

    [Fact]
    public void RouteRegistryValidator_AcceptsNewRoute()
    {
        var routes = new[] { "BAT", "DEV", "DEV2", "TEST" }.ToDictionary(
            env => new RouteKey(env, "D365FSC", Entity),
            _ => new RouteDefinition(OrchestrationType, true, PausePolicy.Hold));

        RouteRegistryValidator.Validate(routes, OrchestrationRegistry.CreateDefault());
    }

    private sealed class TestableDelete : CustomerAddressDelete_To_Capture
    {
        public (string, bool, CaptureCustomerAddressMessageContract, string?, string?) Parse(string payload)
            => ParseAndMap(payload);

        public object Envelope(CaptureCustomerAddressMessageContract data, string sourceId, string action, string? ts)
            => CreateEnvelope(data, sourceId, action, ts);
    }

    private sealed class TestableUpsert : CustomerAddress_To_Capture
    {
        public (string, bool, CaptureCustomerAddressMessageContract, string?, string?) Parse(string payload)
            => ParseAndMap(payload);
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
}
