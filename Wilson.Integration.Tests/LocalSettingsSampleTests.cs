using System.Text.Json;
using Xunit;

namespace Wilson.Integration.Tests;

/// <summary>
/// Spec integration-host-layout, "Samples target local services": the tracked local.settings.sample.json files point
/// a developer at Azurite and a local eimdb, and never at a deployed environment's database or queues.
/// </summary>
public class LocalSettingsSampleTests
{
    public static TheoryData<string> Samples() => new(
        Path.Combine("Wilson.Integration.Automation", "local.settings.sample.json"),
        Path.Combine("ARM.Integration.D365FSC.Gateway", "local.settings.sample.json"));

    private static Dictionary<string, string> Values(string sample)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Root(), sample)));
        return document.RootElement.GetProperty("Values").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText());
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Storage_is_azurite(string sample)
    {
        Assert.Equal("UseDevelopmentStorage=true", Values(sample)["AzureWebJobsStorage"]);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Eimdb_is_a_local_placeholder(string sample)
    {
        var eimdb = Values(sample)["EIMDB"];

        Assert.Matches("^<.*>$", eimdb);
        Assert.Contains("local", eimdb, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void No_value_targets_a_deployed_environment(string sample)
    {
        foreach (var (key, value) in Values(sample))
        {
            Assert.False(value.Contains("azql-wtc-sandbox", StringComparison.OrdinalIgnoreCase), $"{sample}: {key} names the shared SQL server.");
            Assert.False(value.StartsWith("bat-", StringComparison.OrdinalIgnoreCase), $"{sample}: {key} names a bat queue; the deployed bat app consumes it.");
            Assert.False(value.StartsWith("test-", StringComparison.OrdinalIgnoreCase), $"{sample}: {key} names a test queue; the deployed test app consumes it.");
        }
    }
}
