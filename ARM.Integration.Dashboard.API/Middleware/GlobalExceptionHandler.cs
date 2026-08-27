using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;

namespace ARM.Integration.Dashboard.API.Middleware;

/// <summary>
/// Returns a safe failure response carrying a correlation id, and records the detail server-side.
/// The <c>{ success, message }</c> shape matches what the SPA's apiFetch already handles.
/// </summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var correlationId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        logger.LogError(exception,
            "Unhandled exception. CorrelationId={CorrelationId} Method={Method} Path={Path}",
            correlationId, httpContext.Request.Method, httpContext.Request.Path);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await httpContext.Response.WriteAsJsonAsync(new
        {
            success = false,
            message = "An unexpected error occurred. Quote the correlation id when reporting this.",
            correlationId,
        }, cancellationToken);

        return true;
    }
}
