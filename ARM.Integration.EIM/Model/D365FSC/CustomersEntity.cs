using System.Text.Json.Serialization;

namespace ARM.Integration.EIM
{
    public class CustomersEntity
    {
        [JsonPropertyName("dataAreaId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [D365ExcludeOnUpdate("OData key — supplied in the request URL.")]
        public string DataAreaId { get; set; }

        [JsonPropertyName("CustomerAccount")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [D365ExcludeOnUpdate("OData key — supplied in the request URL.")]
        public string CustomerAccount { get; set; }

        [JsonPropertyName("OrganizationName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string OrganizationName { get; set; }

        [JsonPropertyName("NameAlias")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string NameAlias { get; set; }

        [JsonPropertyName("CustomerGroupId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string CustomerGroupId { get; set; }

        [JsonPropertyName("EmployeeResponsibleNumber")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string EmployeeResponsibleNumber { get; set; }

        [JsonPropertyName("SalesCurrencyCode")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [D365ExcludeOnUpdate("Create-only default. The mapper hardcodes \"USD\"; sending it on update would overwrite the customer's currency in D365.")]
        public string SalesCurrencyCode { get; set; }
        
        [JsonPropertyName("Blocked")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Blocked { get; set; }

        [JsonPropertyName("DepositRequired")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string DepositRequired { get; set; }

        [JsonPropertyName("DOTNumber")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string DotNumber { get; set; }

        [JsonPropertyName("TaxStatus")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string TaxStatus { get; set; }

        [JsonPropertyName("PartyNumber")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [D365ExcludeOnUpdate("D365 rejects PATCH: update not allowed for field 'PartyNumber'.")]
        public string PartyNumber { get; set; }
    }
}
