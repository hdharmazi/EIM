using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Wilson.Integration.Orchestrations.Configuration;
using Xunit;

namespace Wilson.Integration.Tests;

/// <summary>Spec integration-host-layout, "Fail-fast configuration validation", for Wilson.Integration.Automation.</summary>
public class RequiredSettingsTests
{
    private static readonly string[][] AutomationGroups =
        [RequiredSettings.Database, RequiredSettings.ServiceBus, RequiredSettings.Queues, RequiredSettings.Schedules];

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static IConfiguration AllPresent(params string[] except) =>
        Config(AutomationGroups.SelectMany(g => g)
            .Where(k => !except.Contains(k))
            .Select(k => (k, (string?)$"value-of-{k}"))
            .ToArray());

    private static IReadOnlyList<string> SampleKeys()
    {
        var path = Path.Combine(RepositoryPaths.Root(), "Wilson.Integration.Automation", "local.settings.sample.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("Values").EnumerateObject().Select(p => p.Name).ToList();
    }

    [Fact]
    public void GetState_reports_present()
    {
        Assert.Equal(SettingState.Present, RequiredSettings.GetState(Config(("EIMDB", "Server=.;Database=x")), "EIMDB"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetState_reports_missing_for_absent_or_blank(string? value)
    {
        var config = value is null ? Config() : Config(("EIMDB", value));

        Assert.Equal(SettingState.Missing, RequiredSettings.GetState(config, "EIMDB"));
    }

    [Fact]
    public void GetState_reports_unresolved_key_vault_reference()
    {
        var config = Config(("ServiceBus", "@Microsoft.KeyVault(SecretUri=https://vault/secrets/ServiceBus)"));

        Assert.Equal(SettingState.UnresolvedKeyVaultReference, RequiredSettings.GetState(config, "ServiceBus"));
    }

    [Fact]
    public void EnsurePresent_passes_when_every_automation_setting_is_present()
    {
        RequiredSettings.EnsurePresent(AllPresent(), AutomationGroups);
    }

    [Fact]
    public void EnsurePresent_MissingServiceBus_NamesSetting()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            RequiredSettings.EnsurePresent(AllPresent(except: "ServiceBus"), AutomationGroups));

        Assert.Contains("ServiceBus", ex.Message);
    }

    [Fact]
    public void EnsurePresent_names_every_missing_key_and_never_a_value()
    {
        var config = AllPresent(except: ["EIMDB", "D365ToCaptureQueue", "RetrySweepSchedule"]);

        var ex = Assert.Throws<InvalidOperationException>(() => RequiredSettings.EnsurePresent(config, AutomationGroups));

        Assert.Contains("EIMDB", ex.Message);
        Assert.Contains("D365ToCaptureQueue", ex.Message);
        Assert.Contains("RetrySweepSchedule", ex.Message);
        Assert.DoesNotContain("ServiceBus", ex.Message);
        Assert.DoesNotContain("value-of-", ex.Message);
    }

    [Fact]
    public void EnsurePresent_reports_an_unresolved_key_vault_reference_without_its_text()
    {
        var config = Config(("EIMDB", "@Microsoft.KeyVault(SecretUri=https://vault/secrets/EimDbConnection)"));

        var ex = Assert.Throws<InvalidOperationException>(() => RequiredSettings.EnsurePresent(config, RequiredSettings.Database));

        Assert.Contains("EIMDB (unresolved Key Vault reference)", ex.Message);
        Assert.DoesNotContain("SecretUri", ex.Message);
        Assert.DoesNotContain("EimDbConnection", ex.Message);
    }

    [Fact]
    public void Sample_settings_contain_every_required_automation_key()
    {
        var sample = SampleKeys();

        var absent = AutomationGroups.SelectMany(g => g).Where(k => !sample.Contains(k)).ToList();

        Assert.True(absent.Count == 0,
            "Wilson.Integration.Automation/local.settings.sample.json is missing: " + string.Join(", ", absent));
    }

    [Fact]
    public void Sample_settings_contain_no_polling_keys()
    {
        var polling = SampleKeys()
            .Where(k => k.Equals("PollSchedule", StringComparison.OrdinalIgnoreCase)
                || k.StartsWith("PollSource", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(polling);
    }
}
