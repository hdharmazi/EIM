namespace ARM.Integration.EIM.Routing;

/// <summary>
/// What happens to a message arriving for a route that is not enabled.
/// </summary>
public enum PausePolicy
{
    /// <summary>Capture it durably at <see cref="MessageStatus.Held"/> and replay it on release.</summary>
    Hold = 0,

    /// <summary>Modelled but not implemented by this change. Reserved so the column has a closed set.</summary>
    Drop = 1
}

/// <summary>
/// Identifies a route. Comparison is case-insensitive because the database collation is, and
/// <c>ENVIRONMENT</c> is stored inconsistently cased in existing data — a case-sensitive
/// comparison here would silently fail to resolve roughly half the historical rows.
/// </summary>
public readonly record struct RouteKey(string Environment, string SourceSystem, string SourceEntity)
{
    public bool Equals(RouteKey other) =>
        string.Equals(Environment, other.Environment, StringComparison.OrdinalIgnoreCase)
        && string.Equals(SourceSystem, other.SourceSystem, StringComparison.OrdinalIgnoreCase)
        && string.Equals(SourceEntity, other.SourceEntity, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => HashCode.Combine(
        Environment?.ToUpperInvariant(),
        SourceSystem?.ToUpperInvariant(),
        SourceEntity?.ToUpperInvariant());

    public override string ToString() => $"{Environment}/{SourceSystem}/{SourceEntity}";
}

/// <summary>
/// A route's orchestration and its current lifecycle state.
/// </summary>
public sealed record RouteDefinition(string OrchestrationType, bool Enabled, PausePolicy OnPause);
