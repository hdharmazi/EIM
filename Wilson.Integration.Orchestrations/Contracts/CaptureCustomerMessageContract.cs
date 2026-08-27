using ARM.Integration.EIM;
using ARM.Integration.EIM.Orchestration;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wilson.Integration.Orchestrations
{
    /// <summary>
    /// Converts JSON numbers to strings and vice versa.
    /// Capture sends some fields as JSON numbers (e.g., EmployeeResponsibleNumber: 5637170177)
    /// but the model stores them as strings.
    /// </summary>
    internal sealed class StringOrNumberConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString(),
                JsonTokenType.Number when reader.TryGetInt64(out var l) => l.ToString(),
                JsonTokenType.Number => reader.GetDouble().ToString(),
                JsonTokenType.Null => null,
                _ => throw new JsonException($"Unexpected token {reader.TokenType} for string property.")
            };
        }

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        {
            if (value is null) writer.WriteNullValue();
            else writer.WriteStringValue(value);
        }
    }

    public class CaptureCustomerMessageContract
    {
        [JsonPropertyName("dataAreaId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string DataAreaId { get; set; }

        [JsonPropertyName("PartyType")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [JsonConverter(typeof(StringOrNumberConverter))]
        public string PartyType { get; set; }

        [JsonPropertyName("CustomerAccount")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
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
        [JsonConverter(typeof(StringOrNumberConverter))]
        public string EmployeeResponsibleNumber { get; set; }

        [JsonPropertyName("Blocked")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [JsonConverter(typeof(StringOrNumberConverter))]
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
        public string PartyNumber { get; set; }
    }
}
