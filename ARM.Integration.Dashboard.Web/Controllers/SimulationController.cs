using ARM.Integration.Dashboard.Web.Services;
using ARM.Integration.Dashboard.Web.Models;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace ARM.Integration.Dashboard.Web.Controllers;

public class SimulationController(ISimulationService service) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var config = await service.GetConfigAsync(ct);
        var vm = new SimulationPageViewModel { Config = config };
        return View(vm);
    }

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

    [HttpPost]
    public async Task<IActionResult> ParseSalesOrder(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest("No file uploaded.");

        await using var stream = file.OpenReadStream();
        using var apiResponse = await service.ParseSalesOrderAsync(stream, file.FileName, ct);

        var json = await apiResponse.Content.ReadAsStringAsync(ct);
        return new ContentResult
        {
            Content = json,
            ContentType = "application/json",
            StatusCode = (int)apiResponse.StatusCode
        };
    }
}
