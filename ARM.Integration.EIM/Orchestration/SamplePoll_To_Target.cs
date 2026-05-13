using System.Text.Json;

namespace ARM.Integration.EIM.Orchestration;

/// <summary>
/// SAMPLE: Demonstrates a polling-based forward orchestration.
/// 
/// This orchestration receives raw JSON from a source API (via PollSourceHandler),
/// maps it to the destination format, and forwards to a queue.
/// 
/// Unlike D365EventForwardOrchestration, there's no EventContext parsing or change tracking —
/// the source API filter already returns only relevant records.
/// 
/// To create a real polling orchestration:
///   1. Copy this file and rename the class
///   2. Define your source/destination contracts (TSource / TDest)
///   3. Implement ParseAndMap to deserialize and map
///   4. Register in Program.cs DI dictionary
///   5. Set the "PollOrchestration" setting to match the dictionary key
/// </summary>
public class SamplePoll_To_Target : BaseForwardOrchestration<SampleTargetContract>
{
    protected override string EntityName => "Customer";

    protected override (string action, bool hasTrackedChanges, SampleTargetContract mappedData, string? sourceTimestamp)
        ParseAndMap(string payload)
    {
        // Payload is the raw JSON record from the source API
        var source = JsonSerializer.Deserialize<SampleSourceContract>(payload)
            ?? throw new JsonException("Source record is null.");

        // All polled records are "changes" — no change tracking needed
        var mapped = new SampleTargetContract
        {
            Name = source.Name,
            Email = source.Email,
            ExternalId = source.Id,
        };

        // Determine action based on source data (customize per integration)
        var action = string.IsNullOrWhiteSpace(source.DeletedAt) ? "New" : "Delete";

        return (action, true, mapped, source.ModifiedAt);
    }
}

// ── Sample contracts — replace with real ones per integration ─────────

/// <summary>Source API record structure (customize per source system).</summary>
public class SampleSourceContract
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Email { get; set; }
    public string? ModifiedAt { get; set; }
    public string? DeletedAt { get; set; }
}

/// <summary>Destination message contract (customize per target system).</summary>
public class SampleTargetContract
{
    public string? ExternalId { get; set; }
    public string? Name { get; set; }
    public string? Email { get; set; }
}
