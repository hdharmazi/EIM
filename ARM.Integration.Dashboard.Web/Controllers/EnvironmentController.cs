using ARM.Integration.Dashboard.Web.Models;
using ARM.Integration.Dashboard.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace ARM.Integration.Dashboard.Web.Controllers;

public class EnvironmentController(IEnvironmentService service) : Controller
{
    public async Task<IActionResult> Index(string? system, string? environment)
    {
        var records = await service.GetAllAsync(system: system, environment: environment);
        return View(records);
    }

    public IActionResult Create() => View(new EnvironmentRecord());

    [HttpPost]
    public async Task<IActionResult> Create(EnvironmentRecord record)
    {
        if (!ModelState.IsValid) return View(record);
        await service.CreateAsync(record);
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Edit(string system, string environment)
    {
        var record = await service.GetByKeyAsync(system, environment);
        if (record is null) return NotFound();
        return View(record);
    }

    [HttpPost]
    public async Task<IActionResult> Edit(string system, string environment, EnvironmentRecord record)
    {
        if (!ModelState.IsValid) return View(record);
        await service.UpdateAsync(system, environment, record.Value);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Delete(string system, string environment)
    {
        await service.DeleteAsync(system, environment);
        return RedirectToAction(nameof(Index));
    }
}
