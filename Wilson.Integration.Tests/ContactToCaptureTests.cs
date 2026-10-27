using System.Text.Json;
using System.Text.Json.Nodes;
using Wilson.Integration.Orchestrations;
using Xunit;

namespace Wilson.Integration.Tests;

public class ContactToCaptureTests
{
    private const string EntityLogicalName = "mserp_smmcontactpersonv2entity";

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
            ["OperationCreatedOn"] = "2026-10-27T00:00:00Z",
            ["InputParameters"] = inputs,
        }.ToJsonString();
    }

    // The contact from bug #5261: blank middle name, first name being updated.
    private static Dictionary<string, string> Attributes(string middleName = "", string lastName = "Smith") => new()
    {
        ["mserp_dataareaid"] = "wtc",
        ["mserp_contactpersonid"] = "000060",
        ["mserp_firstname"] = "First Name Update",
        ["mserp_middlename"] = middleName,
        ["mserp_lastname"] = lastName,
        ["mserp_associatedpartynumber"] = "000029968",
        ["mserp_wtc_associatedcustomeraccount"] = "100300",
        ["mserp_contactpersonpartynumber"] = "000181501",
    };

    /// <summary>Maps the event and serializes the envelope the way BaseForwardOrchestration does.</summary>
    private static (string action, bool hasTrackedChanges, JsonObject envelope) Forward(string payload)
    {
        var sut = new TestableContactToCapture();
        var (action, hasTracked, data, ts, _) = sut.Parse(payload);
        var envelope = sut.Envelope(data, "000060", action, ts);
        var json = JsonNode.Parse(JsonSerializer.Serialize((object)envelope))!.AsObject();
        return (action, hasTracked, json);
    }

    private static JsonObject Data(JsonObject envelope) => envelope["Data"]!.AsObject();

    [Fact]
    public void Update_ChangingOnlyFirstName_SendsAllEightFields()
    {
        var (_, _, envelope) = Forward(Event("OnExternalUpdated", Attributes(), "mserp_firstname"));
        var data = Data(envelope);

        Assert.Equal("Update", (string?)envelope["Action"]);
        Assert.Equal(8, data.Count);
        Assert.Equal("wtc", (string?)data["dataAreaId"]);
        Assert.Equal("000060", (string?)data["ContactPersonId"]);
        Assert.Equal("First Name Update", (string?)data["FirstName"]);
        Assert.Equal("", (string?)data["MiddleName"]);
        Assert.Equal("Smith", (string?)data["LastName"]);
        Assert.Equal("000029968", (string?)data["AssociatedPartyNumber"]);
        Assert.Equal("100300", (string?)data["AssociatedCustomerAccount"]);
        Assert.Equal("000181501", (string?)data["ContactPersonPartyNumber"]);
    }

    [Fact]
    public void Update_EmptyNameFields_AreSentAsEmptyStrings()
    {
        var (_, _, envelope) = Forward(
            Event("OnExternalUpdated", Attributes(middleName: "", lastName: ""), "mserp_firstname"));
        var data = Data(envelope);

        Assert.True(data.ContainsKey("MiddleName"));
        Assert.True(data.ContainsKey("LastName"));
        Assert.Equal("", (string?)data["MiddleName"]);
        Assert.Equal("", (string?)data["LastName"]);
    }

    [Theory]
    [InlineData("OnExternalCreated")]
    [InlineData("OnExternalUpdated")]
    public void AbsentMiddleNameAttribute_IsSentAsEmptyString(string messageName)
    {
        var attributes = Attributes();
        attributes.Remove("mserp_middlename");

        var (_, _, envelope) = Forward(Event(messageName, attributes, "mserp_firstname"));
        var data = Data(envelope);

        Assert.True(data.ContainsKey("MiddleName"));
        Assert.Equal("", (string?)data["MiddleName"]);
    }

    [Fact]
    public void NewAndUpdate_OfSameTarget_CarryTheSameData()
    {
        var (newAction, _, created) = Forward(Event("OnExternalCreated", Attributes()));
        var (updateAction, _, updated) = Forward(Event("OnExternalUpdated", Attributes(), "mserp_firstname"));

        Assert.Equal("New", newAction);
        Assert.Equal("Update", updateAction);
        Assert.True(
            JsonNode.DeepEquals(Data(created), Data(updated)),
            $"New Data {Data(created).ToJsonString()} differs from Update Data {Data(updated).ToJsonString()}");
    }

    [Theory]
    [InlineData("OnExternalCreated")]
    [InlineData("OnExternalUpdated")]
    public void UnmappedContractFields_StayOmitted(string messageName)
    {
        var (_, _, envelope) = Forward(Event(messageName, Attributes(), "mserp_firstname"));
        var data = Data(envelope);

        Assert.False(data.ContainsKey("AssociatedContactNumber"));
        Assert.False(data.ContainsKey("AssociatedContactType"));
    }

    [Fact]
    public void Update_OfUnmappedAttributeOnly_HasNoTrackedChanges()
    {
        var (_, hasTracked, _) = Forward(
            Event("OnExternalUpdated", Attributes(), "mserp_associatedcontactnumber"));

        Assert.False(hasTracked);
    }

    [Fact]
    public void Update_OfMappedAttribute_HasTrackedChanges()
    {
        var (_, hasTracked, _) = Forward(Event("OnExternalUpdated", Attributes(), "mserp_lastname"));

        Assert.True(hasTracked);
    }

    private sealed class TestableContactToCapture : Contact_To_Capture
    {
        public (string, bool, CaptureContactMessageContract, string?, string?) Parse(string payload)
            => ParseAndMap(payload);

        public object Envelope(CaptureContactMessageContract data, string sourceId, string action, string? ts)
            => CreateEnvelope(data, sourceId, action, ts);
    }
}
