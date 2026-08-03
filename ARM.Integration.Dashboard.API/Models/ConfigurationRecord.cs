namespace ARM.Integration.Dashboard.API.Models;

/// <summary>
/// A named configuration in the generic, reusable configuration store.
/// Grouped by <see cref="Category"/>; editable fields are dynamic key/value pairs
/// persisted as a JSON object (no fixed schema), so any feature/project can define
/// its own fields without database changes.
/// </summary>
public class ConfigurationRecord
{
    public string Category { get; set; } = "";
    public string Name { get; set; } = "";
    public string? DisplayName { get; set; }
    public Dictionary<string, string> Fields { get; set; } = [];
    public bool IsActive { get; set; } = true;
}

/// <summary>Well-known configuration categories.</summary>
public static class ConfigurationCategories
{
    /// <summary>Simulation console target environments (Service Bus queue-name sets).</summary>
    public const string SimulationEnvironment = "SimulationEnvironment";
}
