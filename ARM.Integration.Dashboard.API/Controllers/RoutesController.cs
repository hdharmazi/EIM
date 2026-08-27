using ARM.Integration.Dashboard.API.Auth;
using ARM.Integration.Dashboard.API.Services;
using ARM.Integration.EIM.Routing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ARM.Integration.Dashboard.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = AuthorizationPolicies.DashboardUser)]
public class RoutesController(
    RouteRepository repo,
    ILogger<RoutesController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await repo.GetAllAsync());

    [HttpGet("orphans")]
    public async Task<IActionResult> GetOrphans() => Ok(await repo.GetOrphanedBacklogAsync());

    [HttpPost("pause")]
    public Task<IActionResult> Pause([FromBody] RouteControlRequest request) => SetEnabled(request, enabled: false);

    [HttpPost("resume")]
    public Task<IActionResult> Resume([FromBody] RouteControlRequest request) => SetEnabled(request, enabled: true);

    private async Task<IActionResult> SetEnabled(RouteControlRequest request, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(request.Environment)
            || string.IsNullOrWhiteSpace(request.SourceSystem)
            || string.IsNullOrWhiteSpace(request.SourceEntity))
        {
            return BadRequest(new { success = false, message = "Environment, source system and source entity are required." });
        }

        var key = new RouteKey(request.Environment, request.SourceSystem, request.SourceEntity);

        // Recorded so a paused route can be traced back to whoever paused it.
        var actor = User.Identity?.Name ?? "unknown";

        if (!await repo.SetEnabledAsync(key, enabled, actor))
        {
            return NotFound(new { success = false, message = $"No route registered for '{key}'." });
        }

        logger.LogWarning("Route {RouteKey} {Action} by {Actor}.", key, enabled ? "resumed" : "paused", actor);

        return Ok(new { success = true, message = $"Route '{key}' {(enabled ? "resumed" : "paused")}." });
    }
}

public sealed record RouteControlRequest(string Environment, string SourceSystem, string SourceEntity);
