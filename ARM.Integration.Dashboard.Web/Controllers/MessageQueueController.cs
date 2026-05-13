using ARM.Integration.Dashboard.Web.Models;
using ARM.Integration.Dashboard.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace ARM.Integration.Dashboard.Web.Controllers;

public class MessageQueueController(IMessageQueueService repo) : Controller
{
    public async Task<IActionResult> Index(int? status, string? search)
    {
        var messages = await repo.GetAllAsync(statusFilter: status, search: search);
        var vm = new MessageQueueViewModel
        {
            Messages = messages,
            StatusFilter = status,
            SearchTerm = search,
            TotalCount = messages.Count
        };
        return View(vm);
    }

    public async Task<IActionResult> Detail(int id)
    {
        var record = await repo.GetByIdAsync(id);
        if (record is null) return NotFound();

        var logDetails = await repo.GetLogDetailsAsync(id);

        var vm = new MessageDetailViewModel
        {
            Message = record,
            LogDetails = logDetails
        };

        return View(vm);
    }

    [HttpPost]
    public async Task<IActionResult> UpdatePayload(long id, [FromForm] string payload)
    {
        var success = await repo.UpdatePayloadAsync(id, payload);
        TempData["ReprocessSuccess"] = success;
        TempData["ReprocessMessage"] = success ? "Payload updated successfully." : "Failed to update payload.";
        return RedirectToAction(nameof(Detail), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Reprocess(long id)
    {
        var (success, message) = await repo.ReprocessAsync(id);
        TempData["ReprocessSuccess"] = success;
        TempData["ReprocessMessage"] = message;
        return RedirectToAction(nameof(Detail), new { id });
    }

    public async Task<IActionResult> Cleanup()
    {
        var statusCounts = await repo.GetStatusCountsAsync();
        ViewData["StatusCounts"] = statusCounts;
        return View();
    }

    [HttpPost]
    public async Task<IActionResult> CleanupConfirm(int? status, int? olderThanDays)
    {
        var (success, deleted, message) = await repo.CleanupAsync(status, olderThanDays);
        TempData["CleanupSuccess"] = success;
        TempData["CleanupMessage"] = message;
        return RedirectToAction(nameof(Cleanup));
    }
}
