using ARM.Integration.Core.Model;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wilson.Integration.Orchestrations;
using Xunit;

namespace ARM.Integration.EIM.Tests;

public class PartyContactToCaptureTests
{
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
                    ["LogicalName"] = "mserp_dirpartycontactv3entity",
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
            ["PrimaryEntityName"] = "mserp_dirpartycontactv3entity",
            ["OperationId"] = Guid.NewGuid().ToString(),
            ["OperationCreatedOn"] = "2026-10-06T00:00:00Z",
            ["InputParameters"] = inputs,
        }.ToJsonString();
    }

    private static Dictionary<string, string> Attributes(string partyNumber, string partyRole) => new()
    {
        ["mserp_partynumber"] = partyNumber,
        ["mserp_electronicaddressid"] = "000188144",
        ["mserp_locator"] = "712-366-7777",
        ["mserp_wtc_partyrole"] = partyRole,
    };

    [Fact]
    public void Created_Customer_MapsCustomerPartyNumber()
    {
        var data = new TestablePartyContactToCapture().Map(
            Event("OnExternalCreated", Attributes("000029968", "Customer")));

        Assert.Equal("000029968", data.CustomerPartyNumber);
        Assert.Equal("", data.ContactPartyNumber);
        Assert.Equal("000188144", data.ElectronicAddressId);
    }

    [Fact]
    public void Created_Contact_MapsContactPartyNumber()
    {
        var data = new TestablePartyContactToCapture().Map(
            Event("OnExternalCreated", Attributes("000177803", "Contact")));

        Assert.Equal("000177803", data.ContactPartyNumber);
        Assert.Equal("", data.CustomerPartyNumber);
    }

    [Fact]
    public void Created_UnrecognisedRole_LeavesBothPartyNumbersBlank()
    {
        var data = new TestablePartyContactToCapture().Map(
            Event("OnExternalCreated", Attributes("000177900", "Both")));

        Assert.Equal("", data.ContactPartyNumber);
        Assert.Equal("", data.CustomerPartyNumber);
    }

    [Fact]
    public void Created_BlankRole_LeavesBothPartyNumbersBlank()
    {
        var data = new TestablePartyContactToCapture().Map(
            Event("OnExternalCreated", Attributes("000099999", "")));

        Assert.Equal("", data.ContactPartyNumber);
        Assert.Equal("", data.CustomerPartyNumber);
    }

    [Fact]
    public void Updated_RoleNotInChangedFields_StillMapsCustomerPartyNumber()
    {
        var data = new TestablePartyContactToCapture().Map(
            Event("OnExternalUpdated", Attributes("000029968", "Customer"), "mserp_locator"));

        Assert.Equal("000029968", data.CustomerPartyNumber);
        Assert.Equal("", data.ContactPartyNumber);
        Assert.Equal("712-366-7777", data.Locator);
    }

    private sealed class TestablePartyContactToCapture : PartyContact_To_Capture
    {
        public CapturePartyContactMessageContract Map(string payload) => ParseAndMap(payload).mappedData;
    }
}
