using ARM.Integration.Core.Helpers;
using ARM.Integration.D365FSC.Gateway.Configuration;

namespace ARM.Integration.D365FSC.Gateway.Connections;

/// <summary>Supplies the raw D365 connection JSON for a system profile and environment.</summary>
public interface IEnvironmentConfigurationSource
{
    /// <summary>Returns the configuration JSON, or <c>null</c> when the environment is not configured.</summary>
    Task<string?> TryGetAsync(string systemProfile, string environment, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the configuration from <c>ENVIRONMENTTABLE</c> on every request. The lookup is a two-column
/// primary key seek against a table holding a handful of rows, so it is cheap enough to do per
/// request — and doing so is what lets the connection cache invalidate itself on content.
/// </summary>
public sealed class EnvironmentConfigurationSource(GatewayOptions options) : IEnvironmentConfigurationSource
{
    public Task<string?> TryGetAsync(string systemProfile, string environment, CancellationToken cancellationToken = default)
        => EnvironmentStore.TryGetValueAsync(systemProfile, environment, options.EimDbConnectionString, cancellationToken);
}
