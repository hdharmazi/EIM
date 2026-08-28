namespace ARM.Integration.EIM.Retry;

/// <summary>
/// Why a failure was classified <see cref="FailureDisposition.Terminal"/>. Both terminal reasons
/// record the same status; they differ only in how the failure is reported, because bad source
/// data and a defect in our own code need different people to act.
/// </summary>
public enum TerminalReason
{
    /// <summary>The failure is retryable, so no terminal reason applies.</summary>
    None = 0,

    /// <summary>
    /// The destination gave a definite negative answer, or the message could not be understood.
    /// Expected in normal operation; the source data has to change.
    /// </summary>
    Rejected = 1,

    /// <summary>
    /// The failure matched no known retryable or positively terminal condition. Recorded
    /// terminally so it is visible, but it is more likely a defect than invalid source data.
    /// </summary>
    Unclassified = 2
}
