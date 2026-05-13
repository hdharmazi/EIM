using System.Text.Json.Serialization;

namespace ARM.Integration.EIM
{
    public class EIMResponseContract
    {
        [JsonPropertyName("Contract")]
        public string Contract { get; set; }
    }
}
