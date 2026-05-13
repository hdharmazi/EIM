using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ARM.Integration.EIM
{
    public class EventContext
    {
        public string MessageName { get; set; }
        public string PrimaryEntityName { get; set; }
        public string UserAzureActiveDirectoryObjectId { get; set; }
        public string UserId { get; set; }
        public List<InputParameter> InputParameters { get; set; }
        public string OperationCreatedOn { get; set; }
        public string OperationId { get; set; }
    }

    public class InputParameter
    {
        [JsonPropertyName("key")]
        public string Key { get; set; }

        [JsonPropertyName("value")]
        public JsonElement Value { get; set; }
    }

    public class EntityValue
    {
        [JsonPropertyName("Attributes")]
        public List<AttributeKvp> Attributes { get; set; }

        [JsonPropertyName("Id")]
        public string Id { get; set; }

        [JsonPropertyName("LogicalName")]
        public string LogicalName { get; set; }
    }

    public class AttributeKvp
    {
        [JsonPropertyName("key")]
        public string Key { get; set; }

        [JsonPropertyName("value")]
        public JsonElement Value { get; set; }
    }
}