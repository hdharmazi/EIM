using System.Text.Json.Serialization;

namespace ARM.Integration.EIM
{
    public class CapturePartyContactMessageContract
    {
        [JsonPropertyName("PartyNumber")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string PartyNumber { get; set; }

        [JsonPropertyName("ElectronicAddressId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
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
