namespace ARM.Integration.Dashboard.API.Auth;

public static class AuthorizationPolicies
{
    /// <summary>
    /// Matches the fallback policy. Controllers must request this by name: a bare
    /// [Authorize] resolves to DefaultPolicy, which does not require DbUserValidated.
    /// </summary>
    public const string DashboardUser = nameof(DashboardUser);
}
