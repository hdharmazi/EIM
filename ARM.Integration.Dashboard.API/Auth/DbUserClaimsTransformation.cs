using System.Security.Claims;
using ARM.Integration.Dashboard.API.Services;
using Microsoft.AspNetCore.Authentication;

namespace ARM.Integration.Dashboard.API.Auth;

/// <summary>
/// Resolves a bearer-authenticated caller to a dashboard user (by email), adding
/// their role and a <c>DbUserValidated</c> claim. Re-homed from the former
/// Dashboard.Web BFF so the API owns user validation.
/// </summary>
public class DbUserClaimsTransformation(UserRepository users) : IClaimsTransformation
{
    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
            return principal;

        if (identity.HasClaim(c => c.Type == "DbUserValidated"))
            return principal;

        var email = identity.FindFirst(ClaimTypes.Email)?.Value
                 ?? identity.FindFirst("email")?.Value
                 ?? identity.FindFirst("preferred_username")?.Value
                 ?? identity.FindFirst(ClaimTypes.Upn)?.Value
                 ?? identity.FindFirst("upn")?.Value;

        // Windows Auth (Negotiate) provides DOMAIN\username as ClaimTypes.Name.
        // Try email first, then fall back to the Windows identity name.
        var windowsName = identity.FindFirst(ClaimTypes.Name)?.Value;

        if (string.IsNullOrEmpty(email) && string.IsNullOrEmpty(windowsName))
        {
            identity.AddClaim(new Claim("DbUserValidated", "false"));
            return principal;
        }

        var user = !string.IsNullOrEmpty(email)
            ? await users.GetByEmailAsync(email.ToLowerInvariant())
            : null;

        // Fall back to Windows username lookup (stored as EMAIL in USERTABLE).
        user ??= !string.IsNullOrEmpty(windowsName)
            ? await users.GetByEmailAsync(windowsName.ToLowerInvariant())
            : null;

        if (user is not null && user.IsActive)
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, user.Role));
            identity.AddClaim(new Claim("DbUserValidated", "true"));
            if (!string.IsNullOrEmpty(user.DisplayName))
                identity.AddClaim(new Claim("DisplayName", user.DisplayName));
        }
        else
        {
            identity.AddClaim(new Claim("DbUserValidated", "false"));
        }

        return principal;
    }
}
