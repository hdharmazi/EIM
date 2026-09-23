using ARM.Integration.Dashboard.API.Services;
using ARM.Integration.EIM;
using Xunit;

namespace ARM.Integration.D365FSC.Gateway.Tests;

public class CleanupEligibilityTests
{
    [Fact]
    public void No_status_selected_deletes_only_terminal_statuses()
    {
        var scope = CleanupEligibility.Resolve(status: null, acknowledged: false);

        Assert.False(scope.IsRefused);
        Assert.Equal([MessageStatus.Completed, MessageStatus.Failed], scope.Statuses);
    }

    /// <summary>
    /// The bulk path is the one an operator reaches with an age filter alone, so every status that
    /// something else depends on has to be absent from it.
    /// </summary>
    [Theory]
    [InlineData(MessageStatus.New)]
    [InlineData(MessageStatus.Processing)]
    [InlineData(MessageStatus.DeadLetter)]
    [InlineData(MessageStatus.AwaitingD365)]
    [InlineData(MessageStatus.Held)]
    [InlineData(MessageStatus.RetryScheduled)]
    public void No_status_selected_never_includes_a_protected_status(int protectedStatus)
    {
        var scope = CleanupEligibility.Resolve(status: null, acknowledged: false);

        Assert.DoesNotContain(protectedStatus, scope.Statuses);
    }

    [Theory]
    [InlineData(MessageStatus.Completed)]
    [InlineData(MessageStatus.Failed)]
    public void A_deletable_status_needs_no_acknowledgement(int status)
    {
        var scope = CleanupEligibility.Resolve(status, acknowledged: false);

        Assert.False(scope.IsRefused);
        Assert.Equal([status], scope.Statuses);
    }

    [Theory]
    [InlineData(MessageStatus.New)]
    [InlineData(MessageStatus.Processing)]
    [InlineData(MessageStatus.DeadLetter)]
    [InlineData(MessageStatus.AwaitingD365)]
    [InlineData(MessageStatus.Held)]
    [InlineData(MessageStatus.RetryScheduled)]
    public void A_protected_status_is_refused_without_acknowledgement(int status)
    {
        var scope = CleanupEligibility.Resolve(status, acknowledged: false);

        Assert.True(scope.IsRefused);
        Assert.Empty(scope.Statuses);
    }

    [Fact]
    public void A_refusal_names_what_depends_on_the_rows()
    {
        var scope = CleanupEligibility.Resolve(MessageStatus.Held, acknowledged: false);

        Assert.Contains("HELD", scope.Refusal);
        Assert.Contains("paused route", scope.Refusal);
    }

    [Theory]
    [InlineData(MessageStatus.New)]
    [InlineData(MessageStatus.Processing)]
    [InlineData(MessageStatus.DeadLetter)]
    [InlineData(MessageStatus.AwaitingD365)]
    [InlineData(MessageStatus.Held)]
    [InlineData(MessageStatus.RetryScheduled)]
    public void An_acknowledged_protected_status_is_allowed(int status)
    {
        var scope = CleanupEligibility.Resolve(status, acknowledged: true);

        Assert.False(scope.IsRefused);
        Assert.Equal([status], scope.Statuses);
    }

    /// <summary>
    /// Acknowledging one status must not widen the delete to any other, since the resolved set is
    /// the only thing constraining the SQL.
    /// </summary>
    [Fact]
    public void An_acknowledgement_does_not_widen_the_selection()
    {
        var scope = CleanupEligibility.Resolve(MessageStatus.Held, acknowledged: true);

        Assert.Equal([MessageStatus.Held], scope.Statuses);
    }

    [Fact]
    public void An_unrecognised_status_is_protected_rather_than_assumed_deletable()
    {
        var scope = CleanupEligibility.Resolve(status: 99, acknowledged: false);

        Assert.True(scope.IsRefused);
        Assert.Empty(scope.Statuses);
    }
}
