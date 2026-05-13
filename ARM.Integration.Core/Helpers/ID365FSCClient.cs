namespace ARM.Integration.Core.Helpers;

/// <summary>
/// Abstraction for D365 Finance &amp; Supply Chain OData operations.
/// Inject this interface wherever D365 access is needed.
/// </summary>
public interface ID365FSCClient
{
    // ── CRUD ──────────────────────────────────────────────────────────────────

    /// <summary>Creates an entity record and returns the deserialized response.</summary>
    Task<T> CreateAsync<T>(string entity, T payload, CancellationToken ct = default);

    /// <summary>Creates an entity record and returns the raw JSON response.</summary>
    Task<string> CreateRawAsync(string entity, string jsonPayload, CancellationToken ct = default);

    /// <summary>Updates an entity record identified by <paramref name="key"/>.</summary>
    Task UpdateAsync<T>(string entity, EntityKey key, T payload, CancellationToken ct = default);

    /// <summary>Updates an entity record and returns the raw JSON response.</summary>
    Task<string> UpdateRawAsync(string entity, EntityKey key, string jsonPayload, CancellationToken ct = default);

    /// <summary>Deletes an entity record identified by <paramref name="key"/>.</summary>
    Task DeleteAsync(string entity, EntityKey key, CancellationToken ct = default);

    // ── Queries ───────────────────────────────────────────────────────────────

    /// <summary>Runs a raw OData query string and returns the raw JSON response.</summary>
    Task<string> QueryRawAsync(string entity, string odataQuery, CancellationToken ct = default);

    /// <summary>Gets records with an optional OData $filter and returns deserialized collection.</summary>
    Task<D365ODataResponse<T>> QueryAsync<T>(string entity, string? filter = null, CancellationToken ct = default);

    /// <summary>Gets a single record by key.</summary>
    Task<T> GetByKeyAsync<T>(string entity, EntityKey key, CancellationToken ct = default);

    // ── Custom API / Service calls ────────────────────────────────────────────

    /// <summary>POSTs to a custom D365 service endpoint.</summary>
    Task<string> CallServiceAsync(string method, string payload, CancellationToken ct = default);

    /// <summary>POSTs binary data to a recurring data project endpoint.</summary>
    Task<string> PostRecurringDataAsync(string method, BinaryData data, CancellationToken ct = default);
}
