using System.Text.Json;
using System.Text.RegularExpressions;

namespace ARM.Integration.EIM.Utilities
{
    public static class D365FSCEventParser
    {
        /// <summary>
        /// Converts the Attributes key-value array into a flat dictionary.
        /// Handles: string, number, OptionSetValue, and /Date()/ format.
        /// </summary>
        public static Dictionary<string, object> ParseAttributes(List<AttributeKvp> attributes)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            foreach (var attr in attributes)
            {
                result[attr.Key] = ResolveValue(attr.Value);
            }

            return result;
        }

        public static object ResolveValue(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => ParseDateOrString(element.GetString()),
                JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                JsonValueKind.Object => ResolveObject(element),
                _ => element.ToString()
            };
        }

        private static object ResolveObject(JsonElement element)
        {
            // Handle OptionSetValue: { "__type": "OptionSetValue:...", "Value": 200000000 }
            if (element.TryGetProperty("Value", out var optionValue) &&
                element.TryGetProperty("__type", out var typeEl) &&
                typeEl.GetString()?.StartsWith("OptionSetValue") == true)
            {
                return optionValue.GetInt32();
            }

            return element.ToString();
        }

        private static object ParseDateOrString(string value)
        {
            // Handle WCF /Date(1745952594000+0000)/ format
            var match = Regex.Match(value ?? "", @"\/Date\((-?\d+)([+-]\d{4})?\)\/");
            if (match.Success && long.TryParse(match.Groups[1].Value, out var ms))
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
            }

            return value;
        }

        /// <summary>
        /// Gets the Target entity attributes from InputParameters.
        /// Returns null when there is no Target parameter, its value is absent or JSON null,
        /// or the entity carries no Attributes.
        /// </summary>
        public static Dictionary<string, object>? GetTargetAttributes(List<InputParameter> inputParameters)
        {
            var target = inputParameters.FirstOrDefault(p => p.Key == "Target");
            if (target is null || target.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;

            var entity = JsonSerializer.Deserialize<EntityValue>(target.Value.GetRawText());
            return entity?.Attributes is null ? null : ParseAttributes(entity.Attributes);
        }

        /// <summary>
        /// Gets the Change entity attributes from InputParameters
        /// </summary>
        public static Dictionary<string, object> GetChangeAttributes(List<InputParameter> inputParameters)
        {
            var target = inputParameters.FirstOrDefault(p => p.Key == "ChangedFields");
            if (target.Value.ValueKind == JsonValueKind.Undefined) return null;

            var entity = JsonSerializer.Deserialize<EntityValue>(target.Value.GetRawText());
            return ParseAttributes(entity.Attributes);
        }

        /// <summary>
        /// Gets the list of changed fields
        /// </summary>
        public static List<string> GetChangedFields(List<InputParameter> inputParameters)
        {
            var changedFields = inputParameters.FirstOrDefault(p => p.Key == "ChangedFields");
            if (changedFields.Value.ValueKind == JsonValueKind.Undefined) return new List<string>();

            return JsonSerializer.Deserialize<List<string>>(changedFields.Value.GetRawText());
        }
    }
}