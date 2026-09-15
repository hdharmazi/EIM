using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ARM.Integration.Core.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ARM.Integration.D365FSC.Gateway.Connections;

/// <summary>An authenticated D365 client together with the options it was built from.</summary>
public sealed record D365Connection(HttpClient Http, D365FSCOptions Options, string ConfigHash);

public interface ID365ConnectionProvider
{
    /// <summary>Returns a connection, or <c>null</c> when the environment is not configured.</summary>
    Task<D365Connection?> GetAsync(string systemProfile, string environment, CancellationToken cancellationToken = default);
}

/// <summary>
/// Caches one authenticated client per distinct configuration, keyed on a hash of the configuration
/// JSON itself.
/// </summary>
/// <remarks>
/// Keying on content rather than on profile-and-environment removes cache invalidation entirely:
/// editing the row in the dashboard changes the hash, the next request builds a new client, and the
/// old entry ages out. That matters because the gateway scales out — an explicit flush endpoint
/// would only ever reach the single instance that received the flush call.
///
/// Caching is not an optimisation here. <c>CreateStandaloneClient</c> builds a fresh
/// <see cref="HttpClient"/> and a fresh MSAL application per call, which for a queue handler running
/// one message at a time is harmless, but for an HTTP front end means socket churn and a full AAD
/// round trip on every request, because MSAL caches tokens per application instance.
/// </remarks>
public sealed class D365ConnectionProvider : ID365ConnectionProvider, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IEnvironmentConfigurationSource _source;
    private readonly ILogger<D365ConnectionProvider>? _logger;
    private readonly int _maxEntries;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeProvider _time;

    private readonly ConcurrentDictionary<string, Lazy<CacheEntry>> _cache = new(StringComparer.Ordinal);

    public D365ConnectionProvider(
        IEnvironmentConfigurationSource source,
        ILogger<D365ConnectionProvider>? logger = null,
        int maxEntries = 32,
        TimeSpan? idleTimeout = null,
        TimeProvider? timeProvider = null)
    {
        _source = source;
        _logger = logger;
        _maxEntries = maxEntries;
        _idleTimeout = idleTimeout ?? TimeSpan.FromMinutes(30);
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<D365Connection?> GetAsync(
        string systemProfile, string environment, CancellationToken cancellationToken = default)
    {
        var json = await _source.TryGetAsync(systemProfile, environment, cancellationToken);
        if (string.IsNullOrEmpty(json)) return null;

        var hash = ComputeHash(json);

        // Lazy keeps a burst of concurrent first-requests from each building a client and an MSAL app.
        var entry = _cache.GetOrAdd(
            hash,
            key => new Lazy<CacheEntry>(
                () => Build(json, key, systemProfile, environment),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;

        entry.LastUsedUtc = _time.GetUtcNow();

        if (_cache.Count > _maxEntries) EvictIdle();

        return entry.Connection;
    }

    private CacheEntry Build(string json, string hash, string systemProfile, string environment)
    {
        var options = JsonSerializer.Deserialize<D365FSCOptions>(json, JsonOptions)
            ?? throw new InvalidOperationException(
                $"Configuration for SYSTEM '{systemProfile}', ENVIRONMENT '{environment}' is not valid D365 connection JSON.");

        var handler = new D365AuthDelegatingHandler(Options.Create(options))
        {
            // The client outlives a single request, so connections must be recycled on a timer or a
            // DNS change would never be picked up. This is what IHttpClientFactory solves for
            // registered clients; the handler chain here is per-configuration, so it cannot be
            // registered at startup and sets the same lifetime directly instead.
            InnerHandler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }
        };

        _logger?.LogInformation(
            "Built D365 connection for SYSTEM={SystemProfile} ENVIRONMENT={Environment} (config {ConfigHash}).",
            systemProfile, environment, hash[..8]);

        return new CacheEntry(
            new D365Connection(new HttpClient(handler) { Timeout = options.Timeout }, options, hash),
            _time.GetUtcNow());
    }

    private void EvictIdle()
    {
        var cutoff = _time.GetUtcNow() - _idleTimeout;

        foreach (var (key, lazy) in _cache)
        {
            if (!lazy.IsValueCreated || lazy.Value.LastUsedUtc > cutoff) continue;
            if (!_cache.TryRemove(key, out var removed)) continue;

            // Not disposed: a request may still be in flight on this client, and the pooled
            // connection lifetime already bounds how long its sockets survive.
            _logger?.LogInformation("Evicted idle D365 connection (config {ConfigHash}).", key[..8]);
            _ = removed;
        }
    }

    private static string ComputeHash(string json) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

    public void Dispose()
    {
        foreach (var lazy in _cache.Values)
        {
            if (lazy.IsValueCreated) lazy.Value.Connection.Http.Dispose();
        }

        _cache.Clear();
    }

    private sealed class CacheEntry(D365Connection connection, DateTimeOffset lastUsedUtc)
    {
        public D365Connection Connection { get; } = connection;
        public DateTimeOffset LastUsedUtc { get; set; } = lastUsedUtc;
    }
}
