namespace ARM.Integration.Dashboard.Web.Middleware;

public class DatabaseUserAuthorizationMiddleware(RequestDelegate next)
{
    private static readonly HashSet<string> BypassPaths =
    [
        "/accessdenied",
        "/signin-oidc",
        "/signout-callback-oidc",
        "/microsoftidentity/account/signout",
        "/microsoftidentity/account/signedout"
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";

        // Don't check static files, bypass paths, or unauthenticated requests
        if (path.StartsWith("/lib/") || path.StartsWith("/css/") || path.StartsWith("/js/")
            || path.StartsWith("/_") || BypassPaths.Contains(path)
            || context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        var validated = context.User.FindFirst("DbUserValidated")?.Value;

        if (validated == "false")
        {
            context.Response.Redirect("/AccessDenied");
            return;
        }

        await next(context);
    }
}
