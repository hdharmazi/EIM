using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Wilson.Integration.Tests;

/// <summary>
/// Spec deployment-automation: the deployment configuration files under script/deployment/config. Deploy-Wilson.ps1
/// merges deploy.base.json with deploy.&lt;name&gt;.json; these tests check the committed files themselves, so a
/// mistake fails the build rather than a deployment.
/// </summary>
public partial class DeploymentConfigTests
{
    private static readonly string ConfigDirectory = Path.Combine(RepositoryPaths.Root(), "script", "deployment", "config");

    private static readonly string[] EnvironmentConfigurations = ["dev", "bat", "test"];

    private static IReadOnlyList<string> ConfigFileNames() =>
        Directory.GetFiles(ConfigDirectory, "deploy.*.json").Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToList();

    public static TheoryData<string> ConfigFiles() => new(ConfigFileNames());

    private static JsonElement Load(string fileName)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(ConfigDirectory, fileName)));
        return document.RootElement.Clone();
    }

    private static string ConfigName(string fileName) => fileName["deploy.".Length..^".json".Length];

    private static IEnumerable<(string Path, JsonElement Value)> Walk(JsonElement element, string path = "")
    {
        yield return (path, element);
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                foreach (var child in Walk(property.Value, path.Length == 0 ? property.Name : $"{path}.{property.Name}"))
                    yield return child;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var item in element.EnumerateArray())
                foreach (var child in Walk(item, $"{path}[{i++}]"))
                    yield return child;
        }
    }

    private static string? StringAt(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var key in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out current))
                return null;
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static string[] Strings(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var key in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out current))
                return [];
        }
        return current.ValueKind == JsonValueKind.Array ? current.EnumerateArray().Select(e => e.GetString() ?? "").ToArray() : [];
    }

    private static IEnumerable<string> LegacyNames(JsonElement root)
    {
        if (!root.TryGetProperty("legacy", out var legacy))
            yield break;
        foreach (var key in new[] { "automationApp", "gatewayApp" })
            if (legacy.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                yield return value.GetString()!;
        if (legacy.TryGetProperty("resources", out var resources))
            foreach (var resource in resources.EnumerateArray())
                yield return resource.GetProperty("name").GetString()!;
    }

    // The design D7 tables: every derived name, plus the pattern defaults the kept names override.
    private static IEnumerable<string> DerivedNames()
    {
        string[] shared =
        [
            "rg-eim-wtc-nonprod", "sbns-eim-wtc-nonprod", "kv-eim-wtc-nonprod", "sql-eim-wtc-nonprod", "sqldb-eim-wtc-nonprod",
            "asp-eim-wtc-nonprod", "log-eim-wtc-nonprod",
            "azf-eim-gateway-wtc-nonprod", "stwtceimgwnonprod", "appins-eim-gateway-wtc-nonprod",
            "app-eim-dashboard-api-wtc-nonprod", "swa-eim-dashboard-wtc-nonprod",
        ];
        return shared.Concat(EnvironmentConfigurations.SelectMany(e => new[]
        {
            $"azf-eim-automation-wtc-{e}", $"stwtceimauto{e}", $"appins-eim-automation-wtc-{e}",
        }));
    }

    [Theory]
    [MemberData(nameof(ConfigFiles))]
    public void Each_configuration_parses(string fileName)
    {
        Assert.Equal(JsonValueKind.Object, Load(fileName).ValueKind);
    }

    [Fact]
    public void The_expected_configurations_exist()
    {
        var names = ConfigFileNames().Select(ConfigName).Order(StringComparer.Ordinal);
        Assert.Equal(["base", "bat", "dev", "nonprod", "test"], names);
    }

    [Theory]
    [MemberData(nameof(ConfigFiles))]
    public void Environment_matches_the_file_name_and_scope_is_set(string fileName)
    {
        var config = Load(fileName);
        var name = ConfigName(fileName);

        if (name == "base")
        {
            Assert.False(config.TryGetProperty("environment", out _), "deploy.base.json must not set 'environment'.");
            return;
        }

        Assert.Equal(name, StringAt(config, "environment"));
        Assert.Equal(name == "nonprod" ? "shared" : "environment", StringAt(config, "scope"));
    }

    [Fact]
    public void Gated_functions_equal_the_automation_function_names()
    {
        var reflected = AutomationFunctionNamesTests.Functions().Select(f => f.Name).Order(StringComparer.Ordinal).ToArray();
        var configured = Strings(Load("deploy.base.json"), "automation", "gatedFunctions").Order(StringComparer.Ordinal).ToArray();

        Assert.True(reflected.SequenceEqual(configured),
            $"automation.gatedFunctions in deploy.base.json is [{string.Join(", ", configured)}] but the Automation host has [{string.Join(", ", reflected)}].");

        foreach (var fileName in ConfigFileNames())
        {
            if (fileName == "deploy.base.json")
                continue;
            // An override replaces the whole list, so it must still name every function.
            var overridden = Strings(Load(fileName), "automation", "gatedFunctions");
            Assert.True(overridden.Length == 0 || overridden.Order(StringComparer.Ordinal).SequenceEqual(reflected),
                $"{fileName} overrides automation.gatedFunctions with a different set.");
        }
    }

    [Fact]
    public void Poll_schedule_is_retired_and_never_configured()
    {
        Assert.Contains("PollSchedule", Strings(Load("deploy.base.json"), "automation", "retiredSettings"));

        foreach (var fileName in ConfigFileNames())
        {
            var pollKeys = Walk(Load(fileName))
                .Select(node => node.Path.Split('.', '[')[^1])
                .Where(key => key.Equals("pollSchedule", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.True(pollKeys.Count == 0, $"{fileName} has a pollSchedule key; PollSchedule is retired.");
        }
    }

    [Fact]
    public void Nonprod_keeps_the_dashboard_names()
    {
        var nonprod = Load("deploy.nonprod.json");

        Assert.Equal("wtc-dashboard-api-dev", StringAt(nonprod, "names", "dashboardApi"));
        Assert.Equal("wtc-dashboard-frontend-dev", StringAt(nonprod, "names", "staticWebApp"));
    }

    [Fact]
    public void Legacy_names_are_never_names_in_use_and_never_the_dashboard()
    {
        var inUse = new HashSet<string>(DerivedNames(), StringComparer.OrdinalIgnoreCase);
        foreach (var fileName in ConfigFileNames())
        {
            var config = Load(fileName);
            if (config.TryGetProperty("names", out var names))
                foreach (var property in names.EnumerateObject())
                    if (property.Value.ValueKind == JsonValueKind.String)
                        inUse.Add(property.Value.GetString()!);
        }

        foreach (var fileName in ConfigFileNames())
        {
            foreach (var legacy in LegacyNames(Load(fileName)))
            {
                Assert.False(inUse.Contains(legacy), $"{fileName}: legacy name '{legacy}' is also a name in use; remove would delete it.");
                Assert.DoesNotContain("dashboard", legacy, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [GeneratedRegex(@"(?i)(Server|Data Source|Endpoint|AccountKey|SharedAccessKey|Password|Pwd|User ID)\s*=")]
    private static partial Regex ConnectionStringShape();

    [Theory]
    [MemberData(nameof(ConfigFiles))]
    public void No_secrets_in_configuration(string fileName)
    {
        var nodes = Walk(Load(fileName)).ToList();

        Assert.DoesNotContain(nodes, node => node.Path.Split('.', '[')[^1].Equals("secrets", StringComparison.OrdinalIgnoreCase));
        var shaped = nodes
            .Where(node => node.Value.ValueKind == JsonValueKind.String && ConnectionStringShape().IsMatch(node.Value.GetString()!))
            .Select(node => node.Path)
            .ToList();
        Assert.True(shaped.Count == 0, $"{fileName} has connection-string-shaped values at: {string.Join(", ", shaped)}");
    }
}
