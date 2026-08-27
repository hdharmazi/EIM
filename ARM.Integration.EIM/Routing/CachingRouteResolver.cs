using Microsoft.Extensions.Logging;

namespace ARM.Integration.EIM.Routing;

/// <summary>
/// Caches the whole route table for a bounded time.
/// </summary>
/// <remarks>
/// The enabled check runs on every message before anything else, and is a free environment-variable
/// read today. An uncached resolver would add a database round trip per message, so the cache is
/// not an optimisation but a condition of the design being viable at all.
/// <para>
/// The entire table is cached as one snapshot rather than per key. It is a few dozen rows, and a
/// whole-table snapshot means a missing route is known immediately rather than needing negative
/// caching to avoid re-querying for keys that do not exist.
/// </para>
/// </remarks>
public sealed class CachingRouteResolver : IRouteResolver, IDisposable
{
    public static readonly TimeSpan DefaultCacheDuration = TimeSpan.FromSeconds(60);

    private readonly IRouteRepository _repository;
    private readonly TimeSpan _cacheDuration;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CachingRouteResolver>? _logger;
    private readonly SemaphoreSlim _reloadLock = new(1, 1);

    private volatile Snapshot? _snapshot;

    public CachingRouteResolver(
        IRouteRepository repository,
        TimeSpan? cacheDuration = null,
        TimeProvider? timeProvider = null,
        ILogger<CachingRouteResolver>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(repository);

        if (cacheDuration is { } d && d <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cacheDuration), d, "Cache duration must be positive.");

        _repository = repository;
        _cacheDuration = cacheDuration ?? DefaultCacheDuration;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    public async Task<RouteDefinition> ResolveAsync(RouteKey key, CancellationToken cancellationToken = default)
    {
        var routes = await GetRoutesAsync(cancellationToken);

        if (routes.TryGetValue(key, out var route))
        {
            return route;
        }

        // Deliberately no reload-and-retry on a miss. A newly seeded route becomes visible within
        // the cache duration anyway, and pause and resume call Invalidate directly — whereas a
        // flood of genuinely unroutable messages would otherwise reload the table once per message.
        _logger?.LogError("No route registered for {RouteKey}.", key);
        throw new RouteNotFoundException(key);
    }

    public void Invalidate()
    {
        _snapshot = null;
        _logger?.LogInformation("Route cache invalidated.");
    }

    private async Task<IReadOnlyDictionary<RouteKey, RouteDefinition>> GetRoutesAsync(CancellationToken cancellationToken)
    {
        var current = _snapshot;

        return IsFresh(current) ? current!.Routes : await ReloadAsync(cancellationToken);
    }

    private bool IsFresh(Snapshot? snapshot) =>
        snapshot is not null && _timeProvider.GetUtcNow() < snapshot.ExpiresAt;

    private async Task<IReadOnlyDictionary<RouteKey, RouteDefinition>> ReloadAsync(CancellationToken cancellationToken)
    {
        await _reloadLock.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have reloaded while this one waited.
            var current = _snapshot;
            if (IsFresh(current))
            {
                return current!.Routes;
            }

            var routes = await _repository.LoadAllAsync(cancellationToken);
            _snapshot = new Snapshot(routes, _timeProvider.GetUtcNow() + _cacheDuration);

            _logger?.LogDebug("Loaded {Count} routes.", routes.Count);
            return routes;
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    public void Dispose() => _reloadLock.Dispose();

    private sealed record Snapshot(IReadOnlyDictionary<RouteKey, RouteDefinition> Routes, DateTimeOffset ExpiresAt);
}
