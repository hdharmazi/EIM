namespace ARM.Integration.EIM.Retry;

/// <summary>
/// Builds <see cref="RetryOptions"/> from app settings, so attempts and backoff are tunable per
/// environment without a redeploy of the engine.
/// </summary>
public static class RetryOptionsFactory
{
    public const string MaxAttemptsSetting = "RetryMaxAttempts";
    public const string BaseDelaySecondsSetting = "RetryBaseDelaySeconds";
    public const string MaxDelaySecondsSetting = "RetryMaxDelaySeconds";
    public const string JitterFactorSetting = "RetryJitterFactor";
    public const string SweepBatchSizeSetting = "RetrySweepBatchSize";
    public const string RetryableODataErrorCodesSetting = "RetryableODataErrorCodes";

    /// <summary>
    /// Reads each setting via <paramref name="read"/>, falling back to the property default when a
    /// setting is absent or unparseable. Values that parse but are out of range still fail
    /// validation, so a typo is loud rather than silently ignored.
    /// </summary>
    public static RetryOptions Create(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        var options = new RetryOptions();

        if (int.TryParse(read(MaxAttemptsSetting), out var maxAttempts))
            options.MaxAttempts = maxAttempts;

        if (double.TryParse(read(BaseDelaySecondsSetting), out var baseSeconds))
            options.BaseDelay = TimeSpan.FromSeconds(baseSeconds);

        if (double.TryParse(read(MaxDelaySecondsSetting), out var maxSeconds))
            options.MaxDelay = TimeSpan.FromSeconds(maxSeconds);

        if (double.TryParse(read(JitterFactorSetting), out var jitter))
            options.JitterFactor = jitter;

        if (int.TryParse(read(SweepBatchSizeSetting), out var batchSize))
            options.SweepBatchSize = batchSize;

        var codes = read(RetryableODataErrorCodesSetting);
        if (!string.IsNullOrWhiteSpace(codes))
        {
            foreach (var code in codes.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                options.RetryableODataErrorCodes.Add(code);
            }
        }

        options.Validate();
        return options;
    }

    /// <summary>Reads from environment variables, which is how the Functions host is configured.</summary>
    public static RetryOptions FromEnvironment() => Create(Environment.GetEnvironmentVariable);
}
