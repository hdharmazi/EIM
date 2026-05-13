using System.Text.Json.Serialization;

namespace ARM.Integration.Core.Model
{
    public class QueueMessageContract
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("workflowid")]
        public string WorkflowId { get; set; }

        [JsonPropertyName("actionid")]
        public string ActionId { get; set; }

        [JsonPropertyName("environment")]
        public string Environment { get; set; }

        [JsonPropertyName("sourceid")]
        public string SourceId { get; set; }

        [JsonPropertyName("sourcesystem")]
        public string SourceSystem { get; set; }

        [JsonPropertyName("sourceentity")]
        public string SourceEntity { get; set; }

        [JsonPropertyName("destinationsystem")]
        public string DestinationSystem { get; set; }

        [JsonPropertyName("destinationentity")]
        public string DestinationEntity { get; set; }

        [JsonPropertyName("messageid")]
        public string MessageId { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("responsequeue")]
        public string ResponseQueue { get; set; }

        [JsonPropertyName("queue")]
        public string Queue { get; set; }

        [JsonPropertyName("payload")]
        public string Payload { get; set; }
    }
}
