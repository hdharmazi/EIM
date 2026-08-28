namespace ARM.Integration.EIM;

/// <summary>
/// Maps the terminal outcome of a D365 message-processor business event to a
/// <see cref="MessageStatus"/> value.
///
/// The outcome comes from <c>MessageProcessingResult</c>, which captured payloads confirm is
/// populated on both terminal outcomes: success arrives as
/// <c>MessageProcessingResult = "Processed"</c> with <c>MessageState = "Queued"</c>, failure as
/// both fields reading <c>"Failed"</c>. <c>MessageState</c> is the queue lifecycle position and is
/// deliberately NOT consulted — on success it would say "Queued" and discard the event, and on a
/// hypothetical in-flight event it could say "Processed" before D365 had finished.
/// </summary>
public static class BusinessEventOutcome
{
    /// <summary>Why no status could be derived from a <c>MessageProcessingResult</c> value.</summary>
    public enum NonTerminalReason
    {
        /// <summary>A terminal status was derived; there is no reason.</summary>
        None,

        /// <summary>The field was absent or blank — D365 has not finished with the message.</summary>
        Absent,

        /// <summary>The field carried a value this code does not know. Worth an operator's attention.</summary>
        Unrecognised,
    }

    /// <summary>
    /// Maps <paramref name="messageProcessingResult"/> to <see cref="MessageStatus.Completed"/> or
    /// <see cref="MessageStatus.Failed"/>, or null when the event is not terminal.
    /// </summary>
    public static (int? Status, NonTerminalReason Reason) Map(string? messageProcessingResult)
    {
        var value = messageProcessingResult?.Trim();

        if (string.IsNullOrEmpty(value))
            return (null, NonTerminalReason.Absent);

        if (value.Equals("Processed", StringComparison.OrdinalIgnoreCase))
            return (MessageStatus.Completed, NonTerminalReason.None);

        if (value.Equals("Failed", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Error", StringComparison.OrdinalIgnoreCase))
            return (MessageStatus.Failed, NonTerminalReason.None);

        return (null, NonTerminalReason.Unrecognised);
    }
}
