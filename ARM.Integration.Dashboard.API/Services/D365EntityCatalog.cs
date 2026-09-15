using System.Collections.Concurrent;
using System.Text.Json;
using ARM.Integration.Core.Helpers;

namespace ARM.Integration.Dashboard.API.Services;

/// <summary>
/// Lists the data entities an environment exposes, read from D365's OData service document.
/// </summary>
/// <remarks>
/// This exists so grants are picked rather than typed. A typed entity name that is wrong produces a
/// grant that looks correct in the UI and silently never matches, and one that is merely mis-cased
/// authorizes correctly and then 404s at D365, because entity names are case-sensitive in the URL.
/// Selecting from the service document makes both impossible and stores D365's own spelling.
///
/// The service document (<c>GET data/</c>) is used rather than <c>$metadata</c>, which describes
/// every property of every entity and runs to several megabytes.
/// </remarks>
public class D365EntityCatalog(EnvironmentRepository environments, ILogger<D365EntityCatalog> logger)
{
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(1);

    public async Task<IReadOnlyList<string>?> GetEntitiesAsync(string systemProfile, string environment)
    {
        var key = $"{systemProfile}|{environment}".ToUpperInvariant();

        if (Cache.TryGetValue(key, out var cached) && cached.ExpiresOn > DateTimeOffset.UtcNow)
        {
            return cached.Entities;
        }

        var record = await environments.GetByKeyAsync(systemProfile, environment);
        if (record?.Value is null) return null;

        try
        {
            var client = D365FSCServiceCollectionExtensions.CreateStandaloneClient(record.Value);
            var document = await client.QueryRawAsync(string.Empty, string.Empty);

            var entities = ParseServiceDocument(document);
            Cache[key] = new CacheEntry(entities, DateTimeOffset.UtcNow.Add(CacheLifetime));
            return entities;
        }
        catch (Exception ex)
        {
            // Surfaced as a failure rather than an empty list: silently offering no entities would
            // look like an environment with nothing in it.
            logger.LogError(ex, "Could not read the service document for {Profile}/{Environment}.",
                systemProfile, environment);
            throw;
        }
    }

    private static List<string> ParseServiceDocument(string json)
    {
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("value", out var value))
        {
            return [];
        }

        return value.EnumerateArray()
            .Select(item => item.TryGetProperty("name", out var name) ? name.GetString() : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed record CacheEntry(IReadOnlyList<string> Entities, DateTimeOffset ExpiresOn);
}
