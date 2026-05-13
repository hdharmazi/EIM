namespace ARM.Integration.Dashboard.API.Middleware;

public class ApiKeyMiddleware(RequestDelegate next, IConfiguration config)
{
    private const string ApiKeyHeader = "X-Api-Key";
    private const string ApiKeyConfigKey = "ApiKey";

    public async Task InvokeAsync(HttpContext context)
    {
        var expectedKey = config[ApiKeyConfigKey];

        if (string.IsNullOrWhiteSpace(expectedKey))
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsync("API key is not configured on the server.");
            return;
        }

        if (!context.Request.Headers.TryGetValue(ApiKeyHeader, out var receivedKey)
            || receivedKey != expectedKey)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized: invalid or missing API key.");
            return;
        }

        await next(context);
    }
}
