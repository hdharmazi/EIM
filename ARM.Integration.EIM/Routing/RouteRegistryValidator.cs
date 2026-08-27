using ARM.Integration.EIM.Orchestration;

namespace ARM.Integration.EIM.Routing;

/// <summary>
/// Asserts that every registered route can actually be dispatched.
/// </summary>
public static class RouteRegistryValidator
{
    /// <summary>
    /// Fails when a route names an orchestration the host has not registered.
    /// </summary>
    /// <remarks>
    /// Without this the mismatch surfaces as a runtime failure on live traffic, one message at a
    /// time, long after the deployment that caused it. Routes are data and registrations are code,
    /// so they drift independently and nothing else compares them.
    /// </remarks>
    public static void Validate(
        IReadOnlyDictionary<RouteKey, RouteDefinition> routes,
        IReadOnlyDictionary<string, IOrchestration> orchestrations)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(orchestrations);

        var unregistered = routes
            .Where(r => !orchestrations.ContainsKey(r.Value.OrchestrationType))
            .Select(r => $"{r.Key} -> {r.Value.OrchestrationType}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        if (unregistered.Length == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{unregistered.Length} route(s) name an orchestration that is not registered: "
            + string.Join("; ", unregistered)
            + ". Registered: "
            + string.Join(", ", orchestrations.Keys.OrderBy(k => k, StringComparer.Ordinal))
            + ".");
    }
}
