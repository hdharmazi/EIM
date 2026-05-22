namespace ARM.Integration.Dashboard.API.Models;

public class SimulationRequest
{
    public string Scenario { get; set; } = "";
    public Dictionary<string, string> Fields { get; set; } = [];
}

public record SimulationEvent(string Level, string Text);
