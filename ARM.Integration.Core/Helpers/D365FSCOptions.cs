namespace ARM.Integration.Core.Helpers;

/// <summary>
/// Strongly-typed configuration for D365 Finance &amp; Supply Chain OData access.
/// Bind from <c>appsettings.json</c> section or populate from environment JSON.
/// </summary>
public sealed class D365FSCOptions
{
    public const string SectionName = "D365";

    public required string Tenant   { get; set; }
    public required string ClientId { get; set; }
    public required string Secret   { get; set; }
    public required string URL      { get; set; }
    public required string AuthURL  { get; set; }
    public string OnPremise          { get; set; } = "false";
    public bool   CrossCompany      { get; set; } = true;
    public TimeSpan Timeout         { get; set; } = TimeSpan.FromMinutes(5);
    public int    MaxRetries        { get; set; } = 3;
    public int    BaseDelaySeconds  { get; set; } = 2;

    /// <summary>Resolves the OData base URI (with trailing slash stripped).</summary>
    public string BaseUrl => URL.TrimEnd('/');

    /// <summary>Whether this is an on-premise deployment.</summary>
    public bool IsOnPremise => bool.TryParse(OnPremise, out var v) && v;

    /// <summary>On-premise deployments require a namespace prefix.</summary>
    public string Namespace => IsOnPremise ? "namespaces/AXSF/" : string.Empty;
}
