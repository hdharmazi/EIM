using System.Text.Json.Serialization;

namespace ARM.Integration.EIM
{
    public class CaptureCustomerAddressMessageContract
    {
        [JsonPropertyName("CustomerLegalEntityId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string CustomerLegalEntityId { get; set; }

        [JsonPropertyName("CustomerAccountNumber")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string CustomerAccountNumber { get; set; }

        [JsonPropertyName("AddressLocationId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string AddressLocationId { get; set; }

        [JsonPropertyName("IsPrimary")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string IsPrimary { get; set; }

        [JsonPropertyName("AddressLocationRoles")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string AddressLocationRoles { get; set; }

        [JsonPropertyName("AddressDescription")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string AddressDescription { get; set; }

        [JsonPropertyName("IsPostalAddress")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string IsPostalAddress { get; set; }

        [JsonPropertyName("AddressCity")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string AddressCity { get; set; }

        [JsonPropertyName("AddressZipCode")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string AddressZipCode { get; set; }

        [JsonPropertyName("AddressStreet")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string AddressStreet { get; set; }

        [JsonPropertyName("AddressCountryRegionId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string AddressCountryRegionId { get; set; }

        [JsonPropertyName("AddressState")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string AddressState { get; set; }
    }
}
