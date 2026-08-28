namespace ARM.Integration.EIM.Retry;

/// <summary>
/// Maps a classified failure to the status it is recorded at. This is the whole of the terminal
/// status policy, kept pure so it can be tested without a database.
/// </summary>
/// <remarks>
/// <para>
/// Status <see cref="MessageStatus.Failed"/> means the destination gave a definite negative answer
/// or the message could not be understood, so reprocessing it unchanged cannot succeed. Status
/// <see cref="MessageStatus.DeadLetter"/> means no definite answer was ever obtained and we stopped
/// trying, so reprocessing it unchanged is plausible.
/// </para>
/// </remarks>
public static class FailureOutcomeSelector
{
    /// <param name="attempt">
    /// The attempt just consumed, or null when no retry schedule is configured — the operator
    /// reprocess path, which classifies to report the right status but never schedules again.
    /// </param>
    public static FailureOutcome Select(
        FailureVerdict verdict, string responseMessage, int? attempt, bool attemptsRemain)
    {
        if (verdict.IsTerminal)
        {
            return verdict.Reason == TerminalReason.Unclassified
                ? new FailureOutcome(
                    MessageStatus.Failed, $"{responseMessage} (unclassified failure)", LogAsError: true)
                : new FailureOutcome(MessageStatus.Failed, responseMessage, LogAsError: false);
        }

        if (attempt is null)
        {
            return new FailureOutcome(
                MessageStatus.DeadLetter, $"{responseMessage} (no retry configured)", LogAsError: false);
        }

        return attemptsRemain
            ? new FailureOutcome(
                MessageStatus.RetryScheduled,
                $"{responseMessage} (attempt {attempt}, retrying)",
                LogAsError: false)
            : new FailureOutcome(
                MessageStatus.DeadLetter,
                $"{responseMessage} (retry attempts exhausted after {attempt})",
                LogAsError: true);
    }
}
