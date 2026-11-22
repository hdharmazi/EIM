namespace Loen.Integration.SageSF.Worker;

/// <summary>
/// Per-entity sync configuration, read from appsettings.json Sync:Entities section.
/// </summary>
public sealed record SyncEntityConfig
{
    public required string Name { get; init; }
    public bool SageToSfEnabled { get; init; } = true;
    public bool SfToSageEnabled { get; init; }
    public int IntervalSec { get; init; } = 300;
    public int OverlapSec { get; init; } = 300;
    public string BoisMode { get; init; } = "off";
    public string SageOrchestration { get; init; } = "";
    public string SfOrchestration { get; init; } = "";

    public static IReadOnlyList<SyncEntityConfig> GetAll(IConfiguration config)
    {
        var section = config.GetSection("Sync:Entities");
        var entities = new List<SyncEntityConfig>();
        foreach (var child in section.GetChildren())
        {
            entities.Add(new SyncEntityConfig
            {
                Name = child.GetValue<string>("Name") ?? child.Key,
                SageToSfEnabled = child.GetValue("SageToSfEnabled", true),
                SfToSageEnabled = child.GetValue("SfToSageEnabled", false),
                IntervalSec = child.GetValue("IntervalSec", 300),
                OverlapSec = child.GetValue("OverlapSec", 300),
                BoisMode = child.GetValue("BoiMode", "off") ?? "off",
            });
        }
        return entities;
    }
}
