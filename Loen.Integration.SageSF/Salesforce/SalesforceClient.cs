using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

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

    public async Task<JsonElement> QueryAsync(string soql, CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var url = $"{_instanceUrl}/services/data/v62.0/query?q={Uri.EscapeDataString(soql)}";
        var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    public async Task<JsonElement> QueryAllAsync(string soql, CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var url = $"{_instanceUrl}/services/data/v62.0/queryAll?q={Uri.EscapeDataString(soql)}";
        var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    /// <summary>
    /// sObject Collections UPSERT — up to 200 records per call.
    /// </summary>
    public async Task<JsonElement> CompositeUpsertAsync(
        string objectName, string externalIdField,
        IReadOnlyList<Dictionary<string, object?>> records,
        CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var url = $"{_instanceUrl}/services/data/v62.0/composite/sobjects/{objectName}/{externalIdField}";

        var payload = new { allOrNone = false, records };
        var response = await _http.PatchAsJsonAsync(url, payload, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private string BuildJwtAssertion()
    {
        var rsa = RSA.Create();
        var keyPem = File.ReadAllText(_authConfig.PrivateKeyPath);
        rsa.ImportFromPem(keyPem);

        var signingCredentials = new SigningCredentials(
            new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256);

        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _authConfig.ClientId,
            Subject = new System.Security.Claims.ClaimsIdentity(
            [
                new("sub", _authConfig.Username),
            ]),
            Audience = _authConfig.TokenUrl.Replace("/services/oauth2/token", ""),
            Expires = now.AddMinutes(3),
            SigningCredentials = signingCredentials,
        };

        var handler = new JsonWebTokenHandler();
        return handler.CreateToken(descriptor);
    }

    private void EnsureAuthenticated()
    {
        if (_accessToken is null || _instanceUrl is null)
            throw new InvalidOperationException("Call AuthenticateAsync before making API requests.");
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
