using ARM.Integration.Dashboard.API.Auth;
using ARM.Integration.Dashboard.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ARM.Integration.Dashboard.API.Controllers;

/// <summary>
/// The message status catalogue, so the dashboard renders labels from data rather than a
/// hardcoded map that has to be edited in three deployables whenever a status is added.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = AuthorizationPolicies.DashboardUser)]
public class StatusesController(StatusRepository repo) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await repo.GetAllAsync());
}
