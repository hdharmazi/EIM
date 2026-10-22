using Microsoft.Extensions.Configuration;

namespace Wilson.Integration.Orchestrations.Configuration;

public enum SettingState
{
    Present,
    Missing,
    UnresolvedKeyVaultReference,
}

/// <summary>
/// Checks required settings by name. Never exposes a value: results carry only the key and its state,
/// so they are safe to log or return from a diagnostics endpoint.
/// </summary>
public static class RequiredSettings
{
    public static readonly string[] Database = ["EIMDB"];
    public static readonly string[] ServiceBus = ["ServiceBus"];
    public static readonly string[] Queues =
    [
        "D365FscDataEventQueue",
        "D365BusinessEventQueue",
        "CaptureToD365Queue",
        "CaptureToD365QueueResponse",
        "D365ToCaptureQueue",
    ];
    public static readonly string[] Schedules = ["AwaitingD365SweepSchedule", "ReleaseSweepSchedule", "RetrySweepSchedule"];

    public static SettingState GetState(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value)) return SettingState.Missing;

        return value.StartsWith("@Microsoft.KeyVault", StringComparison.OrdinalIgnoreCase)
            ? SettingState.UnresolvedKeyVaultReference
            : SettingState.Present;
    }

    public static IReadOnlyDictionary<string, SettingState> Inspect(IConfiguration configuration, IEnumerable<string> keys) =>
        keys.Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(k => k, k => GetState(configuration, k), StringComparer.OrdinalIgnoreCase);

    /// <summary>Throws naming every required key that is missing or still an unresolved Key Vault reference.</summary>
    public static void EnsurePresent(IConfiguration configuration, params IEnumerable<string>[] keyGroups)
    {
        var problems = Inspect(configuration, keyGroups.SelectMany(g => g))
            .Where(kv => kv.Value != SettingState.Present)
            .Select(kv => kv.Value == SettingState.Missing ? kv.Key : $"{kv.Key} (unresolved Key Vault reference)")
            .ToList();

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                $"Required configuration is missing: {string.Join(", ", problems)}.");
        }
    }
}
