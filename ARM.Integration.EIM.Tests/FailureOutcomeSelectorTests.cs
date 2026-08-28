using ARM.Integration.EIM;
using ARM.Integration.EIM.Retry;
using Xunit;

namespace ARM.Integration.EIM.Tests;

/// <summary>
/// The whole terminal status policy in one table. The distinction that matters: status 3 means
/// reprocessing unchanged cannot succeed, status 4 means it plausibly can.
/// </summary>
public class FailureOutcomeSelectorTests
{
    private const string Message = "ERROR:Write validation failed for table row of type "
        + "'WTC_CustPostalAddressV2Entity'. Infolog: Warning: Field 'Company' must be filled in..";

    [Fact]
    public void A_destination_rejection_is_failed_and_reported_verbatim()
    {
        var outcome = FailureOutcomeSelector.Select(
            FailureVerdict.Rejected, Message, attempt: null, attemptsRemain: false);

        Assert.Equal(MessageStatus.Failed, outcome.Status);
        Assert.Equal(Message, outcome.Message);
        Assert.False(outcome.LogAsError);
    }

    [Fact]
    public void An_unclassified_failure_is_failed_but_marked_and_logged_as_an_error()
    {
        var outcome = FailureOutcomeSelector.Select(
            FailureVerdict.Unclassified, Message, attempt: null, attemptsRemain: false);

        Assert.Equal(MessageStatus.Failed, outcome.Status);
        Assert.Contains("unclassified failure", outcome.Message);
        Assert.True(outcome.LogAsError);
    }

    [Fact]
    public void A_retryable_failure_with_attempts_left_is_scheduled()
    {
        var outcome = FailureOutcomeSelector.Select(
            FailureVerdict.Retryable, Message, attempt: 2, attemptsRemain: true);

        Assert.Equal(MessageStatus.RetryScheduled, outcome.Status);
        Assert.Contains("attempt 2, retrying", outcome.Message);
        Assert.False(outcome.LogAsError);
    }

    [Fact]
    public void A_retryable_failure_with_no_attempts_left_is_dead_lettered()
    {
        var outcome = FailureOutcomeSelector.Select(
            FailureVerdict.Retryable, Message, attempt: 5, attemptsRemain: false);

        Assert.Equal(MessageStatus.DeadLetter, outcome.Status);
        Assert.Contains("retry attempts exhausted after 5", outcome.Message);
        Assert.True(outcome.LogAsError);
    }

    [Fact]
    public void A_retryable_failure_with_no_schedule_is_dead_lettered_without_an_attempt()
    {
        // The operator reprocess path: classify honestly, but never leave the row scheduled.
        var outcome = FailureOutcomeSelector.Select(
            FailureVerdict.Retryable, Message, attempt: null, attemptsRemain: false);

        Assert.Equal(MessageStatus.DeadLetter, outcome.Status);
        Assert.Contains("no retry configured", outcome.Message);
        Assert.DoesNotContain("attempt 0", outcome.Message);
        Assert.False(outcome.LogAsError);
    }

    [Fact]
    public void A_terminal_verdict_is_never_scheduled_or_dead_lettered()
    {
        foreach (var verdict in new[] { FailureVerdict.Rejected, FailureVerdict.Unclassified })
        {
            var withSchedule = FailureOutcomeSelector.Select(verdict, Message, attempt: 1, attemptsRemain: true);
            var exhausted = FailureOutcomeSelector.Select(verdict, Message, attempt: 5, attemptsRemain: false);

            Assert.Equal(MessageStatus.Failed, withSchedule.Status);
            Assert.Equal(MessageStatus.Failed, exhausted.Status);
        }
    }
}
