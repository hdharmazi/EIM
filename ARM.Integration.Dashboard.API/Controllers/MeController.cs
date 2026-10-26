using System.Security.Claims;
using ARM.Integration.Dashboard.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ARM.Integration.Dashboard.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class MeController(IConfiguration config) : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        var user = User;
        var name = user.FindFirst("DisplayName")?.Value
                ?? user.Identity?.Name
                ?? user.FindFirst("preferred_username")?.Value;

        return Ok(new
        {
            name,
            roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray(),
            dbUserValidated = user.FindFirst("DbUserValidated")?.Value == "true",
            // What this deployment offers, so the SPA hides what the API does not serve.
            features = new { simulation = SimulationFeature.IsEnabled(config) },
        });
    }
}
