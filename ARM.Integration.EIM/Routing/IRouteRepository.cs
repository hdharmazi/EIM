namespace ARM.Integration.EIM.Routing;

/// <summary>
/// Loads the full route table. The table is small — one row per entity per environment — so it is
/// read whole rather than a row at a time, which makes an unresolvable route detectable
/// immediately instead of requiring a negative-caching scheme.
/// </summary>
public interface IRouteRepository
{
    Task<IReadOnlyDictionary<RouteKey, RouteDefinition>> LoadAllAsync(CancellationToken cancellationToken = default);
}
