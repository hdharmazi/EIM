using ARM.Integration.EIM;
using ARM.Integration.EIM.Orchestration;
using System.Text.Json.Serialization;

namespace Wilson.Integration.Orchestrations
{
    /// <summary>
    /// Common envelope for all Capture integration messages.
    /// Wraps entity-specific data in a standard structure with SourceId, Action, Entity, Status, and Data.
    /// </summary>
    public class CaptureMessageEnvelope<T> where T : class
    {
        [JsonPropertyName("SourceId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string SourceId { get; set; }

        [JsonPropertyName("Action")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Action { get; set; }

        [JsonPropertyName("Entity")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Entity { get; set; }

        [JsonPropertyName("Status")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Status { get; set; }

        [JsonPropertyName("StatusMessage")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string StatusMessage { get; set; }

        [JsonPropertyName("Data")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public T Data { get; set; }

        [JsonPropertyName("CreatedDateTime")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string CreatedDateTime { get; set; }
    }
}
