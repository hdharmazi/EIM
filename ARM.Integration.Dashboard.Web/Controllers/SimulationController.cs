using ARM.Integration.Dashboard.Web.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace ARM.Integration.Dashboard.Web.Controllers;

public class SimulationController(ISimulationService service) : Controller
{
    public IActionResult Index() => View();

    [HttpPost]
    public async Task Run([FromBody] JsonElement body, CancellationToken ct)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"]     = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        using var apiResponse = await service.RunAsync(body, ct);
        apiResponse.EnsureSuccessStatusCode();

        // Stream SSE events from the API directly to the browser without buffering
        await using var stream = await apiResponse.Content.ReadAsStreamAsync(ct);
        await stream.CopyToAsync(Response.Body, ct);
    }
}
