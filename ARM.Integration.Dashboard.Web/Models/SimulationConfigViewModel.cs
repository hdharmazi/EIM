namespace ARM.Integration.Dashboard.Web.Models;

public class SimulationConfigViewModel
{
    public string D365Queue { get; set; } = "";
    public string CaptureToD365Queue { get; set; } = "";
    public string CaptureToD365ResponseQueue { get; set; } = "";
    public string D365ToCaptureQueue { get; set; } = "";
    public bool IsServiceBusConfigured { get; set; }
}
