using ARM.Integration.EIM;
using ARM.Integration.EIM.Orchestration;
using System.Text.Json.Serialization;

namespace Wilson.Integration.Orchestrations
{
    public class CaptureCustomerAddressMessageContract
    {
        [JsonPropertyName("DataAreaId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string DataAreaId { get; set; }

        [JsonPropertyName("CustomerAccount")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string CustomerAccount { get; set; }

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
