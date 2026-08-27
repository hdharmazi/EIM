namespace ARM.Integration.EIM.Retry;

/// <summary>
/// Computes when a retryably failed message should next be attempted, and when it has run out
/// of attempts.
/// </summary>
/// <remarks>
/// Delay is <c>min(BaseDelay * 2^(attempt-1), MaxDelay)</c> reduced by up to
/// <see cref="RetryOptions.JitterFactor"/> of itself.
/// <para>
/// Jitter subtracts rather than adds, which matters twice. It keeps the schedule inside
/// <see cref="RetryOptions.MaxDelay"/> without a clamp that would flatten the spread — and a herd
/// of messages failing together reaches the ceiling at the same attempt, so that is precisely
/// where spread is needed. And because the factor is capped at 0.5, the shortest delay for an
/// attempt still exceeds the longest delay for the attempt before it, so successive delays grow
/// strictly rather than merely on average.
/// </para>
/// </remarks>
public sealed class RetryScheduleCalculator
{
    private readonly RetryOptions _options;
    private readonly Random _random;

    public RetryScheduleCalculator(RetryOptions options, Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _options = options;
        _random = random ?? Random.Shared;
    }

    /// <summary>
    /// Whether <paramref name="attempt"/> may be made at all, or the message has exhausted its
    /// allowance and must escalate to <see cref="MessageStatus.DeadLetter"/>.
    /// </summary>
    /// <param name="attempt">The attempt number just recorded, counting from 1.</param>
    public bool AttemptsRemain(int attempt) => attempt < _options.MaxAttempts;

    /// <summary>
    /// The delay before <paramref name="attempt"/> should be retried.
    /// </summary>
    /// <param name="attempt">The attempt number just recorded, counting from 1.</param>
    public TimeSpan DelayFor(int attempt)
    {
        if (attempt < 1)
            throw new ArgumentOutOfRangeException(nameof(attempt), attempt,
                "Attempts are counted from 1.");

        var growth = Math.Pow(2, attempt - 1);
        var baseMs = _options.BaseDelay.TotalMilliseconds * growth;

        // A large attempt number overflows to positive infinity rather than wrapping, so this
        // clamp holds for any input.
        var cappedMs = Math.Min(baseMs, _options.MaxDelay.TotalMilliseconds);

        var jitteredMs = cappedMs * (1.0 - (_random.NextDouble() * _options.JitterFactor));

        return TimeSpan.FromMilliseconds(jitteredMs);
    }

    /// <summary>
    /// The absolute UTC time at which <paramref name="attempt"/> becomes due.
    /// </summary>
    /// <param name="attempt">The attempt number just recorded, counting from 1.</param>
    /// <param name="utcNow">Current UTC time. Must be UTC; the column it feeds is UTC.</param>
    public DateTime NextAttemptUtc(int attempt, DateTime utcNow)
    {
        if (utcNow.Kind == DateTimeKind.Local)
            throw new ArgumentException(
                "Next-attempt times are stored in UTC; pass a UTC value.", nameof(utcNow));

        return utcNow + DelayFor(attempt);
    }
}
