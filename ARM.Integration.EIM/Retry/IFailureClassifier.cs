namespace ARM.Integration.EIM.Retry;

/// <summary>
/// Decides whether a processing failure should be retried automatically or surfaced to an operator.
/// </summary>
public interface IFailureClassifier
{
    /// <summary>
    /// Classifies <paramref name="exception"/>. Implementations MUST default to
    /// <see cref="FailureDisposition.Terminal"/> for anything they do not positively recognise as
    /// retryable: repeating an unclassified failure risks repeating a partially applied write,
    /// whereas failing terminally is visible and recoverable.
    /// </summary>
    FailureDisposition Classify(Exception exception);
}
