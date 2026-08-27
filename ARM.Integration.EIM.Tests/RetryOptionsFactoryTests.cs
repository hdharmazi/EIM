using ARM.Integration.EIM.Retry;
using Xunit;

namespace ARM.Integration.EIM.Tests;

public class RetryOptionsFactoryTests
{
    private static Func<string, string?> Settings(Dictionary<string, string?> values)
        => key => values.TryGetValue(key, out var v) ? v : null;

    [Fact]
    public void Absent_settings_fall_back_to_defaults()
    {
        var options = RetryOptionsFactory.Create(_ => null);
        var defaults = new RetryOptions();

        Assert.Equal(defaults.MaxAttempts, options.MaxAttempts);
        Assert.Equal(defaults.BaseDelay, options.BaseDelay);
        Assert.Equal(defaults.MaxDelay, options.MaxDelay);
        Assert.Equal(defaults.SweepBatchSize, options.SweepBatchSize);
    }

    [Fact]
    public void Settings_are_read_and_applied()
    {
        var options = RetryOptionsFactory.Create(Settings(new()
        {
            [RetryOptionsFactory.MaxAttemptsSetting] = "8",
            [RetryOptionsFactory.BaseDelaySecondsSetting] = "10",
            [RetryOptionsFactory.MaxDelaySecondsSetting] = "600",
            [RetryOptionsFactory.JitterFactorSetting] = "0.3",
            [RetryOptionsFactory.SweepBatchSizeSetting] = "250",
        }));

        Assert.Equal(8, options.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(10), options.BaseDelay);
        Assert.Equal(TimeSpan.FromMinutes(10), options.MaxDelay);
        Assert.Equal(0.3, options.JitterFactor);
        Assert.Equal(250, options.SweepBatchSize);
    }

    [Fact]
    public void Retryable_odata_codes_are_semicolon_separated_and_trimmed()
    {
        var options = RetryOptionsFactory.Create(Settings(new()
        {
            [RetryOptionsFactory.RetryableODataErrorCodesSetting] = " CodeA ; CodeB ;; ",
        }));

        Assert.Equal(2, options.RetryableODataErrorCodes.Count);
        Assert.Contains("codea", options.RetryableODataErrorCodes);
        Assert.Contains("CodeB", options.RetryableODataErrorCodes);
    }

    [Fact]
    public void An_out_of_range_setting_fails_loudly()
    {
        // Silently falling back would hide the typo and leave the schedule quietly wrong.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RetryOptionsFactory.Create(Settings(new()
            {
                [RetryOptionsFactory.JitterFactorSetting] = "0.9",
            })));
    }

    [Fact]
    public void A_max_delay_below_the_base_delay_fails_loudly()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RetryOptionsFactory.Create(Settings(new()
            {
                [RetryOptionsFactory.BaseDelaySecondsSetting] = "600",
                [RetryOptionsFactory.MaxDelaySecondsSetting] = "30",
            })));
    }

    [Fact]
    public void An_unparseable_setting_falls_back_rather_than_throwing()
    {
        // A missing or malformed value is not the same as an out-of-range one: there is nothing to
        // apply, so the default stands.
        var options = RetryOptionsFactory.Create(Settings(new()
        {
            [RetryOptionsFactory.MaxAttemptsSetting] = "not-a-number",
        }));

        Assert.Equal(new RetryOptions().MaxAttempts, options.MaxAttempts);
    }
}
