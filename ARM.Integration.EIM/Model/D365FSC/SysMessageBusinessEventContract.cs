using System.Text.Json.Serialization;

namespace ARM.Integration.EIM
{
    /// <summary>
    /// D365 F&amp;SC business event raised by the message processor when a queued message reaches a
    /// terminal state (Processed / Failed / Cancelled). Used to reconcile the status of a previously
    /// submitted asynchronous message, correlated by <see cref="CreatedTransactionId"/>.
    /// Only the fields needed for reconciliation are modeled; the event carries additional fields.
    /// </summary>
    public class SysMessageBusinessEventContract
    {
        [JsonPropertyName("BusinessEventId")]
        public string? BusinessEventId { get; set; }

        [JsonPropertyName("CreatedTransactionId")]
        public long? CreatedTransactionId { get; set; }

        /// <summary>Terminal state: "Processed", "Failed", or "Cancelled".</summary>
        [JsonPropertyName("MessageState")]
        public string? MessageState { get; set; }

        /// <summary>Human-readable processing detail; populated with the error text on failure.</summary>
        [JsonPropertyName("MessageInfolog")]
        public string? MessageInfolog { get; set; }

        [JsonPropertyName("MessageType")]
        public string? MessageType { get; set; }

        [JsonPropertyName("MessageQueue")]
        public string? MessageQueue { get; set; }

        [JsonPropertyName("BusinessEventLegalEntity")]
        public string? BusinessEventLegalEntity { get; set; }
    }
}
