namespace ARM.Integration.EIM.Retry;

/// <summary>
/// Tuning for automatic retry of transient failures.
/// </summary>
public sealed class RetryOptions
{
    public const string SectionName = "Retry";

    /// <summary>
    /// Attempts allowed before a message escalates to <see cref="MessageStatus.DeadLetter"/>.
    /// </summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Delay before the first retry. Doubles on each subsequent attempt.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Upper bound on the delay. Bounds the worst case for a stalled route, so the total time to
    /// exhaustion stays predictable.
    /// </summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Fraction of the computed delay that jitter may subtract, in [0, 0.5]. Jitter is subtractive
    /// so that successive delays still grow strictly (see <see cref="RetryScheduleCalculator"/>),
    /// and so that spread is preserved once delays reach <see cref="MaxDelay"/> — which is exactly
    /// when a herd of messages would otherwise become synchronised.
    /// </summary>
    public double JitterFactor { get; set; } = 0.2;

    /// <summary>
    /// Destination OData error codes to treat as retryable, case-insensitive. Empty by default:
    /// unrecognised codes are terminal. Populate this as codes are observed in a given environment
    /// rather than guessing at them in code.
    /// </summary>
    public HashSet<string> RetryableODataErrorCodes { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Messages a single retry sweep may claim.</summary>
    public int SweepBatchSize { get; set; } = 100;

    /// <summary>
    /// Throws if the values cannot produce a sane schedule. Called by the calculator so a
    /// misconfiguration fails loudly at first use rather than producing silently useless delays.
    /// </summary>
    public void Validate()
    {
        if (MaxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts), MaxAttempts,
                "MaxAttempts must be at least 1.");

        if (BaseDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(BaseDelay), BaseDelay,
                "BaseDelay must be positive.");

        if (MaxDelay < BaseDelay)
            throw new ArgumentOutOfRangeException(nameof(MaxDelay), MaxDelay,
                "MaxDelay must be greater than or equal to BaseDelay.");

        if (JitterFactor is < 0 or > 0.5)
            throw new ArgumentOutOfRangeException(nameof(JitterFactor), JitterFactor,
                "JitterFactor must be in [0, 0.5]; above 0.5 successive delays could overlap.");

        if (SweepBatchSize < 1)
            throw new ArgumentOutOfRangeException(nameof(SweepBatchSize), SweepBatchSize,
                "SweepBatchSize must be at least 1.");
    }
}
