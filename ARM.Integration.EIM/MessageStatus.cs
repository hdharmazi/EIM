namespace ARM.Integration.EIM;

/// <summary>
/// The MESSAGEQUEUETABLE.STATUS vocabulary. Display labels live in STATUSTABLE; these are the
/// values the engine, the API and the frontend must all agree on.
/// </summary>
public static class MessageStatus
{
    /// <summary>Inserted, not yet processed. Also the state left behind if processing is interrupted before it begins.</summary>
    public const int New = 0;

    public const int Processing = 1;
    public const int Completed = 2;

    /// <summary>
    /// The destination gave a definite negative answer, or the message could not be understood.
    /// Terminal; reprocessing it unchanged cannot succeed, so the source data or configuration
    /// has to change first.
    /// </summary>
    public const int Failed = 3;

    /// <summary>
    /// No definite answer was ever obtained and the system stopped trying. Terminal; unlike
    /// <see cref="Failed"/>, reprocessing it unchanged is plausible once the destination recovers.
    /// </summary>
    public const int DeadLetter = 4;

    /// <summary>Submitted to the destination, awaiting asynchronous confirmation.</summary>
    public const int AwaitingD365 = 5;

    /// <summary>Captured durably but deliberately not processed, because its route is paused.</summary>
    public const int Held = 6;

    /// <summary>Failed retryably; a sweep will attempt it again once its next-attempt time passes.</summary>
    public const int RetryScheduled = 7;

    /// <summary>Statuses an operator may reprocess from.</summary>
    public static readonly int[] Reprocessable = [Failed, DeadLetter, Held];
}
