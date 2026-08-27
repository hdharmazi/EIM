using ARM.Integration.EIM.Retry;
using Xunit;

namespace ARM.Integration.EIM.Tests;

public class RetryScheduleCalculatorTests
{
    private static RetryOptions Options(double jitterFactor = 0.2) => new()
    {
        MaxAttempts = 5,
        BaseDelay = TimeSpan.FromSeconds(30),
        MaxDelay = TimeSpan.FromMinutes(30),
        JitterFactor = jitterFactor
    };

    [Fact]
    public void The_first_delay_is_the_base_delay_when_jitter_is_disabled()
    {
        var calculator = new RetryScheduleCalculator(Options(jitterFactor: 0));

        Assert.Equal(TimeSpan.FromSeconds(30), calculator.DelayFor(1));
    }

    [Fact]
    public void One_max_attempt_schedules_no_retry_at_all()
    {
        // The documented rollback lever: MaxAttempts of 1 must send the first failure straight to a
        // terminal status rather than parking it at Retry Scheduled, where a disabled sweep would
        // never collect it.
        var options = Options();
        options.MaxAttempts = 1;
        var calculator = new RetryScheduleCalculator(options);

        Assert.False(calculator.AttemptsRemain(1));
    }

    [Fact]
    public void Delay_doubles_per_attempt_when_jitter_is_disabled()
    {
        var calculator = new RetryScheduleCalculator(Options(jitterFactor: 0));

        Assert.Equal(TimeSpan.FromSeconds(30), calculator.DelayFor(1));
        Assert.Equal(TimeSpan.FromSeconds(60), calculator.DelayFor(2));
        Assert.Equal(TimeSpan.FromSeconds(120), calculator.DelayFor(3));
        Assert.Equal(TimeSpan.FromSeconds(240), calculator.DelayFor(4));
    }

    [Fact]
    public void Successive_delays_grow_strictly_even_with_jitter()
    {
        // The property that makes the schedule meaningful: growth must dominate jitter, otherwise
        // an "exponential" backoff can hand out a shorter delay than the attempt before it.
        var calculator = new RetryScheduleCalculator(Options(jitterFactor: 0.5));

        for (var trial = 0; trial < 500; trial++)
        {
            var previous = calculator.DelayFor(1);

            for (var attempt = 2; attempt <= 6; attempt++)
            {
                var current = calculator.DelayFor(attempt);
                Assert.True(current > previous,
                    $"attempt {attempt} delay {current} did not exceed attempt {attempt - 1} delay {previous}");
                previous = current;
            }
        }
    }

    [Fact]
    public void Delay_never_exceeds_the_ceiling()
    {
        var options = Options();
        var calculator = new RetryScheduleCalculator(options);

        for (var attempt = 1; attempt <= 40; attempt++)
            Assert.True(calculator.DelayFor(attempt) <= options.MaxDelay);
    }

    [Fact]
    public void An_extreme_attempt_number_still_yields_the_ceiling_rather_than_overflowing()
    {
        var options = Options(jitterFactor: 0);
        var calculator = new RetryScheduleCalculator(options);

        Assert.Equal(options.MaxDelay, calculator.DelayFor(int.MaxValue));
    }

    [Fact]
    public void Jitter_still_spreads_attempts_once_the_ceiling_is_reached()
    {
        // Where a herd of messages failing together would otherwise synchronise permanently.
        var calculator = new RetryScheduleCalculator(Options());

        var delays = Enumerable.Range(0, 50).Select(_ => calculator.DelayFor(20)).ToList();

        Assert.True(delays.Distinct().Count() > 1, "delays at the ceiling were identical");
    }

    [Fact]
    public void Attempts_run_out_at_the_configured_maximum()
    {
        var calculator = new RetryScheduleCalculator(Options());

        Assert.True(calculator.AttemptsRemain(1));
        Assert.True(calculator.AttemptsRemain(4));
        Assert.False(calculator.AttemptsRemain(5));
        Assert.False(calculator.AttemptsRemain(6));
    }

    [Fact]
    public void Next_attempt_time_is_in_the_future()
    {
        var calculator = new RetryScheduleCalculator(Options());
        var now = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.True(calculator.NextAttemptUtc(1, now) > now);
    }

    [Fact]
    public void Next_attempt_time_rejects_a_local_time()
    {
        var calculator = new RetryScheduleCalculator(Options());

        Assert.Throws<ArgumentException>(
            () => calculator.NextAttemptUtc(1, DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Local)));
    }

    [Fact]
    public void Attempts_are_counted_from_one()
    {
        var calculator = new RetryScheduleCalculator(Options());

        Assert.Throws<ArgumentOutOfRangeException>(() => calculator.DelayFor(0));
    }

    [Theory]
    [InlineData(0, null, null, 0.2)]      // MaxAttempts below 1
    [InlineData(5, 0, null, 0.2)]          // BaseDelay not positive
    [InlineData(5, null, 10, 0.2)]         // MaxDelay below BaseDelay
    [InlineData(5, null, null, 0.6)]       // JitterFactor above the monotonicity limit
    [InlineData(5, null, null, -0.1)]      // JitterFactor negative
    public void A_misconfigured_schedule_fails_at_construction(
        int maxAttempts, int? baseDelaySeconds, int? maxDelaySeconds, double jitterFactor)
    {
        var options = new RetryOptions
        {
            MaxAttempts = maxAttempts,
            BaseDelay = TimeSpan.FromSeconds(baseDelaySeconds ?? 30),
            MaxDelay = TimeSpan.FromSeconds(maxDelaySeconds ?? 1800),
            JitterFactor = jitterFactor
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryScheduleCalculator(options));
    }
}
