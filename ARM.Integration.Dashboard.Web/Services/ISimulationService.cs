using ARM.Integration.Dashboard.Web.Models;

namespace ARM.Integration.Dashboard.Web.Services;

public interface ISimulationService
{
    Task<SimulationConfigViewModel> GetConfigAsync(CancellationToken ct = default);
    Task<HttpResponseMessage> RunAsync(object request, CancellationToken ct = default);
}
