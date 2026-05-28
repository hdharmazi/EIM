namespace ARM.Integration.Dashboard.API.Models;

public class SimulationRequest
{
    public string Scenario { get; set; } = "";
    public Dictionary<string, string> Fields { get; set; } = [];
}

public record SimulationEvent(string Level, string Text);

public class SimulationConfigResponse
{
    public string D365Queue { get; set; } = "";
    public string CaptureToD365Queue { get; set; } = "";
    public string CaptureToD365ResponseQueue { get; set; } = "";
    public string D365ToCaptureQueue { get; set; } = "";
    public bool IsServiceBusConfigured { get; set; }
}
