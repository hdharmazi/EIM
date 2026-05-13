using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace ARM.Integration.Core.Helpers;

/// <summary>
/// Stateless, DI-friendly D365 F&amp;SC OData client.
/// All per-request state (entity keys, payloads) is passed as method parameters.
/// </summary>
public sealed class D365FSCClient : ID365FSCClient
{
    private readonly HttpClient     _http;
    private readonly D365FSCOptions _options;
    private readonly ILogger<D365FSCClient> _logger;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public D365FSCClient(
        HttpClient httpClient,
        IOptions<D365FSCOptions> options,
        ILogger<D365FSCClient> logger)
    {
        _http    = httpClient;
        _options = options.Value;
        _logger  = logger;

        _http.BaseAddress ??= new Uri($"{_options.BaseUrl}/{_options.Namespace}");
        _http.Timeout = _options.Timeout;
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    public async Task<T> CreateAsync<T>(string entity, T payload, CancellationToken ct = default)
    {
        _logger.LogDebug("D365 Create: entity={Entity}", entity);

        using var response = await _http.PostAsJsonAsync(
            $"data/{entity}", payload, _jsonOptions, ct);

        await EnsureSuccessAsync(response, ct);

        var result = await response.Content.ReadFromJsonAsync<T>(_jsonOptions, ct)
            ?? throw new D365FSCException("Null response body on create.", response.StatusCode);

        _logger.LogDebug("D365 Create: succeeded for entity={Entity}", entity);
        return result;
    }

    public async Task<string> CreateRawAsync(string entity, string jsonPayload, CancellationToken ct = default)
    {
        _logger.LogDebug("D365 CreateRaw: entity={Entity}", entity);

        using var response = await _http.PostAsync(
            $"data/{entity}",
            new StringContent(jsonPayload, Encoding.UTF8, "application/json"), ct);

        await EnsureSuccessAsync(response, ct);
        var result = await response.Content.ReadAsStringAsync(ct);

        _logger.LogDebug("D365 CreateRaw: succeeded for entity={Entity}", entity);
        return result;
    }

    public async Task UpdateAsync<T>(
        string entity, EntityKey key, T payload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        _logger.LogDebug("D365 Update: entity={Entity} keys={Keys}", entity, key);

        using var request = new HttpRequestMessage(HttpMethod.Patch,
            $"data/{entity}({key}){CrossCompanySegment()}")
        {
            Content = JsonContent.Create(payload, options: _jsonOptions)
        };

        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);

        _logger.LogDebug("D365 Update: succeeded for entity={Entity}", entity);
    }

    public async Task<string> UpdateRawAsync(
        string entity, EntityKey key, string jsonPayload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        _logger.LogDebug("D365 UpdateRaw: entity={Entity} keys={Keys}", entity, key);

        using var request = new HttpRequestMessage(HttpMethod.Patch,
            $"data/{entity}({key}){CrossCompanySegment()}")
        {
            Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
        };

        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        var result = await response.Content.ReadAsStringAsync(ct);

        _logger.LogDebug("D365 UpdateRaw: succeeded for entity={Entity}", entity);
        return result;
    }

    public async Task DeleteAsync(string entity, EntityKey key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        _logger.LogInformation("D365 Delete: entity={Entity} keys={Keys}", entity, key);

        using var response = await _http.DeleteAsync(
            $"data/{entity}({key}){CrossCompanySegment()}", ct);

        await EnsureSuccessAsync(response, ct);

        _logger.LogInformation("D365 Delete: succeeded for entity={Entity} keys={Keys}", entity, key);
    }

    // ── Queries ───────────────────────────────────────────────────────────────

    public async Task<string> QueryRawAsync(
        string entity, string odataQuery, CancellationToken ct = default)
    {
        _logger.LogDebug("D365 QueryRaw: entity={Entity} query={Query}", entity, odataQuery);

        using var response = await _http.GetAsync($"data/{entity}{odataQuery}", ct);
        await EnsureSuccessAsync(response, ct);

        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<D365ODataResponse<T>> QueryAsync<T>(
        string entity, string? filter = null, CancellationToken ct = default)
    {
        _logger.LogDebug("D365 Query: entity={Entity} filter={Filter}", entity, filter);

        string filterClause = filter is not null ? $"&$filter={filter}" : string.Empty;
        string uri = $"data/{entity}{CrossCompanySegment()}{filterClause}";

        using var response = await _http.GetAsync(uri, ct);
        await EnsureSuccessAsync(response, ct);

        return await response.Content.ReadFromJsonAsync<D365ODataResponse<T>>(_jsonOptions, ct)
            ?? new D365ODataResponse<T>();
    }

    public async Task<T> GetByKeyAsync<T>(
        string entity, EntityKey key, CancellationToken ct = default)
    {
        _logger.LogDebug("D365 GetByKey: entity={Entity} keys={Keys}", entity, key);

        using var response = await _http.GetAsync(
            $"data/{entity}({key}){CrossCompanySegment()}", ct);

        await EnsureSuccessAsync(response, ct);

        return await response.Content.ReadFromJsonAsync<T>(_jsonOptions, ct)
            ?? throw new D365FSCException("Null response body.", response.StatusCode);
    }

    // ── Custom API / Service calls ────────────────────────────────────────────

    public async Task<string> CallServiceAsync(
        string method, string payload, CancellationToken ct = default)
    {
        _logger.LogDebug("D365 Service POST: {Method}", method);

        using var response = await _http.PostAsync(method,
            new StringContent(payload, Encoding.UTF8, "application/json"), ct);

        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<string> PostRecurringDataAsync(
        string method, BinaryData data, CancellationToken ct = default)
    {
        _logger.LogDebug("D365 RecurringData POST: {Method}", method);

        using var content = new ByteArrayContent(data.ToArray());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        using var response = await _http.PostAsync(method, content, ct);

        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private string CrossCompanySegment() =>
        _options.CrossCompany ? "?cross-company=true" : string.Empty;

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(ct);

        _logger.LogError("D365 request failed: {StatusCode} {Reason} — {Body}",
            (int)response.StatusCode, response.ReasonPhrase, body);

        string message    = body;
        string? errorCode = null;
        try
        {
            var foError = JsonSerializer.Deserialize<FOError>(body, _jsonOptions);
            if (foError?.error?.message is not null)
            {
                errorCode = foError.error.code;
                message   = foError.error.innererror?.internalexception?.message
                         ?? foError.error.innererror?.message
                         ?? foError.error.message;
            }
        }
        catch (JsonException) { /* use raw body */ }

        throw new D365FSCException(message, response.StatusCode, errorCode, body);
    }
}
