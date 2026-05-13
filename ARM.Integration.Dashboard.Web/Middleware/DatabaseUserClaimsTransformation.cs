using System.Security.Claims;
using ARM.Integration.Dashboard.Web.Services;
using Microsoft.AspNetCore.Authentication;

namespace ARM.Integration.Dashboard.Web.Middleware;

public class DatabaseUserClaimsTransformation(IUserService userService) : IClaimsTransformation
{
    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
            return principal;

        // Already transformed in this request
        if (identity.HasClaim(c => c.Type == "DbUserValidated"))
            return principal;

        var email = identity.FindFirst(ClaimTypes.Email)?.Value
                 ?? identity.FindFirst("preferred_username")?.Value;

        if (string.IsNullOrEmpty(email))
            return principal;

        var user = await userService.GetByEmailAsync(email.ToLowerInvariant());

        if (user is not null && user.IsActive)
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, user.Role));
            identity.AddClaim(new Claim("DbUserValidated", "true"));
            if (!string.IsNullOrEmpty(user.DisplayName))
            {
                identity.AddClaim(new Claim("DisplayName", user.DisplayName));
            }
        }
        else
        {
            // Mark as not authorized in DB — middleware will redirect
            identity.AddClaim(new Claim("DbUserValidated", "false"));
        }

        return principal;
    }
}
