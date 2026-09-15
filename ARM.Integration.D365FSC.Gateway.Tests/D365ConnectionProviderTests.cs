using ARM.Integration.D365FSC.Gateway.Connections;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace ARM.Integration.D365FSC.Gateway.Tests;

public class D365ConnectionProviderTests
{
    private const string ConfigDev = """
        {"Tenant":"11111111-1111-1111-1111-111111111111",
         "ClientId":"22222222-2222-2222-2222-222222222222",
         "Secret":"s3cr3t",
         "URL":"https://wtc-dev.operations.dynamics.com",
         "AuthURL":"https://login.microsoftonline.com"}
        """;

    private const string ConfigDevRotatedSecret = """
        {"Tenant":"11111111-1111-1111-1111-111111111111",
         "ClientId":"22222222-2222-2222-2222-222222222222",
         "Secret":"rotated",
         "URL":"https://wtc-dev.operations.dynamics.com",
         "AuthURL":"https://login.microsoftonline.com"}
        """;

    private sealed class FakeSource : IEnvironmentConfigurationSource
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public int Reads { get; private set; }

        public void Set(string systemProfile, string environment, string? json)
        {
            var key = $"{systemProfile}|{environment}";
            if (json is null) _values.Remove(key); else _values[key] = json;
        }

        public Task<string?> TryGetAsync(string systemProfile, string environment, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(_values.TryGetValue($"{systemProfile}|{environment}", out var v) ? v : null);
        }
    }

    private static (D365ConnectionProvider Provider, FakeSource Source) Create()
    {
        var source = new FakeSource();
        source.Set("D365FSC-GW", "DEV", ConfigDev);
        return (new D365ConnectionProvider(source), source);
    }

    [Fact]
    public async Task Unconfigured_environment_returns_null()
    {
        var (provider, _) = Create();

        Assert.Null(await provider.GetAsync("D365FSC-GW", "CDB-PROD"));
    }

    [Fact]
    public async Task Repeated_requests_reuse_the_same_client()
    {
        var (provider, _) = Create();

        var first = await provider.GetAsync("D365FSC-GW", "DEV");
        var second = await provider.GetAsync("D365FSC-GW", "DEV");

        // Same HttpClient means the same delegating handler, hence the same MSAL application and
        // its token cache. Rebuilding per request would mean a fresh AAD round trip every time.
        Assert.NotNull(first);
        Assert.Same(first!.Http, second!.Http);
    }

    [Fact]
    public async Task Editing_configuration_produces_a_new_client_without_a_flush()
    {
        var (provider, source) = Create();
        var before = await provider.GetAsync("D365FSC-GW", "DEV");

        source.Set("D365FSC-GW", "DEV", ConfigDevRotatedSecret);
        var after = await provider.GetAsync("D365FSC-GW", "DEV");

        Assert.NotSame(before!.Http, after!.Http);
        Assert.NotEqual(before.ConfigHash, after.ConfigHash);
        Assert.Equal("rotated", after.Options.Secret);
    }

    [Fact]
    public async Task Reverting_configuration_reuses_the_original_entry()
    {
        var (provider, source) = Create();
        var original = await provider.GetAsync("D365FSC-GW", "DEV");

        source.Set("D365FSC-GW", "DEV", ConfigDevRotatedSecret);
        await provider.GetAsync("D365FSC-GW", "DEV");

        source.Set("D365FSC-GW", "DEV", ConfigDev);
        var reverted = await provider.GetAsync("D365FSC-GW", "DEV");

        // The key is the content hash, so identical configuration lands on the identical entry.
        Assert.Same(original!.Http, reverted!.Http);
    }

    [Fact]
    public async Task Configuration_is_read_on_every_request()
    {
        var (provider, source) = Create();

        await provider.GetAsync("D365FSC-GW", "DEV");
        await provider.GetAsync("D365FSC-GW", "DEV");

        // Reading per request is what makes the cache self-invalidating; caching the lookup too
        // would reintroduce a staleness window that no flush could close across instances.
        Assert.Equal(2, source.Reads);
    }

    [Fact]
    public async Task Concurrent_first_requests_build_one_client()
    {
        var (provider, _) = Create();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 32).Select(_ => provider.GetAsync("D365FSC-GW", "DEV")));

        Assert.Single(results.Select(r => r!.Http).Distinct());
    }

    [Fact]
    public async Task Different_environments_get_different_clients()
    {
        var (provider, source) = Create();
        source.Set("D365FSC-GW", "TEST", ConfigDevRotatedSecret);

        var dev = await provider.GetAsync("D365FSC-GW", "DEV");
        var test = await provider.GetAsync("D365FSC-GW", "TEST");

        Assert.NotSame(dev!.Http, test!.Http);
    }

    [Fact]
    public async Task Environment_lookup_is_case_insensitive_end_to_end()
    {
        var (provider, _) = Create();

        var upper = await provider.GetAsync("D365FSC-GW", "DEV");
        var lower = await provider.GetAsync("d365fsc-gw", "dev");

        Assert.Same(upper!.Http, lower!.Http);
    }

    [Fact]
    public async Task Invalid_configuration_json_fails_loudly()
    {
        var source = new FakeSource();
        source.Set("D365FSC-GW", "DEV", """{"Tenant":"only-a-tenant"}""");
        var provider = new D365ConnectionProvider(source);

        await Assert.ThrowsAnyAsync<Exception>(() => provider.GetAsync("D365FSC-GW", "DEV"));
    }

    [Fact]
    public async Task Superseded_entries_are_evicted_once_idle()
    {
        var source = new FakeSource();
        var time = new FakeTimeProvider();
        var provider = new D365ConnectionProvider(
            source, maxEntries: 2, idleTimeout: TimeSpan.FromMinutes(30), timeProvider: time);

        // Each configuration edit strands the previous entry; without eviction they accumulate.
        for (var i = 0; i < 5; i++)
        {
            source.Set("D365FSC-GW", "DEV", ConfigDev.Replace("s3cr3t", $"secret-{i}"));
            await provider.GetAsync("D365FSC-GW", "DEV");
            time.Advance(TimeSpan.FromMinutes(20));
        }

        var current = await provider.GetAsync("D365FSC-GW", "DEV");

        // The live entry survives regardless of how many stale ones were swept.
        Assert.NotNull(current);
        Assert.Same(current!.Http, (await provider.GetAsync("D365FSC-GW", "DEV"))!.Http);
    }

    [Fact]
    public async Task Entry_in_active_use_is_not_evicted()
    {
        var source = new FakeSource();
        source.Set("D365FSC-GW", "DEV", ConfigDev);
        var time = new FakeTimeProvider();
        var provider = new D365ConnectionProvider(
            source, maxEntries: 1, idleTimeout: TimeSpan.FromMinutes(30), timeProvider: time);

        var first = await provider.GetAsync("D365FSC-GW", "DEV");

        // Kept warm by traffic: each request refreshes LastUsed, so the sweep must never take it.
        for (var i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromMinutes(20));
            await provider.GetAsync("D365FSC-GW", "DEV");
        }

        Assert.Same(first!.Http, (await provider.GetAsync("D365FSC-GW", "DEV"))!.Http);
    }
}
