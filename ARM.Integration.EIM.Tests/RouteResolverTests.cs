using ARM.Integration.EIM.Orchestration;
using ARM.Integration.EIM.Routing;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace ARM.Integration.EIM.Tests;

public class RouteResolverTests
{
    private static readonly RouteKey CustomerAddress =
        new("TEST", "D365FSC", "mserp_wtc_custpostaladdressv2entity");

    /// <summary>Counts loads so cache behaviour is observable without a database.</summary>
    private sealed class FakeRouteRepository(params (RouteKey Key, RouteDefinition Route)[] routes)
        : IRouteRepository
    {
        private Dictionary<RouteKey, RouteDefinition> _routes = routes.ToDictionary(r => r.Key, r => r.Route);

        public int LoadCount { get; private set; }

        public void Replace(params (RouteKey Key, RouteDefinition Route)[] newRoutes)
            => _routes = newRoutes.ToDictionary(r => r.Key, r => r.Route);

        public Task<IReadOnlyDictionary<RouteKey, RouteDefinition>> LoadAllAsync(CancellationToken ct = default)
        {
            LoadCount++;
            return Task.FromResult<IReadOnlyDictionary<RouteKey, RouteDefinition>>(
                new Dictionary<RouteKey, RouteDefinition>(_routes));
        }
    }

    private static (CachingRouteResolver Resolver, FakeRouteRepository Repo, FakeTimeProvider Clock) Build(
        params (RouteKey, RouteDefinition)[] routes)
    {
        var repo = new FakeRouteRepository(routes);
        var clock = new FakeTimeProvider();
        return (new CachingRouteResolver(repo, TimeSpan.FromSeconds(60), clock), repo, clock);
    }

    private static RouteDefinition Enabled(string type = "CustomerAddress_To_Capture")
        => new(type, Enabled: true, PausePolicy.Hold);

    [Fact]
    public async Task Resolves_a_seeded_route()
    {
        var (resolver, _, _) = Build((CustomerAddress, Enabled()));

        var route = await resolver.ResolveAsync(CustomerAddress);

        Assert.Equal("CustomerAddress_To_Capture", route.OrchestrationType);
        Assert.True(route.Enabled);
        Assert.Equal(PausePolicy.Hold, route.OnPause);
    }

    [Fact]
    public async Task Resolution_ignores_case()
    {
        // ENVIRONMENT is stored as both 'TEST' and 'test' in existing data, and the database
        // collation is case-insensitive. A case-sensitive resolver would fail to route half of it.
        var (resolver, _, _) = Build((CustomerAddress, Enabled()));

        var route = await resolver.ResolveAsync(
            new RouteKey("test", "d365fsc", "MSERP_WTC_CUSTPOSTALADDRESSV2ENTITY"));

        Assert.Equal("CustomerAddress_To_Capture", route.OrchestrationType);
    }

    [Fact]
    public async Task Routes_are_scoped_by_environment()
    {
        var (resolver, _, _) = Build((CustomerAddress, Enabled()));

        // Same system and entity, different environment: pausing in one must not affect another.
        await Assert.ThrowsAsync<RouteNotFoundException>(() =>
            resolver.ResolveAsync(new RouteKey("BAT", "D365FSC", "mserp_wtc_custpostaladdressv2entity")));
    }

    [Fact]
    public async Task Routes_are_scoped_by_source_system()
    {
        var (resolver, _, _) = Build((CustomerAddress, Enabled()));

        await Assert.ThrowsAsync<RouteNotFoundException>(() =>
            resolver.ResolveAsync(new RouteKey("TEST", "CAPTURE", "mserp_wtc_custpostaladdressv2entity")));
    }

    [Fact]
    public async Task An_unresolvable_route_is_an_error_naming_the_key()
    {
        var (resolver, _, _) = Build((CustomerAddress, Enabled()));
        var missing = new RouteKey("TEST", "D365FSC", "mserp_unknown");

        var ex = await Assert.ThrowsAsync<RouteNotFoundException>(() => resolver.ResolveAsync(missing));

        Assert.Equal(missing, ex.Key);
        Assert.Contains("mserp_unknown", ex.Message);
    }

    [Fact]
    public async Task Repeated_resolution_hits_the_cache()
    {
        var (resolver, repo, _) = Build((CustomerAddress, Enabled()));

        for (var i = 0; i < 20; i++) await resolver.ResolveAsync(CustomerAddress);

        Assert.Equal(1, repo.LoadCount);
    }

    [Fact]
    public async Task A_miss_does_not_reload_the_table()
    {
        // Otherwise a flood of unroutable messages reloads the route table once per message.
        var (resolver, repo, _) = Build((CustomerAddress, Enabled()));
        var missing = new RouteKey("TEST", "D365FSC", "mserp_unknown");

        for (var i = 0; i < 5; i++)
            await Assert.ThrowsAsync<RouteNotFoundException>(() => resolver.ResolveAsync(missing));

        Assert.Equal(1, repo.LoadCount);
    }

    [Fact]
    public async Task The_cache_expires_after_its_duration()
    {
        var (resolver, repo, clock) = Build((CustomerAddress, Enabled()));

        await resolver.ResolveAsync(CustomerAddress);
        clock.Advance(TimeSpan.FromSeconds(59));
        await resolver.ResolveAsync(CustomerAddress);
        Assert.Equal(1, repo.LoadCount);

        clock.Advance(TimeSpan.FromSeconds(2));
        await resolver.ResolveAsync(CustomerAddress);
        Assert.Equal(2, repo.LoadCount);
    }

    [Fact]
    public async Task Invalidate_makes_a_pause_visible_immediately()
    {
        var (resolver, repo, _) = Build((CustomerAddress, Enabled()));

        Assert.True((await resolver.ResolveAsync(CustomerAddress)).Enabled);

        repo.Replace((CustomerAddress, new RouteDefinition("CustomerAddress_To_Capture", false, PausePolicy.Hold)));

        // Without invalidation the pause would not be seen until the cache expired.
        Assert.True((await resolver.ResolveAsync(CustomerAddress)).Enabled);

        resolver.Invalidate();

        Assert.False((await resolver.ResolveAsync(CustomerAddress)).Enabled);
        Assert.Equal(2, repo.LoadCount);
    }

    [Fact]
    public async Task Concurrent_first_resolutions_load_once()
    {
        var (resolver, repo, _) = Build((CustomerAddress, Enabled()));

        await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => resolver.ResolveAsync(CustomerAddress))));

        Assert.Equal(1, repo.LoadCount);
    }

    [Fact]
    public void A_non_positive_cache_duration_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CachingRouteResolver(new FakeRouteRepository(), TimeSpan.Zero));
    }
}

public class RouteRegistryValidatorTests
{
    private sealed class StubOrchestration : IOrchestration
    {
        public Task<OrchestrationResult> ProcessAsync(
            ARM.Integration.Core.Model.QueueMessageContract message,
            string connectionString,
            ARM.Integration.Core.Helpers.IOrchestrationLogger stepLog,
            CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static Dictionary<string, IOrchestration> Registered(params string[] names)
        => names.ToDictionary(n => n, _ => (IOrchestration)new StubOrchestration());

    private static Dictionary<RouteKey, RouteDefinition> Routes(params (string Env, string Type)[] routes)
        => routes.ToDictionary(
            r => new RouteKey(r.Env, "D365FSC", "entity"),
            r => new RouteDefinition(r.Type, true, PausePolicy.Hold));

    [Fact]
    public void Passes_when_every_route_has_a_registration()
    {
        RouteRegistryValidator.Validate(
            Routes(("TEST", "Customer_To_Capture")),
            Registered("Customer_To_Capture"));
    }

    [Fact]
    public void Fails_naming_the_route_and_the_missing_orchestration()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            RouteRegistryValidator.Validate(
                Routes(("TEST", "Ghost_To_Nowhere")),
                Registered("Customer_To_Capture")));

        Assert.Contains("Ghost_To_Nowhere", ex.Message);
        Assert.Contains("TEST", ex.Message);
        // The registered set is listed too, since the usual cause is a typo in one or the other.
        Assert.Contains("Customer_To_Capture", ex.Message);
    }

    [Fact]
    public void An_orchestration_with_no_route_is_not_an_error()
    {
        // Registering more than the routes need is harmless; the reverse is not.
        RouteRegistryValidator.Validate(
            Routes(("TEST", "Customer_To_Capture")),
            Registered("Customer_To_Capture", "SalesOrder_To_D365FSC"));
    }

    [Fact]
    public void Empty_routes_pass()
    {
        RouteRegistryValidator.Validate(
            new Dictionary<RouteKey, RouteDefinition>(),
            Registered("Customer_To_Capture"));
    }
}
