using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ARM.Integration.Dashboard.API.Auth;

/// <summary>
/// Authenticates non-interactive service callers via the <c>X-Api-Key</c> header.
/// A successful key match yields a trusted service principal (Admin role).
/// </summary>
public class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration config) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    private const string HeaderName = "X-Api-Key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var provided))
            return Task.FromResult(AuthenticateResult.NoResult());

        var expected = config["ApiKey"];
        if (string.IsNullOrWhiteSpace(expected))
            return Task.FromResult(AuthenticateResult.Fail("API key is not configured."));

        if (provided != expected)
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "service"),
            new Claim("IsService", "true"),
            new Claim("DbUserValidated", "true"),
            new Claim(ClaimTypes.Role, "Admin"),
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
