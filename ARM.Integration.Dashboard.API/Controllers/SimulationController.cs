using ARM.Integration.Dashboard.API.Models;
using ARM.Integration.Dashboard.API.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace ARM.Integration.Dashboard.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SimulationController(SimulationService service) : ControllerBase
{
    private static readonly JsonSerializerOptions _camelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [HttpPost("run")]
    public async Task Run([FromBody] SimulationRequest request, CancellationToken ct)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"]     = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        await foreach (var evt in service.RunAsync(request, ct))
        {
            await Response.WriteAsync($"data: {JsonSerializer.Serialize(evt, _camelCase)}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }
    }
}
