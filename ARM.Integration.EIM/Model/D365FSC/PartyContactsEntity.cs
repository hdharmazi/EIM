using System.Text.Json.Serialization;

namespace ARM.Integration.EIM
{
    public class PartyContactsEntity
    {
        [JsonPropertyName("PartyNumber")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [D365ExcludeOnUpdate("OData key — supplied in the request URL.")]
        public string PartyNumber { get; set; }

        [JsonPropertyName("WTC_ContactPartyNumber")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string WTC_ContactPartyNumber { get; set; }

        [JsonPropertyName("WTC_CustomerPartyNumber")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string WTC_CustomerPartyNumber { get; set; }

        [JsonPropertyName("ElectronicAddressId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [D365ExcludeOnUpdate("OData key — supplied in the request URL.")]
        public string ElectronicAddressId { get; set; }

        [JsonPropertyName("Locator")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Locator { get; set; }

        [JsonPropertyName("LocatorExtension")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string LocatorExtension { get; set; }

        [JsonPropertyName("IsPrimary")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string IsPrimary { get; set; }

        [JsonPropertyName("Description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Description { get; set; }

        [JsonPropertyName("Type")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [D365ExcludeOnUpdate("Set on creation and not patchable in D365 PartyContactsV3.")]
        public string Type { get; set; }

        [JsonPropertyName("IsInstantMessage")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string IsInstantMessage { get; set; }

        [JsonPropertyName("IsMobilePhone")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string IsMobilePhone { get; set; }

        [JsonPropertyName("CountryRegionCode")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string CountryRegionCode { get; set; }

        [JsonPropertyName("Purpose")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Purpose { get; set; }

        [JsonPropertyName("LocationId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string LocationId { get; set; }

        [JsonPropertyName("IsPrivate")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string IsPrivate { get; set; }
    }
}
