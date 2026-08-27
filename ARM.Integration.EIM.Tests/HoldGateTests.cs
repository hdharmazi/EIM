using ARM.Integration.EIM.Routing;
using Xunit;

namespace ARM.Integration.EIM.Tests;

public class HoldGateTests
{
    private static readonly RouteKey Address =
        new("TEST", "D365FSC", "mserp_wtc_custpostaladdressv2entity");

    private sealed class StubResolver(RouteDefinition? route) : IRouteResolver
    {
        public Task<RouteDefinition> ResolveAsync(RouteKey key, CancellationToken ct = default)
            => route is null
                ? throw new RouteNotFoundException(key)
                : Task.FromResult(route);

        public void Invalidate() { }
    }

    private sealed class StubBacklog(bool hasBacklog) : IRouteBacklogProbe
    {
        public int Calls { get; private set; }

        public Task<bool> HasBacklogAsync(RouteKey key, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(hasBacklog);
        }
    }

    private static RouteDefinition Route(bool enabled) =>
        new("CustomerAddress_To_Capture", enabled, PausePolicy.Hold);

    [Fact]
    public async Task Enabled_route_with_no_backlog_is_processed()
    {
        var gate = new HoldGate(new StubResolver(Route(enabled: true)), new StubBacklog(false));

        var decision = await gate.DecideAsync(Address);

        Assert.True(decision.Process);
        Assert.Equal(MessageStatus.New, decision.Status);
        Assert.Equal("CustomerAddress_To_Capture", decision.OrchestrationType);
    }

    [Fact]
    public async Task Paused_route_holds()
    {
        var gate = new HoldGate(new StubResolver(Route(enabled: false)), new StubBacklog(false));

        var decision = await gate.DecideAsync(Address);

        Assert.False(decision.Process);
        Assert.Equal(MessageStatus.Held, decision.Status);
        Assert.Contains("paused", decision.Reason);
    }

    [Fact]
    public async Task Enabled_route_with_a_backlog_holds()
    {
        // Ordering: a new message must not overtake older ones still waiting.
        var gate = new HoldGate(new StubResolver(Route(enabled: true)), new StubBacklog(true));

        var decision = await gate.DecideAsync(Address);

        Assert.False(decision.Process);
        Assert.Equal(MessageStatus.Held, decision.Status);
        Assert.Contains("draining", decision.Reason);
    }

    [Fact]
    public async Task A_paused_route_is_not_probed_for_backlog()
    {
        // The probe is a database round trip per message; a paused route must not pay for it.
        var backlog = new StubBacklog(false);
        var gate = new HoldGate(new StubResolver(Route(enabled: false)), backlog);

        await gate.DecideAsync(Address);

        Assert.Equal(0, backlog.Calls);
    }

    [Fact]
    public async Task An_unregistered_route_is_an_error()
    {
        var gate = new HoldGate(new StubResolver(null), new StubBacklog(false));

        await Assert.ThrowsAsync<RouteNotFoundException>(() => gate.DecideAsync(Address));
    }

    [Fact]
    public async Task The_orchestration_type_comes_from_the_route_even_when_held()
    {
        // It is needed on release, so it must be recorded at insert time rather than re-derived.
        var gate = new HoldGate(new StubResolver(Route(enabled: false)), new StubBacklog(false));

        var decision = await gate.DecideAsync(Address);

        Assert.Equal("CustomerAddress_To_Capture", decision.OrchestrationType);
    }
}
