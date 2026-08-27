namespace ARM.Integration.Dashboard.API.Models;

public class SimulationRequest
{
    public string Scenario { get; set; } = "";
    public Dictionary<string, string> Fields { get; set; } = [];

    /// <summary>Name of the selected <c>SimulationEnvironment</c> configuration whose queue names this run targets.</summary>
    public string Environment { get; set; } = "";
}

public record SimulationEvent(string Level, string Text);

public class SimulationConfigResponse
{
    public string D365FscDataEventQueue { get; set; } = "";
    public string CaptureToD365Queue { get; set; } = "";
    public string CaptureToD365ResponseQueue { get; set; } = "";
    public string D365ToCaptureQueue { get; set; } = "";
    public bool IsServiceBusConfigured { get; set; }
}
