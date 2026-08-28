using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ARM.Integration.Core.Helpers;
using ARM.Integration.EIM.Retry;
using Xunit;

namespace ARM.Integration.EIM.Tests;

/// <summary>
/// Covers the rule that carries the risk: an unrecognised failure must be terminal, because
/// retrying a failure raised after a partially applied write duplicates it.
/// </summary>
public class FailureClassifierTests
{
    private static DefaultFailureClassifier Classifier(params string[] retryableODataCodes)
    {
        var options = new RetryOptions();
        foreach (var code in retryableODataCodes) options.RetryableODataErrorCodes.Add(code);
        return new DefaultFailureClassifier(options);
    }

    private static D365FSCException D365(HttpStatusCode status, string? odataCode = null)
        => new("destination rejected the write", status, odataCode);

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public void Throttling_and_server_errors_are_retryable(HttpStatusCode status)
        => Assert.Equal(FailureDisposition.Retryable, Classifier().Classify(D365(status)));

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public void Validation_and_authorization_rejections_are_terminal(HttpStatusCode status)
        => Assert.Equal(FailureDisposition.Terminal, Classifier().Classify(D365(status)));

    [Theory]
    [InlineData(HttpStatusCode.NotImplemented)]
    [InlineData(HttpStatusCode.HttpVersionNotSupported)]
    public void Server_errors_the_destination_will_never_support_are_terminal(HttpStatusCode status)
        => Assert.Equal(FailureDisposition.Terminal, Classifier().Classify(D365(status)));

    [Fact]
    public void Transport_failures_are_retryable()
    {
        var classifier = Classifier();

        Assert.Equal(FailureDisposition.Retryable,
            classifier.Classify(new HttpRequestException("connection reset")));
        Assert.Equal(FailureDisposition.Retryable,
            classifier.Classify(new SocketException((int)SocketError.HostUnreachable)));
        Assert.Equal(FailureDisposition.Retryable,
            classifier.Classify(new TimeoutException("timed out")));
    }

    [Fact]
    public void An_http_client_timeout_is_retryable_through_its_inner_exception()
    {
        // This is the shape HttpClient produces on its own timeout: a cancellation wrapping a
        // TimeoutException. The cancellation alone says nothing, so the chain has to be walked.
        var timeout = new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout",
            new TimeoutException());

        Assert.Equal(FailureDisposition.Retryable, Classifier().Classify(timeout));
    }

    [Fact]
    public void A_plain_cancellation_is_not_retryable()
        => Assert.Equal(FailureDisposition.Terminal,
            Classifier().Classify(new OperationCanceledException()));

    [Fact]
    public void Unrecognised_failures_default_to_terminal()
    {
        var classifier = Classifier();

        Assert.Equal(FailureDisposition.Terminal,
            classifier.Classify(new InvalidOperationException("something unexpected")));
        Assert.Equal(FailureDisposition.Terminal,
            classifier.Classify(new ApplicationException("business rule violated")));
    }

    [Fact]
    public void A_failure_raised_after_the_destination_write_succeeded_is_terminal()
    {
        // The exact incident this rule exists for: the D365 create succeeded, then publishing the
        // response failed on a malformed configuration row. Retrying would duplicate the record.
        var afterTheWrite = new JsonException("'E' is an invalid start of a value.");

        Assert.Equal(FailureDisposition.Terminal, Classifier().Classify(afterTheWrite));
    }

    [Fact]
    public void A_definite_destination_rejection_outranks_a_retryable_inner_exception()
    {
        var rejection = new D365FSCException(
            "invalid payload", HttpStatusCode.BadRequest, "InvalidData", null,
            new HttpRequestException("noise"));

        Assert.Equal(FailureDisposition.Terminal, Classifier().Classify(rejection));
    }

    [Fact]
    public void A_retryable_failure_inside_an_aggregate_is_found()
    {
        var aggregate = new AggregateException(
            new InvalidOperationException("unrelated"),
            D365(HttpStatusCode.ServiceUnavailable));

        Assert.Equal(FailureDisposition.Retryable, Classifier().Classify(aggregate));
    }

    [Fact]
    public void Configured_odata_error_codes_are_retryable_despite_a_terminal_status()
    {
        var configured = Classifier("SrsReportServerUnavailable");

        Assert.Equal(FailureDisposition.Retryable,
            configured.Classify(D365(HttpStatusCode.BadRequest, "SrsReportServerUnavailable")));
    }

    [Fact]
    public void Odata_error_codes_are_terminal_until_configured_otherwise()
        => Assert.Equal(FailureDisposition.Terminal,
            Classifier().Classify(D365(HttpStatusCode.BadRequest, "SrsReportServerUnavailable")));

    [Fact]
    public void Odata_error_code_matching_ignores_case()
    {
        var configured = Classifier("SomeTransientCode");

        Assert.Equal(FailureDisposition.Retryable,
            configured.Classify(D365(HttpStatusCode.BadRequest, "sometransientcode")));
    }

    [Fact]
    public void A_destination_rejection_is_terminal_and_positively_matched()
    {
        var verdict = Classifier().ClassifyVerdict(D365(HttpStatusCode.BadRequest));

        Assert.True(verdict.IsTerminal);
        Assert.Equal(TerminalReason.Rejected, verdict.Reason);
    }

    [Fact]
    public void A_server_error_carries_no_terminal_reason()
    {
        var verdict = Classifier().ClassifyVerdict(D365(HttpStatusCode.ServiceUnavailable));

        Assert.False(verdict.IsTerminal);
        Assert.Equal(TerminalReason.None, verdict.Reason);
    }

    [Fact]
    public void A_deserialization_failure_is_positively_matched()
    {
        var verdict = Classifier().ClassifyVerdict(new JsonException("'E' is an invalid start of a value."));

        Assert.True(verdict.IsTerminal);
        Assert.Equal(TerminalReason.Rejected, verdict.Reason);
    }

    [Fact]
    public void An_unrecognised_failure_is_terminal_but_unclassified()
    {
        var verdict = Classifier().ClassifyVerdict(new NullReferenceException("mapper bug"));

        Assert.True(verdict.IsTerminal);
        Assert.Equal(TerminalReason.Unclassified, verdict.Reason);
    }

    [Fact]
    public void Wrapping_does_not_lose_a_positively_matched_rejection()
    {
        var wrapped = new InvalidOperationException("orchestration failed", D365(HttpStatusCode.BadRequest));

        var verdict = Classifier().ClassifyVerdict(wrapped);

        Assert.True(verdict.IsTerminal);
        Assert.Equal(TerminalReason.Rejected, verdict.Reason);
    }
}
