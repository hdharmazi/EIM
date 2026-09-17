using System.Net.Http.Headers;
using System.Text.Json;
using ARM.Integration.D365FSC.SampleClient.Models;
using Microsoft.Extensions.Options;

namespace ARM.Integration.D365FSC.SampleClient;

/// <summary>Minimal read-only client for the WTC D365 F&amp;O gateway.</summary>
public sealed class D365FscGatewayClient(HttpClient httpClient, IOptions<GatewayOptions> options)
{
    private const string EntityName = "WTC_EngineeringAppStatuses";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly GatewayOptions _options = options.Value;

    /// <summary>The URL of the most recent request, useful when diagnosing a 400 from D365.</summary>
    public string? LastRequestUrl { get; private set; }

    /// <summary>The <c>X-Correlation-Id</c> of the most recent response. Quote this when raising a support ticket.</summary>
    public string? LastCorrelationId { get; private set; }

    public async Task<IReadOnlyList<WtcEngineeringAppStatus>> GetEngineeringAppStatusesAsync(
        DateOnly shippingDateFrom,
        DateOnly shippingDateTo,
        string dataAreaId,
        bool crossCompany = true,
        CancellationToken cancellationToken = default)
    {
        var filter = $"ShippingDateConfirmed ge {shippingDateFrom:yyyy-MM-dd} and " +
                     $"ShippingDateConfirmed le {shippingDateTo:yyyy-MM-dd} and " +
                     $"dataAreaId eq '{dataAreaId}'";

        // The gateway forwards the query string byte-for-byte, so the caller must percent-encode values itself.
        var query = (crossCompany ? "?cross-company=true&" : "?") +
                    $"$filter={Uri.EscapeDataString(filter)}";

        var url = $"{_options.BaseUrl.TrimEnd('/')}/api/v1/d365fsc/" +
                  $"{Uri.EscapeDataString(_options.Environment)}/data/{EntityName}{query}";

        var response = await SendAsync(HttpMethod.Get, url, cancellationToken);

        var payload = JsonSerializer.Deserialize<ODataCollectionResponse<WtcEngineeringAppStatus>>(
            response, JsonOptions);

        return payload?.Value ?? [];
    }

    private async Task<string> SendAsync(HttpMethod method, string url, CancellationToken cancellationToken)
    {
        LastRequestUrl = url;

        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("x-functions-key", _options.FunctionKey);
        request.Headers.TryAddWithoutValidation("X-Consumer-Id", _options.ConsumerId);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await httpClient.SendAsync(request, cancellationToken);

        LastCorrelationId = response.Headers.TryGetValues("X-Correlation-Id", out var values)
            ? values.FirstOrDefault()
            : null;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new GatewayException(response.StatusCode, TryReadErrorCode(body), LastCorrelationId, body);
        }

        return body;
    }

    private static string? TryReadErrorCode(string body)
    {
        // A 401 from the Functions host has no JSON body; only the gateway itself emits the error envelope.
        try
        {
            return JsonSerializer.Deserialize<GatewayError>(body, JsonOptions)?.Error;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
