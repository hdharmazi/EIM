using ARM.Integration.Dashboard.API.Auth;
using ARM.Integration.Dashboard.API.Models;
using ARM.Integration.Dashboard.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ARM.Integration.Dashboard.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = AuthorizationPolicies.DashboardUser)]
public class ConfigurationController(ConfigurationRepository repo) : ControllerBase
{
    /// <summary>Lists active configurations in a category. Available to any validated user (used by selection UIs).</summary>
    [HttpGet]
    public async Task<IActionResult> GetByCategory([FromQuery] string category)
    {
        if (string.IsNullOrWhiteSpace(category))
            return BadRequest("A 'category' query parameter is required.");

        var records = await repo.GetByCategoryAsync(category, activeOnly: true);
        return Ok(records);
    }

    [HttpGet("{category}/{name}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetByKey(string category, string name)
    {
        var record = await repo.GetByKeyAsync(category, name);
        return record is null ? NotFound() : Ok(record);
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create([FromBody] ConfigurationRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.Category) || string.IsNullOrWhiteSpace(record.Name))
            return BadRequest("Category and Name are required.");

        var existing = await repo.GetByKeyAsync(record.Category, record.Name);
        if (existing is not null)
            return Conflict("A configuration with this Category and Name already exists.");

        await repo.CreateAsync(record);
        return CreatedAtAction(nameof(GetByKey), new { category = record.Category, name = record.Name }, record);
    }

    [HttpPut("{category}/{name}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(string category, string name, [FromBody] ConfigurationRecord record)
    {
        record.Category = category;
        record.Name = name;
        var updated = await repo.UpdateAsync(record);
        return updated ? NoContent() : NotFound();
    }

    [HttpDelete("{category}/{name}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(string category, string name)
    {
        var deleted = await repo.DeleteAsync(category, name);
        return deleted ? NoContent() : NotFound();
    }
}
