using ARM.Integration.Dashboard.API.Models;
using ARM.Integration.Dashboard.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace ARM.Integration.Dashboard.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class UserController(UserRepository repo) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var users = await repo.GetAllAsync();
        return Ok(users);
    }

    [HttpGet("{email}")]
    public async Task<IActionResult> GetByEmail(string email)
    {
        var user = await repo.GetByEmailAsync(email);
        return user is null ? NotFound() : Ok(user);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] AppUser user)
    {
        var existing = await repo.GetByEmailAsync(user.Email);
        if (existing is not null)
            return Conflict("A user with this email already exists.");

        await repo.CreateAsync(user);
        return CreatedAtAction(nameof(GetByEmail), new { email = user.Email }, user);
    }

    [HttpPut("{email}")]
    public async Task<IActionResult> Update(string email, [FromBody] AppUser user)
    {
        var updated = await repo.UpdateAsync(email, user);
        return updated ? NoContent() : NotFound();
    }

    [HttpDelete("{email}")]
    public async Task<IActionResult> Delete(string email)
    {
        var deleted = await repo.DeleteAsync(email);
        return deleted ? NoContent() : NotFound();
    }
}
