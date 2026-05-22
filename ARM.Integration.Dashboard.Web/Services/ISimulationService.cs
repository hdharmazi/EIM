namespace ARM.Integration.Dashboard.Web.Services;

public interface ISimulationService
{
    Task<HttpResponseMessage> RunAsync(object request, CancellationToken ct = default);
}
