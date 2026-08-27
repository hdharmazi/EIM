namespace ARM.Integration.EIM.Routing;

/// <summary>
/// Resolves a message's route. Replaces the hardcoded entity-to-orchestration dictionaries, so
/// adding an entity becomes a data change rather than an edit to shared engine code.
/// </summary>
public interface IRouteResolver
{
    /// <exception cref="RouteNotFoundException">The route is not registered.</exception>
    Task<RouteDefinition> ResolveAsync(RouteKey key, CancellationToken cancellationToken = default);

    /// <summary>Drops any cached state so the next resolution reflects a pause or resume promptly.</summary>
    void Invalidate();
}

/// <summary>
/// Thrown when a message arrives for a route that is not registered.
/// </summary>
/// <remarks>
/// This is deliberately an error rather than an implicit hold or an implicit skip. Treating an
/// unknown route as held would accumulate messages nobody is watching; skipping it would discard
/// them silently. Both hide a seeding mistake, which is the most likely cause.
/// </remarks>
public sealed class RouteNotFoundException(RouteKey key)
    : Exception($"No route is registered for '{key}'. Check ROUTETABLE seeding for this environment.")
{
    public RouteKey Key { get; } = key;
}
