using System.Net.Http.Json;
using ARM.Integration.Dashboard.Web.Models;

namespace ARM.Integration.Dashboard.Web.Services;

public class SimulationApiService(HttpClient httpClient) : ISimulationService
{
    public async Task<SimulationConfigViewModel> GetConfigAsync(CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<SimulationConfigViewModel>("api/simulation/config", ct)
            ?? new SimulationConfigViewModel();
    }

    public async Task<HttpResponseMessage> RunAsync(object request, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "api/simulation/run")
        {
            Content = JsonContent.Create(request)
        };

        // ResponseHeadersRead is critical for SSE — do NOT buffer the whole stream
        return await httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    public async Task<HttpResponseMessage> ParseSalesOrderAsync(Stream fileStream, string fileName, CancellationToken ct = default)
    {
        var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        content.Add(fileContent, "file", fileName);

        return await httpClient.PostAsync("api/simulation/salesorder/parse", content, ct);
    }
}
