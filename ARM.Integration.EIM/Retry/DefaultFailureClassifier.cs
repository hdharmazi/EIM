using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using ARM.Integration.Core.Helpers;
using Microsoft.Extensions.Logging;

namespace ARM.Integration.EIM.Retry;

/// <summary>
/// Classifies failures by transport and destination response, defaulting to
/// <see cref="FailureDisposition.Terminal"/>.
/// </summary>
/// <remarks>
/// <para>
/// This sits above the HTTP client's own resilience handler, which already retries 429 and 5xx a
/// few times within a single request. A failure reaching here has therefore already survived that,
/// so "retryable" means "worth trying again in minutes", not "worth trying again in milliseconds".
/// </para>
/// <para>
/// The default is deliberately terminal. Anything raised <em>after</em> the destination write
/// succeeded — a serialization fault while publishing the response, say — is unrecognised and so
/// becomes terminal, which is what keeps a retry from duplicating an applied write.
/// </para>
/// </remarks>
public sealed class DefaultFailureClassifier(
    RetryOptions options,
    ILogger<DefaultFailureClassifier>? logger = null) : IFailureClassifier
{
    private readonly RetryOptions _options = options
        ?? throw new ArgumentNullException(nameof(options));

    public FailureDisposition Classify(Exception exception) => ClassifyVerdict(exception).Disposition;

    public FailureVerdict ClassifyVerdict(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var sawRetryable = false;

        foreach (var link in Unwrap(exception))
        {
            switch (ClassifySingle(link))
            {
                // A positively terminal link wins outright: the destination has given a definite
                // answer, and no transport detail underneath it changes that.
                case FailureDisposition.Terminal when IsPositivelyTerminal(link):
                    Log(exception, link, FailureDisposition.Terminal);
                    return FailureVerdict.Rejected;

                case FailureDisposition.Retryable:
                    sawRetryable = true;
                    break;
            }
        }

        var verdict = sawRetryable ? FailureVerdict.Retryable : FailureVerdict.Unclassified;
        Log(exception, exception, verdict.Disposition);
        return verdict;
    }

    private FailureDisposition ClassifySingle(Exception exception) => exception switch
    {
        D365FSCException d365 when
            d365.ODataErrorCode is not null
            && _options.RetryableODataErrorCodes.Contains(d365.ODataErrorCode)
            => FailureDisposition.Retryable,

        D365FSCException d365 => IsRetryableStatus(d365.StatusCode)
            ? FailureDisposition.Retryable
            : FailureDisposition.Terminal,

        // Destination unreachable, connection reset, DNS failure, or a request that timed out.
        HttpRequestException or SocketException or TimeoutException or IOException
            => FailureDisposition.Retryable,

        _ => FailureDisposition.Terminal
    };

    /// <summary>
    /// Whether this link is a definite terminal answer rather than merely unrecognised. Only
    /// definite answers short-circuit the walk over the inner-exception chain.
    /// </summary>
    private static bool IsPositivelyTerminal(Exception exception) => exception switch
    {
        D365FSCException d365 => !IsRetryableStatus(d365.StatusCode),
        JsonException => true,
        MissingEntityKeyException => true,
        D365ServiceRejectedException => true,
        _ => false
    };

    private static bool IsRetryableStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.RequestTimeout => true,
        HttpStatusCode.TooManyRequests => true,

        // The destination understood the request and will never support it. Retrying cannot help.
        HttpStatusCode.NotImplemented => false,
        HttpStatusCode.HttpVersionNotSupported => false,

        _ => (int)status >= 500
    };

    private void Log(Exception original, Exception decidedBy, FailureDisposition disposition)
    {
        if (logger is null) return;

        var code = decidedBy is D365FSCException d365
            ? $"{(int)d365.StatusCode}/{d365.ODataErrorCode ?? "-"}"
            : "-";

        logger.LogDebug(
            "Classified {ExceptionType} as {Disposition} (decided by {DecidingType}, destination {Code}).",
            original.GetType().Name, disposition, decidedBy.GetType().Name, code);
    }

    /// <summary>
    /// Yields the exception and everything beneath it, flattening <see cref="AggregateException"/>.
    /// </summary>
    private static IEnumerable<Exception> Unwrap(Exception exception)
    {
        var pending = new Stack<Exception>();
        pending.Push(exception);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            yield return current;

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }
    }
}
