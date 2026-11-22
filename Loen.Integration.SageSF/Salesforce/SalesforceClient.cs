using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Loen.Integration.SageSF.Salesforce;

/// <summary>
/// Thin Salesforce REST client: OAuth JWT bearer, SOQL, sObject Collections UPSERT, Bulk API 2.0.
/// </summary>
public sealed class SalesforceClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly SalesforceAuthConfig _authConfig;
    private string? _accessToken;
    private string? _instanceUrl;

    public SalesforceClient(HttpClient http, SalesforceAuthConfig authConfig)
    {
        _http = http;
        _authConfig = authConfig;
    }

    public async Task AuthenticateAsync(CancellationToken ct = default)
    {
        // JWT bearer flow — no interactive login
        // TODO M1: implement JWT assertion signing with the connected app's certificate/key
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
            ["assertion"] = BuildJwtAssertion(),
        });

        var response = await _http.PostAsync(_authConfig.TokenUrl, form, ct);
        response.EnsureSuccessStatusCode();

        var token = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        _accessToken = token.GetProperty("access_token").GetString();
        _instanceUrl = token.GetProperty("instance_url").GetString();

        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _accessToken);
    }

    /// <summary>Runs a SOQL query and returns the records.</summary>
    public async Task<JsonElement> QueryAsync(string soql, CancellationToken ct = default)
    {
        var url = $"{_instanceUrl}/services/data/v62.0/query?q={Uri.EscapeDataString(soql)}";
        var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    /// <summary>Runs queryAll (includes deleted records) for detecting SF deletes.</summary>
    public async Task<JsonElement> QueryAllAsync(string soql, CancellationToken ct = default)
    {
        var url = $"{_instanceUrl}/services/data/v62.0/queryAll?q={Uri.EscapeDataString(soql)}";
        var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    /// <summary>
    /// sObject Collections UPSERT — up to 200 records per call.
    /// Uses external ID field for matching.
    /// </summary>
    public async Task<JsonElement> CompositeUpsertAsync(
        string objectName, string externalIdField,
        IReadOnlyList<Dictionary<string, object?>> records,
        CancellationToken ct = default)
    {
        var url = $"{_instanceUrl}/services/data/v62.0/composite/sobjects/{objectName}/{externalIdField}";

        var payload = new { allOrNone = false, records };
        var response = await _http.PatchAsJsonAsync(url, payload, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private string BuildJwtAssertion()
    {
        // TODO M1: sign a JWT with the connected app's private key
        throw new NotImplementedException(
            "JWT assertion signing not yet implemented. " +
            "Requires the connected app's private key (see SF dev org setup).");
    }

    public void Dispose() => _http.Dispose();
}

public sealed record SalesforceAuthConfig
{
    public required string TokenUrl { get; init; }
    public required string ClientId { get; init; }
    public required string Username { get; init; }
    public required string PrivateKeyPath { get; init; }
}

public sealed class SalesforceClientFactory
{
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpFactory;

    public SalesforceClientFactory(IConfiguration config, IHttpClientFactory httpFactory)
    {
        _config = config;
        _httpFactory = httpFactory;
    }

    public SalesforceClient Create(string environment)
    {
        var section = _config.GetSection($"Salesforce:{environment}");
        var authConfig = new SalesforceAuthConfig
        {
            TokenUrl = section["TokenUrl"] ?? "https://login.salesforce.com/services/oauth2/token",
            ClientId = section["ClientId"] ?? throw new InvalidOperationException("Salesforce ClientId is not configured."),
            Username = section["Username"] ?? throw new InvalidOperationException("Salesforce Username is not configured."),
            PrivateKeyPath = section["PrivateKeyPath"] ?? throw new InvalidOperationException("Salesforce PrivateKeyPath is not configured."),
        };

        var http = _httpFactory.CreateClient("Salesforce");
        return new SalesforceClient(http, authConfig);
    }
}
