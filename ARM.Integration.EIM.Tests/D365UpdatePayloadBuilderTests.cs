using Wilson.Integration.Orchestrations;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ARM.Integration.Core.Helpers;
using ARM.Integration.EIM.Orchestration;
using ARM.Integration.EIM.Utilities;
using Xunit;

namespace ARM.Integration.EIM.Tests;

public class D365UpdatePayloadBuilderTests
{
    private static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;

    /// <summary>Extracts the field names from an <see cref="EntityKey"/>'s rendered form.</summary>
    private static IEnumerable<string> KeyFieldNames(EntityKey key) =>
        key.ToString().Split(',').Select(s => s[..s.IndexOf('=')]);

    private static CustomersEntity SampleCustomer() => new()
    {
        DataAreaId = "WTC",
        CustomerAccount = "C-001",
        PartyNumber = "000123",
        SalesCurrencyCode = "USD",
        OrganizationName = "Acme",
        NameAlias = "ACME",
        CustomerGroupId = "10",
    };

    // 5.2 — marked properties are removed, unmarked non-null properties survive.
    [Fact]
    public void BuildUpdatePayload_RemovesMarkedFields_AndRetainsUnmarkedFields()
    {
        var (json, removed) = D365UpdatePayloadBuilder.BuildUpdatePayload(SampleCustomer());
        var payload = Parse(json);

        Assert.False(payload.ContainsKey("dataAreaId"));
        Assert.False(payload.ContainsKey("CustomerAccount"));
        Assert.False(payload.ContainsKey("PartyNumber"));
        Assert.False(payload.ContainsKey("SalesCurrencyCode"));

        Assert.Equal("Acme", (string?)payload["OrganizationName"]);
        Assert.Equal("ACME", (string?)payload["NameAlias"]);
        Assert.Equal("10", (string?)payload["CustomerGroupId"]);

        Assert.Equal(
            ["CustomerAccount", "PartyNumber", "SalesCurrencyCode", "dataAreaId"],
            removed.OrderBy(r => r, StringComparer.Ordinal).ToArray());
    }

    // 5.2 — null properties remain absent, as before.
    [Fact]
    public void BuildUpdatePayload_OmitsNullProperties()
    {
        var (json, _) = D365UpdatePayloadBuilder.BuildUpdatePayload(new CustomersEntity
        {
            CustomerAccount = "C-001",
            OrganizationName = "Acme",
        });

        Assert.False(Parse(json).ContainsKey("NameAlias"));
    }

    // 5.2 — an entity with no exclusions is passed through untouched.
    [Fact]
    public void BuildUpdatePayload_WithNoExclusions_ReportsNothingRemoved()
    {
        var (json, removed) = D365UpdatePayloadBuilder.BuildUpdatePayload(new NoExclusions { Value = "x" });

        Assert.Empty(removed);
        Assert.Equal("x", (string?)Parse(json)["Value"]);
    }

    // 5.3 — no orchestration leaks its key fields into the update body.
    [Theory]
    [MemberData(nameof(OrchestrationCases))]
    public void BuildUpdatePayload_ContainsNoKeyField(string name, string[] keyFields, string json)
    {
        var payload = Parse(json);

        foreach (var field in keyFields)
        {
            Assert.False(
                payload.Any(p => string.Equals(p.Key, field, StringComparison.OrdinalIgnoreCase)),
                $"{name}: key field '{field}' must not appear in the update payload.");
        }
    }

    public static TheoryData<string, string[], string> OrchestrationCases()
    {
        var data = new TheoryData<string, string[], string>();

        var customer = SampleCustomer();
        data.Add(
            nameof(Customer_To_D365FSC),
            KeyFieldNames(new TestableCustomer().Key(customer)).ToArray(),
            D365UpdatePayloadBuilder.BuildUpdatePayload(customer).Json);

        var address = new CustomerPostalAddressesEntity
        {
            DataAreaId = "WTC",
            CustomerAccount = "C-001",
            AddressLocationId = "LOC-1",
            AddressCity = "Dallas",
        };
        data.Add(
            nameof(CustomerAddress_To_D365FSC),
            KeyFieldNames(new TestableCustomerAddress().Key(address)).ToArray(),
            D365UpdatePayloadBuilder.BuildUpdatePayload(address).Json);

        var contact = new ContactPersonEntity
        {
            DataAreaId = "WTC",
            ContactPersonId = "CP-1",
            FirstName = "Ada",
        };
        data.Add(
            nameof(Contact_To_D365FSC),
            KeyFieldNames(new TestableContact().Key(contact)).ToArray(),
            D365UpdatePayloadBuilder.BuildUpdatePayload(contact).Json);

        var partyContact = new PartyContactsEntity
        {
            PartyNumber = "000123",
            ElectronicAddressId = "EA-1",
            Locator = "ada@example.com",
            Type = "Email",
        };
        data.Add(
            nameof(PartyContact_To_D365FSC),
            KeyFieldNames(new TestablePartyContact().Key(partyContact)).ToArray(),
            D365UpdatePayloadBuilder.BuildUpdatePayload(partyContact).Json);

        return data;
    }

    // 5.3 — PartyContact's non-key read-only field is still excluded after the override was removed.
    [Fact]
    public void BuildUpdatePayload_PartyContact_ExcludesType()
    {
        var (json, _) = D365UpdatePayloadBuilder.BuildUpdatePayload(new PartyContactsEntity
        {
            PartyNumber = "000123",
            ElectronicAddressId = "EA-1",
            Locator = "ada@example.com",
            Type = "Email",
        });

        var payload = Parse(json);
        Assert.False(payload.ContainsKey("Type"));
        Assert.Equal("ada@example.com", (string?)payload["Locator"]);
    }

    // 5.4 — filtering does not mutate the entity, so the key still resolves from the full instance.
    [Fact]
    public void BuildUpdatePayload_DoesNotMutateEntity_SoKeyStillResolves()
    {
        var customer = SampleCustomer();

        D365UpdatePayloadBuilder.BuildUpdatePayload(customer);
        var key = new TestableCustomer().Key(customer);

        Assert.Equal("WTC", customer.DataAreaId);
        Assert.Equal("C-001", customer.CustomerAccount);
        Assert.Equal("dataAreaId='WTC',CustomerAccount='C-001'", key.ToString());
    }

    // 5.5 — the Create branch serializes the entity unfiltered.
    [Fact]
    public void CreatePayload_RetainsExcludedFields()
    {
        var payload = Parse(JsonSerializer.Serialize(SampleCustomer()));

        Assert.Equal("USD", (string?)payload["SalesCurrencyCode"]);
        Assert.Equal("000123", (string?)payload["PartyNumber"]);
        Assert.Equal("WTC", (string?)payload["dataAreaId"]);
        Assert.Equal("C-001", (string?)payload["CustomerAccount"]);
    }

    // 5.6 — exclusion matches the wire name, not the CLR property name.
    [Fact]
    public void BuildUpdatePayload_MatchesOnJsonPropertyName()
    {
        var (json, removed) = D365UpdatePayloadBuilder.BuildUpdatePayload(
            new WireNameFixture { DotNumber = "12345", Keep = "kept" });

        Assert.Equal(["DOTNumber"], removed);
        Assert.False(Parse(json).ContainsKey("DOTNumber"));
        Assert.Equal("kept", (string?)Parse(json)["Keep"]);
    }

    // 5.6 — excluded names are resolved from the wire name for reflection lookups too.
    [Fact]
    public void GetExcludedFieldNames_UsesWireNames()
    {
        Assert.Equal(["DOTNumber"], D365UpdatePayloadBuilder.GetExcludedFieldNames(typeof(WireNameFixture)));
    }

    private sealed class WireNameFixture
    {
        [JsonPropertyName("DOTNumber")]
        [D365ExcludeOnUpdate("Test fixture: verifies wire-name matching.")]
        public string? DotNumber { get; set; }

        [JsonPropertyName("Keep")]
        public string? Keep { get; set; }
    }

    private sealed class NoExclusions
    {
        [JsonPropertyName("Value")]
        public string? Value { get; set; }
    }

    private sealed class TestableCustomer : Customer_To_D365FSC
    {
        public EntityKey Key(CustomersEntity e) => BuildEntityKey(e);
    }

    private sealed class TestableCustomerAddress : CustomerAddress_To_D365FSC
    {
        public EntityKey Key(CustomerPostalAddressesEntity e) => BuildEntityKey(e);
    }

    private sealed class TestableContact : Contact_To_D365FSC
    {
        public EntityKey Key(ContactPersonEntity e) => BuildEntityKey(e);
    }

    private sealed class TestablePartyContact : PartyContact_To_D365FSC
    {
        public EntityKey Key(PartyContactsEntity e) => BuildEntityKey(e);
    }
}
