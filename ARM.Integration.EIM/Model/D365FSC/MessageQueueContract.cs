using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace ARM.Integration.EIM
{
    public class MessageQueueContract
    {
        [JsonPropertyName("_companyId")]
        public string CompanyId { get; set; }

        [JsonPropertyName("_messageQueue")]
        public string MessageQueue { get; set; }

        [JsonPropertyName("_messageType")]
        public string MessageType { get; set; }

        [JsonPropertyName("_messageContent")]
        public string MessageContent { get; set; }
    }
}
