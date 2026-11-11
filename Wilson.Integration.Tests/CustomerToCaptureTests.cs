using ARM.Integration.EIM;
using ARM.Integration.EIM.Utilities;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wilson.Integration.Automation.Outbound;
using Wilson.Integration.Orchestrations;
using Xunit;

namespace Wilson.Integration.Tests;

public class CustomerToCaptureTests
{
    private const string EntityLogicalName = "mserp_wtc_custcustomerentity";

    private static string Event(string messageName, Dictionary<string, string> attributes, params string[] changedFields)
    {
        var attrs = new JsonArray(attributes
            .Select(kv => (JsonNode)new JsonObject { ["key"] = kv.Key, ["value"] = kv.Value })
            .ToArray());

        var inputs = new JsonArray
        {
            new JsonObject
            {
                ["key"] = "Target",
                ["value"] = new JsonObject
                {
                    ["Attributes"] = attrs,
                    ["Id"] = Guid.NewGuid().ToString(),
                    ["LogicalName"] = EntityLogicalName,
                },
            },
            new JsonObject
            {
                ["key"] = "ChangedFields",
                ["value"] = new JsonArray(changedFields.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()),
            },
        };

        return new JsonObject
        {
            ["MessageName"] = messageName,
            ["PrimaryEntityName"] = EntityLogicalName,
            ["OperationId"] = Guid.NewGuid().ToString(),
            ["OperationCreatedOn"] = "2026-11-11T00:00:00Z",
            ["InputParameters"] = inputs,
        }.ToJsonString();
    }

    private static Dictionary<string, string> Attributes(string dotNumber = "DOT987", string taxStatus = "Exempt") => new()
    {
        ["mserp_dataareaid"] = "wtc",
        ["mserp_customeraccount"] = "AR-082797",
        ["mserp_organizationname"] = "Example Customer",
        ["mserp_namealias"] = "Example",
        ["mserp_customergroupid"] = "RETAIL",
        ["mserp_employeeresponsiblenumber"] = "ARM001",
        ["mserp_onholdstatus"] = "200000000",
        ["mserp_partynumber"] = "000000101",
        ["mserp_dotnumber"] = dotNumber,
        ["mserp_taxstatus"] = taxStatus,
    };

    private static (string action, bool hasTrackedChanges, JsonObject envelope) Forward(string payload)
    {
        var sut = new TestableCustomerToCapture();
        var (action, hasTracked, data, ts, _) = sut.Parse(payload);
        var context = JsonSerializer.Deserialize<EventContext>(payload)!;
        var attributes = D365FSCEventParser.GetTargetAttributes(context.InputParameters)!;
        var sourceId = BusinessKeyResolver.GetBusinessKey(context.PrimaryEntityName, attributes)!;
        var envelope = sut.Envelope(data, sourceId, action, ts);
        return (action, hasTracked, JsonSerializer.SerializeToNode(envelope)!.AsObject());
    }

    [Fact]
    public void EntityRoute_UsesNewWtcCustomerEntityOnly()
    {
        Assert.True(DataEventProcessor.EntityRoutes.TryGetValue(EntityLogicalName, out var route));
        Assert.Equal("Customer_To_Capture", route.Type);
        Assert.Equal("D365ToCaptureQueue", route.QueueSetting);
        Assert.DoesNotContain("mserp_custcustomerv3entity", DataEventProcessor.EntityRoutes.Keys);
    }

    [Fact]
    public void Update_OnHoldStatusIsTracked()
    {
        var context = JsonSerializer.Deserialize<EventContext>(
            Event("OnExternalUpdated", Attributes(), "mserp_onholdstatus"))!;

        Assert.True(DataEventProcessor.HasTrackedChange(context, out _));
    }

    [Fact]
    public void Update_UnmappedWtcFieldIsNotTracked()
    {
        var context = JsonSerializer.Deserialize<EventContext>(
            Event("OnExternalUpdated", Attributes(), "mserp_wtc_consignment"))!;

        Assert.False(DataEventProcessor.HasTrackedChange(context, out _));
    }

    [Fact]
    public void CreatedCustomer_MapsBusinessKeyDotNumberAndTaxStatus_WithoutDepositRequired()
    {
        var (action, hasTracked, envelope) = Forward(Event("OnExternalCreated", Attributes()));
        var data = envelope["Data"]!.AsObject();

        Assert.Equal("New", action);
        Assert.True(hasTracked);
        Assert.Equal("AR-082797", (string?)envelope["SourceId"]);
        Assert.Equal("DOT987", (string?)data["DOTNumber"]);
        Assert.Equal("Exempt", (string?)data["TaxStatus"]);
        Assert.False(data.ContainsKey("DepositRequired"));
    }

    [Fact]
    public void CreatedCustomer_WithEmptyDotAndTaxValues_DoesNotUsePlaceholders()
    {
        var (_, _, envelope) = Forward(Event("OnExternalCreated", Attributes(dotNumber: "", taxStatus: "")));
        var data = envelope["Data"]!.AsObject();

        Assert.Equal("", (string?)data["DOTNumber"]);
        Assert.Equal("", (string?)data["TaxStatus"]);
        Assert.DoesNotContain("X1234", data.ToJsonString());
        Assert.DoesNotContain("30CHARSTATUS", data.ToJsonString());
        Assert.False(data.ContainsKey("DepositRequired"));
    }

    [Fact]
    public void Migration016_SeedsAndRemovesCustomerRoutes()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "Migrations", "016_replace_customer_route.sql");
        var sql = File.ReadAllText(path);

        Assert.Contains("N'BAT'", sql);
        Assert.Contains("N'DEV'", sql);
        Assert.Contains("N'DEV2'", sql);
        Assert.Contains("N'TEST'", sql);
        Assert.Contains("N'PRD'", sql);
        Assert.Contains("N'mserp_wtc_custcustomerentity'", sql);
        Assert.Contains("N'Customer_To_Capture'", sql);
        Assert.Contains("WHEN NOT MATCHED BY TARGET THEN", sql);
        Assert.Contains("N'Migrations.016_replace_customer_route.sql'", sql);
        Assert.Contains("THROW 50016", sql);
        Assert.Contains("DELETE FROM dbo.ROUTETABLE", sql);
        Assert.Contains("N'mserp_custcustomerv3entity'", sql);
    }

    private sealed class TestableCustomerToCapture : Customer_To_Capture
    {
        public (string, bool, CaptureCustomerMessageContract, string?, string?) Parse(string payload)
            => ParseAndMap(payload);

        public object Envelope(CaptureCustomerMessageContract data, string sourceId, string action, string? timestamp)
            => CreateEnvelope(data, sourceId, action, timestamp);
    }
}
