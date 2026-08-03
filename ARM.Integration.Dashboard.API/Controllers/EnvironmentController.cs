using ARM.Integration.Dashboard.API.Models;
using ARM.Integration.Dashboard.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ARM.Integration.Dashboard.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin")]
public class EnvironmentController(EnvironmentRepository repo) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string? system, [FromQuery] string? environment)
    {
        var records = await repo.GetAllAsync(system, environment);
        return Ok(records);
    }

    [HttpGet("{system}/{environment}")]
    public async Task<IActionResult> GetByKey(string system, string environment)
    {
        var record = await repo.GetByKeyAsync(system, environment);
        return record is null ? NotFound() : Ok(record);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] EnvironmentRecord record)
    {
        var existing = await repo.GetByKeyAsync(record.System, record.Environment);
        if (existing is not null)
            return Conflict("A record with this System and Environment already exists.");

        await repo.CreateAsync(record);
        return CreatedAtAction(nameof(GetByKey), new { system = record.System, environment = record.Environment }, record);
    }

    [HttpPut("{system}/{environment}")]
    public async Task<IActionResult> Update(string system, string environment, [FromBody] EnvironmentRecord record)
    {
        var updated = await repo.UpdateAsync(system, environment, record.Value);
        return updated ? NoContent() : NotFound();
    }

    [HttpDelete("{system}/{environment}")]
    public async Task<IActionResult> Delete(string system, string environment)
    {
        var deleted = await repo.DeleteAsync(system, environment);
        return deleted ? NoContent() : NotFound();
    }
}
