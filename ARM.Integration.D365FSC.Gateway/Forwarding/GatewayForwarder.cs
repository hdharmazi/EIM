using System.Net;
using System.Text;
using ARM.Integration.D365FSC.Gateway.Configuration;
using ARM.Integration.D365FSC.Gateway.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace ARM.Integration.D365FSC.Gateway.Forwarding;

public sealed record ForwardOutcome(int StatusCode, string Body, string? ContentType, Dictionary<string, string> Headers);

/// <summary>
/// Forwards a request to D365 verbatim and rewrites the response back into gateway terms.
/// </summary>
/// <remarks>
/// The typed <c>D365FSCClient</c> is deliberately not used here. Its CRUD methods hard-code
/// <c>data/{entity}</c> and append a <c>cross-company</c> segment from configuration, which is right
/// for an orchestration that knows its own shape and wrong for a proxy: it would silently add query
/// parameters the caller did not ask for, and it has no path that covers bound actions and custom
/// services uniformly. Forwarding the method, path, raw query string and body over the authenticated
/// <see cref="HttpClient"/> gives one code path for all three, and absorbs whatever D365 adds next.
/// </remarks>
public sealed class GatewayForwarder(GatewayOptions options, ILogger<GatewayForwarder>? logger = null)
{
    public async Task<ForwardOutcome> ForwardAsync(
        D365Connection connection,
        HttpRequest request,
        string targetRelativePath,
        string environment,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var rawQuery = request.QueryString.HasValue ? request.QueryString.Value : string.Empty;

        // The query string is passed through byte-for-byte. Parsing it into a dictionary and
        // re-serialising would re-encode $skiptoken, whose value carries quotes and commas, and
        // D365 would reject the mangled cursor.
        var target = new Uri(connection.Http.BaseAddress ?? new Uri(connection.Options.BaseUrl),
            $"{connection.Options.Namespace}{targetRelativePath}{rawQuery}");

        using var upstream = new HttpRequestMessage(new HttpMethod(request.Method), target);

        if (CanHaveBody(request.Method))
        {
            var body = await ReadBoundedBodyAsync(request, cancellationToken);
            if (body is null)
            {
                return Problem(HttpStatusCode.RequestEntityTooLarge, "payload_too_large", correlationId);
            }

            upstream.Content = new ByteArrayContent(body);

            if (!string.IsNullOrEmpty(request.ContentType))
            {
                upstream.Content.Headers.TryAddWithoutValidation("Content-Type", request.ContentType);
            }
        }

        foreach (var header in request.Headers)
        {
            if (!HeaderPolicy.MayForwardRequestHeader(header.Key)) continue;
            if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)) continue;

            upstream.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        using var response = await connection.Http.SendAsync(
            upstream, HttpCompletionOption.ResponseContentRead, cancellationToken);

        var (from, to) = ResponseRewriter.BuildSubstitution(
            connection.Options.BaseUrl, connection.Options.Namespace, options.NormalisedBaseUrl, environment);

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var rewritten = ResponseRewriter.Rewrite(content, from, to);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            if (!HeaderPolicy.MayReturnResponseHeader(header.Key)) continue;

            var value = string.Join(",", header.Value);

            // A 201 carries the created record's D365 URL, which has the same problem as nextLink.
            headers[header.Key] = string.Equals(header.Key, "Location", StringComparison.OrdinalIgnoreCase)
                ? ResponseRewriter.Rewrite(value, from, to)
                : value;
        }

        logger?.LogInformation(
            "Forwarded {Method} {Path} to {Environment}: {StatusCode} ({CorrelationId})",
            request.Method, targetRelativePath, environment, (int)response.StatusCode, correlationId);

        return new ForwardOutcome(
            (int)response.StatusCode,
            rewritten,
            response.Content.Headers.ContentType?.ToString(),
            headers);
    }

    /// <summary>Reads the body up to the configured limit, returning null when it is exceeded.</summary>
    private async Task<byte[]?> ReadBoundedBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength is { } declared && declared > options.MaxRequestBytes) return null;

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;

        // Content-Length is absent for chunked requests, so the limit is also enforced while reading
        // rather than trusting the declared length.
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > options.MaxRequestBytes) return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static bool CanHaveBody(string method) =>
        !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsDelete(method);

    private static ForwardOutcome Problem(HttpStatusCode status, string error, string correlationId) =>
        new((int)status,
            $$"""{"error":"{{error}}","correlationId":"{{correlationId}}"}""",
            "application/json",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Returns the request path exactly as it arrived on the wire, before any percent-decoding.
    /// </summary>
    /// <remarks>
    /// <c>HttpRequest.Path</c> has already been through the server's decoding rules, which is the
    /// wrong input for an authorization decision: the whole point of the strict parser is that what
    /// gets authorized is what was actually sent.
    /// </remarks>
    public static string? RawPathAfter(HttpRequest request, string marker)
    {
        var rawTarget = request.HttpContext.Features.Get<IHttpRequestFeature>()?.RawTarget;

        var path = string.IsNullOrEmpty(rawTarget)
            ? request.Path.Value
            : rawTarget.Split('?', 2)[0];

        if (string.IsNullOrEmpty(path)) return null;

        var index = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? null : path[(index + marker.Length)..];
    }

    public static string BuildErrorBody(string error, string correlationId) =>
        $$"""{"error":"{{error}}","correlationId":"{{correlationId}}"}""";
}
