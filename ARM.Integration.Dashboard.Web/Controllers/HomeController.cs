using System.Diagnostics;
using ARM.Integration.Dashboard.Web.Models;
using ARM.Integration.Dashboard.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace ARM.Integration.Dashboard.Web.Controllers;

public class HomeController(IMessageQueueService repo) : Controller
{
    public async Task<IActionResult> Index()
    {
        var vm = new HomeViewModel
        {
            StatusCounts = await repo.GetStatusCountsAsync(),
            RecentFailed = await repo.GetRecentFailedAsync(top: 10)
        };
        return View(vm);
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}

