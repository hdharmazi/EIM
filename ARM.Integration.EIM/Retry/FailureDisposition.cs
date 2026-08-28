namespace ARM.Integration.EIM.Retry;

/// <summary>
/// Whether a processing failure is worth attempting again.
/// </summary>
public enum FailureDisposition
{
    /// <summary>
    /// The failure is not expected to resolve itself. The message is recorded at
    /// <see cref="MessageStatus.Failed"/> and an operator has to act. This is the default for
    /// anything unrecognised.
    /// </summary>
    Terminal = 0,

    /// <summary>
    /// The failure looks like the destination being briefly unavailable, throttled or unreachable.
    /// The message is scheduled at <see cref="MessageStatus.RetryScheduled"/> and a sweep tries again.
    /// </summary>
    Retryable = 1
}
