using System.Net;

namespace ARM.Integration.D365FSC.SampleEngApp.Models;

/// <summary>Thrown for any non-2xx gateway response. Quote <see cref="CorrelationId"/> when raising a support ticket.</summary>
public sealed class GatewayException(
    HttpStatusCode statusCode,
    string? errorCode,
    string? correlationId,
    string rawBody)
    : Exception($"Gateway returned {(int)statusCode} {statusCode}" +
                (errorCode is null ? "" : $" ({errorCode})") +
                (correlationId is null ? "" : $" [correlationId: {correlationId}]"))
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    /// <summary>Gateway error code such as <c>forbidden</c>. Null when the body was not a gateway error (e.g. a 401 from the Functions host).</summary>
    public string? ErrorCode { get; } = errorCode;

    public string? CorrelationId { get; } = correlationId;

    public string RawBody { get; } = rawBody;
}
