using ARM.Integration.Dashboard.Web.Models;
using ARM.Integration.Dashboard.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ARM.Integration.Dashboard.Web.Controllers;

[Authorize(Roles = "Admin")]
public class UserController(IUserService service) : Controller
{
    public async Task<IActionResult> Index()
    {
        var users = await service.GetAllAsync();
        return View(users);
    }

    public IActionResult Create() => View(new AppUser());

    [HttpPost]
    public async Task<IActionResult> Create(AppUser user)
    {
        if (!ModelState.IsValid) return View(user);
        user.Email = user.Email.Trim().ToLowerInvariant();
        await service.CreateAsync(user);
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Edit(string email)
    {
        var user = await service.GetByEmailAsync(email);
        if (user is null) return NotFound();
        return View(user);
    }

    [HttpPost]
    public async Task<IActionResult> Edit(string email, AppUser user)
    {
        if (!ModelState.IsValid) return View(user);
        await service.UpdateAsync(email, user);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Delete(string email)
    {
        await service.DeleteAsync(email);
        return RedirectToAction(nameof(Index));
    }
}
