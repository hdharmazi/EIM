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

        [JsonPropertyName("AdressLocationId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string AddressLocationId { get; set; }

        [JsonPropertyName("IsPrimary")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IsPrimary { get; set; }

        [JsonPropertyName("IsRoleDelivery")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IsRoleDelivery { get; set; }

        [JsonPropertyName("IsRoleBusiness")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IsRoleBusiness { get; set; }

        [JsonPropertyName("IsRoleInvoice")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IsRoleInvoice { get; set; }

        [JsonPropertyName("AddressDescription")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string AddressDescription { get; set; }

        [JsonPropertyName("IsPostalAddress")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IsPostalAddress { get; set; }

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
