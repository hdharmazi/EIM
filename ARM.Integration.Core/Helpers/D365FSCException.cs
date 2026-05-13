using System.Net;

namespace ARM.Integration.Core.Helpers;

/// <summary>
/// Represents a failed D365 OData request with parsed error details.
/// </summary>
public sealed class D365FSCException(
    string message,
    HttpStatusCode statusCode,
    string? odataErrorCode = null,
    string? responseBody = null,
    Exception? inner = null)
    : Exception(message, inner)
{
    public HttpStatusCode StatusCode     { get; } = statusCode;
    public string?        ODataErrorCode { get; } = odataErrorCode;
    public string?        ResponseBody   { get; } = responseBody;
}
