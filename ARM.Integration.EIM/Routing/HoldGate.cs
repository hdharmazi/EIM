namespace ARM.Integration.EIM.Routing;

/// <summary>
/// Reports whether a route still has messages waiting that have not reached the destination.
/// </summary>
public interface IRouteBacklogProbe
{
    /// <summary>
    /// True when the route has any message at <see cref="MessageStatus.Held"/> or
    /// <see cref="MessageStatus.RetryScheduled"/>.
    /// </summary>
    Task<bool> HasBacklogAsync(RouteKey key, CancellationToken cancellationToken = default);
}

/// <summary>
/// What should happen to a message that has just arrived.
/// </summary>
/// <param name="Status">The status to insert the message with.</param>
/// <param name="Process">Whether to dispatch it immediately after insertion.</param>
/// <param name="OrchestrationType">The orchestration that will handle it, now or on release.</param>
/// <param name="Reason">Why, for logging. Operators ask "why is this held" constantly.</param>
public readonly record struct GateDecision(int Status, bool Process, string OrchestrationType, string Reason);

/// <summary>
/// Decides whether an arriving message is processed immediately or captured and held.
/// </summary>
/// <remarks>
/// <para>
/// A route is live when it is enabled <em>and</em> has no backlog. The backlog check is what
/// preserves ordering: if older messages are still waiting, a newly arrived one must queue behind
/// them rather than overtake them. Messages awaiting retry count as backlog for exactly the same
/// reason — they are older messages not yet applied to the destination.
/// </para>
/// <para>
/// The gate lives here rather than in a handler because every entry point performs the same
/// insert-then-process sequence, and a gate duplicated per entry point would drift.
/// </para>
/// </remarks>
public sealed class HoldGate(IRouteResolver resolver, IRouteBacklogProbe backlogProbe)
{
    private readonly IRouteResolver _resolver = resolver
        ?? throw new ArgumentNullException(nameof(resolver));
    private readonly IRouteBacklogProbe _backlogProbe = backlogProbe
        ?? throw new ArgumentNullException(nameof(backlogProbe));

    /// <exception cref="RouteNotFoundException">The route is not registered.</exception>
    public async Task<GateDecision> DecideAsync(RouteKey key, CancellationToken cancellationToken = default)
    {
        var route = await _resolver.ResolveAsync(key, cancellationToken);

        if (!route.Enabled)
        {
            return new GateDecision(MessageStatus.Held, Process: false, route.OrchestrationType,
                "route is paused");
        }

        // Only asked when the route is enabled, so a paused route costs no query at all.
        if (await _backlogProbe.HasBacklogAsync(key, cancellationToken))
        {
            return new GateDecision(MessageStatus.Held, Process: false, route.OrchestrationType,
                "route is draining a backlog");
        }

        return new GateDecision(MessageStatus.New, Process: true, route.OrchestrationType,
            "route is live");
    }
}
