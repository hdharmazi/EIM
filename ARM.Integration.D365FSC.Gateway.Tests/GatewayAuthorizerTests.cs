using ARM.Integration.D365FSC.Gateway.Authorization;
using ARM.Integration.D365FSC.Gateway.Routing;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace ARM.Integration.D365FSC.Gateway.Tests;

public class GatewayAuthorizerTests
{
    private const string Key = "a-real-function-key";

    private sealed class FakeConsumers : IGatewayConsumerRepository
    {
        public readonly Dictionary<string, GatewayConsumer> Items = new(StringComparer.OrdinalIgnoreCase);

        public Task<GatewayConsumer?> FindAsync(string consumerId, CancellationToken cancellationToken = default)
            => Task.FromResult(Items.TryGetValue(consumerId, out var c) ? c : null);
    }

    private sealed class FakePermissions : IGatewayPermissionRepository
    {
        private readonly Dictionary<string, string> _grants = new(StringComparer.OrdinalIgnoreCase);

        public int Lookups { get; private set; }

        public void Grant(string consumer, string environment, GatewayResourceType type, string resource, string verb)
            => _grants[$"{consumer}|{environment}|{type}|{resource}|{verb}"] = resource;

        public Task<string?> FindCanonicalResourceAsync(
            string consumerId, string environment, GatewayResourceType resourceType,
            string resource, string verb, CancellationToken cancellationToken = default)
        {
            Lookups++;
            return Task.FromResult(_grants.TryGetValue($"{consumerId}|{environment}|{resourceType}|{resource}|{verb}", out var v) ? v : null);
        }
    }

    private static (GatewayAuthorizer Authorizer, FakeConsumers Consumers, FakePermissions Permissions, FakeTimeProvider Time) Create(
        string? keyHash = null)
    {
        var consumers = new FakeConsumers();
        consumers.Items["acme"] = new GatewayConsumer("acme", keyHash, "D365FSC-GW", Enabled: true, ExpiresOn: null);

        var permissions = new FakePermissions();
        permissions.Grant("acme", "DEV", GatewayResourceType.Data, "CustomersV3", "GET");

        var time = new FakeTimeProvider();
        return (new GatewayAuthorizer(consumers, permissions, null, time), consumers, permissions, time);
    }

    private static GatewayAuthorizationRequest Request(
        string environment = "DEV",
        string verb = "GET",
        string resource = "CustomersV3",
        GatewayResourceType type = GatewayResourceType.Data,
        string? consumerId = "acme",
        string? key = Key,
        bool keyInQuery = false)
        => new(consumerId, key, keyInQuery, environment, verb, new GatewayResource(type, resource));

    [Fact]
    public async Task Granted_request_is_allowed()
    {
        var (authorizer, _, _, _) = Create();

        var result = await authorizer.AuthorizeAsync(Request());

        Assert.True(result.IsAllowed);
        Assert.Equal("CustomersV3", result.CanonicalResource);
    }

    [Fact]
    public async Task Caller_spelling_is_replaced_by_stored_canonical_spelling()
    {
        var (authorizer, _, _, _) = Create();

        var result = await authorizer.AuthorizeAsync(Request(resource: "customersv3"));

        // D365 entity names are case-sensitive in the URL, so forwarding the caller's spelling
        // would authorize correctly and then 404 upstream.
        Assert.True(result.IsAllowed);
        Assert.Equal("CustomersV3", result.CanonicalResource);
    }

    [Fact]
    public async Task Environment_is_matched_case_insensitively()
    {
        var (authorizer, _, _, _) = Create();

        Assert.True((await authorizer.AuthorizeAsync(Request(environment: "  dev  "))).IsAllowed);
    }

    [Fact]
    public async Task Read_grant_does_not_confer_write()
    {
        var (authorizer, _, _, _) = Create();

        var result = await authorizer.AuthorizeAsync(Request(verb: "POST"));

        Assert.False(result.IsAllowed);
        Assert.Equal(403, result.StatusCode);
    }

    [Fact]
    public async Task Read_grant_on_entity_does_not_confer_bound_action()
    {
        var (authorizer, _, permissions, _) = Create();
        permissions.Grant("acme", "DEV", GatewayResourceType.Data, "SalesOrderHeadersV2", "GET");

        var result = await authorizer.AuthorizeAsync(Request(
            verb: "POST", resource: "SalesOrderHeadersV2/Confirm", type: GatewayResourceType.Action));

        Assert.False(result.IsAllowed);
    }

    [Fact]
    public async Task Grants_do_not_leak_across_environments()
    {
        var (authorizer, _, _, _) = Create();

        var result = await authorizer.AuthorizeAsync(Request(environment: "CDB-PROD"));

        Assert.False(result.IsAllowed);
        Assert.Equal(403, result.StatusCode);
    }

    [Fact]
    public async Task Delete_is_never_grantable()
    {
        var (authorizer, _, permissions, _) = Create();
        permissions.Grant("acme", "DEV", GatewayResourceType.Data, "CustomersV3", "DELETE");

        var result = await authorizer.AuthorizeAsync(Request(verb: "DELETE"));

        // Rejected on the verb before the grant is even consulted, so a row inserted directly into
        // the database could not enable it either.
        Assert.False(result.IsAllowed);
        Assert.Equal(0, permissions.Lookups);
    }

    [Fact]
    public async Task Unknown_consumer_is_denied()
    {
        var (authorizer, _, _, _) = Create();

        Assert.Equal(403, (await authorizer.AuthorizeAsync(Request(consumerId: "ghost"))).StatusCode);
    }

    [Fact]
    public async Task Missing_consumer_header_is_a_bad_request()
    {
        var (authorizer, _, _, _) = Create();

        Assert.Equal(400, (await authorizer.AuthorizeAsync(Request(consumerId: null))).StatusCode);
    }

    [Fact]
    public async Task Key_in_query_string_is_rejected_before_anything_else()
    {
        var (authorizer, _, permissions, _) = Create();

        var result = await authorizer.AuthorizeAsync(Request(keyInQuery: true));

        Assert.Equal(400, result.StatusCode);
        Assert.Equal(0, permissions.Lookups);
    }

    [Fact]
    public async Task Disabled_consumer_is_denied()
    {
        var (authorizer, consumers, _, _) = Create();
        consumers.Items["acme"] = consumers.Items["acme"] with { Enabled = false };

        Assert.Equal(403, (await authorizer.AuthorizeAsync(Request())).StatusCode);
    }

    [Fact]
    public async Task Expired_consumer_is_denied()
    {
        var (authorizer, consumers, _, time) = Create();
        consumers.Items["acme"] = consumers.Items["acme"] with { ExpiresOn = time.GetUtcNow().AddHours(1) };

        Assert.True((await authorizer.AuthorizeAsync(Request())).IsAllowed);

        time.Advance(TimeSpan.FromHours(2));

        Assert.Equal(403, (await authorizer.AuthorizeAsync(Request())).StatusCode);
    }

    [Fact]
    public async Task Null_key_hash_allows_unverified_identity()
    {
        var (authorizer, _, _, _) = Create(keyHash: null);

        var result = await authorizer.AuthorizeAsync(Request());

        Assert.True(result.IsAllowed);
        Assert.False(result.IdentityVerified);
    }

    [Fact]
    public async Task Populated_key_hash_verifies_the_presented_key()
    {
        var (authorizer, _, _, _) = Create(keyHash: GatewayAuthorizer.HashKey(Key));

        var result = await authorizer.AuthorizeAsync(Request());

        Assert.True(result.IsAllowed);
        Assert.True(result.IdentityVerified);
    }

    /// <summary>
    /// The dashboard records which application verified a consumer's key and when. None of that may
    /// reach the request path: if it did, a dashboard-side defect or a stale verification record
    /// would start refusing live traffic. The runtime model is deliberately limited to the five
    /// fields the decision actually needs.
    /// </summary>
    [Fact]
    public void Runtime_consumer_model_carries_no_verification_metadata()
    {
        var properties = typeof(GatewayConsumer).GetProperties().Select(p => p.Name).ToArray();

        Assert.Equal(
            ["ConsumerId", "KeyHash", "SystemProfile", "Enabled", "ExpiresOn"],
            properties);
    }

    [Fact]
    public async Task Spoofed_consumer_id_is_caught_once_the_key_hash_is_set()
    {
        var (authorizer, _, _, _) = Create(keyHash: GatewayAuthorizer.HashKey("a-different-consumers-key"));

        var result = await authorizer.AuthorizeAsync(Request());

        Assert.False(result.IsAllowed);
        Assert.Equal(403, result.StatusCode);
    }

    [Fact]
    public async Task Verification_rejects_a_missing_key()
    {
        var (authorizer, _, _, _) = Create(keyHash: GatewayAuthorizer.HashKey(Key));

        Assert.False((await authorizer.AuthorizeAsync(Request(key: null))).IsAllowed);
    }

    [Fact]
    public async Task Key_hash_comparison_tolerates_stored_casing()
    {
        var (authorizer, _, _, _) = Create(keyHash: GatewayAuthorizer.HashKey(Key).ToUpperInvariant());

        Assert.True((await authorizer.AuthorizeAsync(Request())).IsAllowed);
    }
}
