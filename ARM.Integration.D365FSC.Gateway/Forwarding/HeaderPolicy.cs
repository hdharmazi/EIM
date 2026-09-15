namespace ARM.Integration.D365FSC.Gateway.Forwarding;

/// <summary>
/// Explicit allow-lists for headers crossing the gateway in either direction.
/// </summary>
/// <remarks>
/// Allow-list rather than deny-list: a header nobody thought about should not reach D365, and an
/// upstream header nobody thought about should not reach the caller. In particular the caller's own
/// credentials must never be forwarded — the gateway authenticates to D365 as itself.
/// </remarks>
public static class HeaderPolicy
{
    private static readonly HashSet<string> ForwardToD365 = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept",
        "Accept-Language",
        "Content-Type",
        "If-Match",
        "If-None-Match",
        "Prefer",            // carries odata.maxpagesize, which is how a client controls page size
        "OData-Version",
        "OData-MaxVersion",
        "Isolation",
    };

    private static readonly HashSet<string> ReturnToCaller = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type",
        "ETag",
        "Location",
        "Preference-Applied",
        "OData-Version",
        "OData-EntityId",
        "Retry-After",
    };

    public static bool MayForwardRequestHeader(string name) => ForwardToD365.Contains(name);

    public static bool MayReturnResponseHeader(string name) => ReturnToCaller.Contains(name);
}
