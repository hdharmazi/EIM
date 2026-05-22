using System.Net.Http.Json;

namespace ARM.Integration.Dashboard.Web.Services;

public class SimulationApiService(HttpClient httpClient) : ISimulationService
{
    public async Task<HttpResponseMessage> RunAsync(object request, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "api/simulation/run")
        {
            Content = JsonContent.Create(request)
        };

        // ResponseHeadersRead is critical for SSE — do NOT buffer the whole stream
        return await httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }
}
