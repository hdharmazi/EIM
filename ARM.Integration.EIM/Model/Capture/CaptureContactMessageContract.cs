using System.Text.Json.Serialization;

namespace ARM.Integration.EIM
{
    public class CaptureContactMessageContract
    {
        [JsonPropertyName("dataAreaId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string DataAreaId { get; set; }

        [JsonPropertyName("ContactPersonId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string ContactPersonId { get; set; }

        [JsonPropertyName("FirstName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string FirstName { get; set; }

        [JsonPropertyName("MiddleName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string MiddleName { get; set; }

        [JsonPropertyName("LastName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string LastName { get; set; }

        [JsonPropertyName("AssociatedPartyNumber")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string AssociatedPartyNumber { get; set; }

        [JsonPropertyName("AssociatedContactNumber")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string AssociatedContactNumber { get; set; }

        [JsonPropertyName("AssociatedContactType")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string AssociatedContactType { get; set; }
    }
}
