namespace ARM.Integration.D365FSC.Gateway.Forwarding;

/// <summary>
/// Rewrites absolute D365 URLs in responses so they address the gateway instead.
/// </summary>
/// <remarks>
/// D365 returns <c>@odata.nextLink</c> and the <c>Location</c> header as absolute URLs pointing at
/// the D365 host. Passed through untouched, the caller receives a link it cannot follow — it holds
/// no Entra credential — so paging silently dead-ends on page two and a created record's location
/// is unusable. Rewriting keeps the gateway a standards-compliant OData endpoint, which is what lets
/// off-the-shelf OData clients page without any custom code.
///
/// This is an ordinal substring replacement rather than a JSON parse: a single linear scan costs a
/// few milliseconds against a D365 round trip of hundreds, whereas parsing and re-serialising a
/// large page costs both time and several times the body in memory. It also fixes
/// <c>@odata.context</c> as a side effect, which a targeted JSON edit would not.
/// </remarks>
public static class ResponseRewriter
{
    /// <summary>
    /// Builds the substitution pair for an environment: the D365 OData root, and the gateway route
    /// that fronts it.
    /// </summary>
    public static (string From, string To) BuildSubstitution(
        string d365BaseUrl, string d365Namespace, string gatewayBaseUrl, string environment)
    {
        var from = $"{d365BaseUrl.TrimEnd('/')}/{d365Namespace}data/";
        var to = $"{gatewayBaseUrl.TrimEnd('/')}/api/v1/d365fsc/{environment}/data/";
        return (from, to);
    }

    public static string Rewrite(string? content, string from, string to)
    {
        if (string.IsNullOrEmpty(content)) return content ?? string.Empty;

        // Guard before replacing so a body with no D365 URL in it is not copied at all.
        return content.Contains(from, StringComparison.Ordinal)
            ? content.Replace(from, to, StringComparison.Ordinal)
            : content;
    }
}
