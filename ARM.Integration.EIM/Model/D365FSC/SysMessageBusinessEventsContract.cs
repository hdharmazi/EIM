using System.Text.Json.Serialization;

namespace ARM.Integration.EIM
{
    /// <summary>
    /// D365 F&amp;SC business event raised by the message processor as a queued message moves through
    /// its lifecycle. Used to reconcile the status of a previously submitted asynchronous message,
    /// correlated by <see cref="CreatedTransactionId"/>.
    ///
    /// The terminal outcome is <see cref="MessageProcessingResult"/>, NOT <see cref="MessageState"/>:
    /// a successfully processed message arrives with <c>MessageState = "Queued"</c> and
    /// <c>MessageProcessingResult = "Processed"</c>. Only the fields needed for reconciliation and
    /// diagnosis are modeled; the event carries roughly two dozen more.
    /// </summary>
    public class SysMessageBusinessEventsContract
    {
        [JsonPropertyName("BusinessEventId")]
        public string? BusinessEventId { get; set; }

        // D365 sends this as a JSON number today, but quoted 64-bit values are common enough in
        // F&SC serialisation that failing closed on one is not worth the risk.
        [JsonPropertyName("CreatedTransactionId")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long? CreatedTransactionId { get; set; }

        /// <summary>Terminal outcome: "Processed", "Failed", "Cancelled" or "Error". Drives status.</summary>
        [JsonPropertyName("MessageProcessingResult")]
        public string? MessageProcessingResult { get; set; }

        /// <summary>Localised display form of <see cref="MessageProcessingResult"/>. Diagnostic only.</summary>
        [JsonPropertyName("MessageProcessingResultLabel")]
        public string? MessageProcessingResultLabel { get; set; }

        /// <summary>Queue lifecycle position, e.g. "Queued". Diagnostic only — never drives status.</summary>
        [JsonPropertyName("MessageState")]
        public string? MessageState { get; set; }

        /// <summary>Localised display form of <see cref="MessageState"/>. Diagnostic only.</summary>
        [JsonPropertyName("MessageStateLabel")]
        public string? MessageStateLabel { get; set; }

        /// <summary>Human-readable processing detail. Populated on success as well as failure.</summary>
        [JsonPropertyName("MessageInfolog")]
        public string? MessageInfolog { get; set; }

        /// <summary>The business key D365 created, e.g. the sales order number "200007".</summary>
        [JsonPropertyName("MessageResponseKey")]
        public string? MessageResponseKey { get; set; }

        /// <summary>Names what <see cref="MessageResponseKey"/> is, e.g. "SalesId".</summary>
        [JsonPropertyName("MessageResponseKeyFieldName")]
        public string? MessageResponseKeyFieldName { get; set; }

        [JsonPropertyName("MessageType")]
        public string? MessageType { get; set; }

        [JsonPropertyName("MessageQueue")]
        public string? MessageQueue { get; set; }

        [JsonPropertyName("BusinessEventLegalEntity")]
        public string? BusinessEventLegalEntity { get; set; }

        [JsonPropertyName("EventId")]
        public string? EventId { get; set; }

        // The event also carries "EventTime" in the legacy /Date(ms)/ form, which does not bind to
        // DateTime; this ISO-8601 field is the same instant in a usable format.
        [JsonPropertyName("EventTimeIso8601")]
        public string? EventTimeIso8601 { get; set; }

        /// <summary>Deep link to the message in the D365 message processor, for operator triage.</summary>
        [JsonPropertyName("MessageProcessorMessagePageUrl")]
        public string? MessageProcessorMessagePageUrl { get; set; }
    }
}
