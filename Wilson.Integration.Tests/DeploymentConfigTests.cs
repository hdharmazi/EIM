using System.Text.Json;
using System.Text.Json.Nodes;
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

    // Development runs locally: there is no dev deployment configuration.
    private static readonly string[] EnvironmentConfigurations = ["bat", "test"];

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
        foreach (var retired in Strings(root, "legacy", "retiredAutomationApps"))
            yield return retired;
        if (legacy.TryGetProperty("resources", out var resources))
            foreach (var resource in resources.EnumerateArray())
                yield return resource.GetProperty("name").GetString()!;
    }

    private static IEnumerable<(string Name, string Type)> LegacyResources(JsonElement root) =>
        root.TryGetProperty("legacy", out var legacy) && legacy.TryGetProperty("resources", out var resources)
            ? resources.EnumerateArray().Select(r => (r.GetProperty("name").GetString()!, r.GetProperty("type").GetString()!)).ToList()
            : [];

    // The design D7 tables: every derived name, plus the pattern defaults the kept names override. Production names
    // are those of the production-stamp spec.
    private static IEnumerable<string> DerivedNames()
    {
        static string[] Shared(string suffix) =>
        [
            $"rg-eim-wtc-{suffix}", $"sbns-eim-wtc-{suffix}", $"kv-eim-wtc-{suffix}", $"sql-eim-wtc-{suffix}", $"sqldb-eim-wtc-{suffix}",
            $"asp-eim-wtc-{suffix}", $"log-eim-wtc-{suffix}",
            $"azf-eim-gateway-wtc-{suffix}", $"stwtceimgw{suffix}", $"appins-eim-gateway-wtc-{suffix}",
            $"app-eim-dashboard-api-wtc-{suffix}", $"swa-eim-dashboard-wtc-{suffix}",
        ];
        static string[] Automation(string environment) =>
            [$"azf-eim-automation-wtc-{environment}", $"stwtceimauto{environment}", $"appins-eim-automation-wtc-{environment}"];

        return Shared("nonprod")
            .Concat(EnvironmentConfigurations.SelectMany(Automation))
            .Concat(Shared("prd"))
            .Append(ProductionResourceGroup)
            .Concat(ProductionEnvironmentConfigurations.SelectMany(Automation));
    }

    // ---- Merged configurations, mirroring Deploy-Wilson.ps1 (Merge-ConfigObject, Get-ResourceNames, Get-NamesInUse) ----

    private const string ProductionResourceGroup = "d365fsc-integration-prod";

    private static readonly string[] ProductionConfigurations = ["prd", "prdshared"];

    private static readonly string[] ProductionEnvironmentConfigurations = ["prd"];

    private static IReadOnlyList<string> ConfigurationNames() =>
        ConfigFileNames().Select(ConfigName).Where(n => n != "base").ToList();

    private static JsonObject LoadObject(string fileName) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(ConfigDirectory, fileName)))!.AsObject();

    // Objects merge key by key; anything else (values, lists) replaces the earlier value.
    private static JsonObject MergeObject(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch)
        {
            if (target[key] is JsonObject current && value is JsonObject child)
            {
                MergeObject(current, child);
                continue;
            }
            target[key] = value?.DeepClone();
        }
        return target;
    }

    private static JsonObject Merged(string name) => MergeObject(LoadObject("deploy.base.json"), LoadObject($"deploy.{name}.json"));

    private static JsonNode? NodeAt(JsonNode? root, string path)
    {
        var current = root;
        foreach (var key in path.Split('.'))
            current = current is JsonObject o && o.TryGetPropertyValue(key, out var next) ? next : null;
        return current;
    }

    private static bool HasPath(JsonObject root, string path)
    {
        JsonNode? current = root;
        foreach (var key in path.Split('.'))
        {
            if (current is not JsonObject o || !o.TryGetPropertyValue(key, out current))
                return false;
        }
        return true;
    }

    private static string? AsText(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string? Text(JsonNode? root, string path) => AsText(NodeAt(root, path));

    private static bool IsProduction(JsonObject config) =>
        NodeAt(config, "production") is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static bool IsPlaceholder(string? value) => value is not null && Regex.IsMatch(value, "^<.*>$");

    private static Dictionary<string, string> ResourceNames(JsonObject config, bool withOverrides = true)
    {
        var c = Text(config, "client");
        var w = Text(config, "workload");
        var e = Text(config, "environment");
        var s = Text(config, "sharedSuffix");
        string Pick(string key, string pattern)
        {
            var value = withOverrides ? Text(config, $"names.{key}") : null;
            return string.IsNullOrWhiteSpace(value) ? pattern : value;
        }

        return new()
        {
            ["resourceGroup"] = Pick("resourceGroup", $"rg-{w}-{c}-{s}"),
            ["serviceBusNamespace"] = Pick("serviceBusNamespace", $"sbns-{w}-{c}-{s}"),
            ["keyVault"] = Pick("keyVault", $"kv-{w}-{c}-{s}"),
            ["sqlServer"] = Pick("sqlServer", $"sql-{w}-{c}-{s}"),
            ["sqlDatabase"] = Pick("sqlDatabase", $"sqldb-{w}-{c}-{s}"),
            ["appServicePlan"] = Pick("appServicePlan", $"asp-{w}-{c}-{s}"),
            ["logAnalytics"] = Pick("logAnalytics", $"log-{w}-{c}-{s}"),
            ["gatewayApp"] = Pick("gatewayApp", $"azf-{w}-gateway-{c}-{s}"),
            ["gatewayStorage"] = Pick("gatewayStorage", $"st{c}{w}gw{s}"),
            ["gatewayInsights"] = Pick("gatewayInsights", $"appins-{w}-gateway-{c}-{s}"),
            ["dashboardApi"] = Pick("dashboardApi", $"app-{w}-dashboard-api-{c}-{s}"),
            ["staticWebApp"] = Pick("staticWebApp", $"swa-{w}-dashboard-{c}-{s}"),
            ["automationApp"] = Pick("automationApp", $"azf-{w}-automation-{c}-{e}"),
            ["automationStorage"] = Pick("automationStorage", $"st{c}{w}auto{e}"),
            ["automationInsights"] = Pick("automationInsights", $"appins-{w}-automation-{c}-{e}"),
            ["queuePrefix"] = Pick("queuePrefix", e!),
        };
    }

    // Every resource name a configuration uses, derived and overridden; the queue prefix is not a resource.
    private static HashSet<string> NamesInUse(JsonObject config) =>
        ResourceNames(config).Concat(ResourceNames(config, withOverrides: false))
            .Where(kv => kv.Key != "queuePrefix")
            .Select(kv => kv.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // The deploy.base.json values that identify non-production (production-stamp spec). Each production file must
    // set them itself, and the merged value must differ from the base. entra.tenantId may equal the base (same
    // tenant); a value that is a placeholder in both is not yet known on either side and is not a leak.
    private static readonly string[] NonProductionIdentifyingPaths =
    [
        "sharedSuffix",
        "names.resourceGroup", "names.serviceBusNamespace", "names.keyVault", "names.sqlServer", "names.sqlDatabase",
        "entra.tenantId", "entra.clientId", "entra.audience",
        "gateway.legacyVerificationTargets",
        "automation.excludedUsers",
        "dashboard.simulationEnabled", "dashboard.simulationQueuePrefix",
    ];

    private static IEnumerable<string> InheritedNonProductionValues(string name, JsonObject baseConfig, JsonObject own)
    {
        var merged = MergeObject(baseConfig.DeepClone().AsObject(), own);
        foreach (var path in NonProductionIdentifyingPaths)
        {
            var baseValue = NodeAt(baseConfig, path);
            var mergedValue = NodeAt(merged, path);
            if (!HasPath(own, path))
            {
                yield return $"{name}: {path} is not set, so it inherits the non-production value from deploy.base.json";
                continue;
            }
            if (path == "entra.tenantId")
                continue;
            if (IsPlaceholder(Text(baseConfig, path)) && IsPlaceholder(Text(merged, path)))
                continue;
            if (JsonNode.DeepEquals(baseValue, mergedValue))
                yield return $"{name}: {path} equals the non-production value in deploy.base.json";
        }
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
        Assert.Equal(["base", "bat", "nonprod", "prd", "prdshared", "test"], names);
    }

    [Fact]
    public void No_configuration_deploys_a_dev_environment()
    {
        Assert.False(File.Exists(Path.Combine(ConfigDirectory, "deploy.dev.json")), "deploy.dev.json must not exist; development runs locally.");
        foreach (var fileName in ConfigFileNames())
            Assert.NotEqual("dev", StringAt(Load(fileName), "environment"));
    }

    [Fact]
    public void Nonprod_retires_the_dev_automation_app()
    {
        Assert.Equal(["azf-eim-customer-wtc-dev"], Strings(Load("deploy.nonprod.json"), "legacy", "retiredAutomationApps"));
    }

    [Theory]
    [MemberData(nameof(ConfigFiles))]
    public void Each_retired_automation_app_is_a_legacy_site(string fileName)
    {
        var config = Load(fileName);
        var resources = LegacyResources(config).ToList();
        foreach (var retired in Strings(config, "legacy", "retiredAutomationApps"))
            Assert.True(resources.Contains((retired, "Microsoft.Web/sites")),
                $"{fileName}: retired Automation app '{retired}' must also be a Microsoft.Web/sites entry in legacy.resources so remove deletes it.");
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
        // One shared configuration per stamp: nonprod for non-production, prdshared for production.
        Assert.Equal(name is "nonprod" or "prdshared" ? "shared" : "environment", StringAt(config, "scope"));
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

    [Fact]
    public void No_secrets_in_configuration_covers_the_production_files()
    {
        var covered = ConfigFileNames();

        Assert.Contains("deploy.prdshared.json", covered);
        Assert.Contains("deploy.prd.json", covered);
    }

    // ---- Spec production-stamp: production is a separate stamp that shares nothing with non-production ----

    [Fact]
    public void Production_configurations_are_exactly_prdshared_and_prd()
    {
        var production = ConfigurationNames().Where(n => IsProduction(Merged(n))).Order(StringComparer.Ordinal);

        Assert.Equal(ProductionConfigurations, production);
        Assert.True(NodeAt(LoadObject("deploy.base.json"), "production")?.GetValue<bool>() == false, "deploy.base.json must set production to false.");
        foreach (var name in ProductionConfigurations)
            Assert.Equal("prd", Text(Merged(name), "sharedSuffix"));
    }

    [Fact]
    public void Production_names_resolve_to_the_pattern()
    {
        string[] blank = ["serviceBusNamespace", "keyVault", "sqlServer", "sqlDatabase"];
        foreach (var name in ProductionConfigurations)
        {
            var own = LoadObject($"deploy.{name}.json")["names"]!.AsObject();
            var overridden = own.Where(p => !string.IsNullOrWhiteSpace(AsText(p.Value))).Select(p => p.Key).ToList();
            Assert.True(overridden.SequenceEqual(["resourceGroup"]),
                $"deploy.{name}.json may override only names.resourceGroup; it overrides [{string.Join(", ", overridden)}].");
            foreach (var key in blank)
                Assert.True(own.ContainsKey(key) && Text(own, key) == "", $"deploy.{name}.json must set names.{key} to \"\" so the pattern name is derived.");
        }

        var shared = ResourceNames(Merged("prdshared"));
        Assert.Equal(ProductionResourceGroup, shared["resourceGroup"]);
        Assert.Equal("sbns-eim-wtc-prd", shared["serviceBusNamespace"]);
        Assert.Equal("sql-eim-wtc-prd", shared["sqlServer"]);
        Assert.Equal("sqldb-eim-wtc-prd", shared["sqlDatabase"]);
        Assert.Equal("asp-eim-wtc-prd", shared["appServicePlan"]);
        Assert.Equal("log-eim-wtc-prd", shared["logAnalytics"]);
        Assert.Equal("azf-eim-gateway-wtc-prd", shared["gatewayApp"]);
        Assert.Equal("stwtceimgwprd", shared["gatewayStorage"]);
        Assert.Equal("appins-eim-gateway-wtc-prd", shared["gatewayInsights"]);
        Assert.Equal("app-eim-dashboard-api-wtc-prd", shared["dashboardApi"]);
        Assert.Equal("swa-eim-dashboard-wtc-prd", shared["staticWebApp"]);

        var environment = ResourceNames(Merged("prd"));
        Assert.Equal("azf-eim-automation-wtc-prd", environment["automationApp"]);
        Assert.Equal("stwtceimautoprd", environment["automationStorage"]);
        Assert.Equal("appins-eim-automation-wtc-prd", environment["automationInsights"]);
        Assert.Equal("prd", environment["queuePrefix"]);

        // The shared names of prd equal those of prdshared.
        foreach (var key in shared.Keys.Where(k => !k.StartsWith("automation", StringComparison.Ordinal) && k != "queuePrefix"))
            Assert.Equal(shared[key], environment[key]);
    }

    [Fact]
    public void Production_and_nonproduction_names_are_disjoint()
    {
        var configs = ConfigurationNames().Select(n => (Name: n, Config: Merged(n))).ToList();
        var problems = (
            from production in configs.Where(c => IsProduction(c.Config))
            from other in configs.Where(c => !IsProduction(c.Config))
            from name in NamesInUse(production.Config).Intersect(NamesInUse(other.Config), StringComparer.OrdinalIgnoreCase)
            select $"{production.Name} and {other.Name} both use '{name}'").ToList();

        Assert.True(problems.Count == 0, "Production shares names with non-production:\n" + string.Join("\n", problems));
    }

    [Fact]
    public void Production_does_not_inherit_nonproduction_values()
    {
        var baseConfig = LoadObject("deploy.base.json");
        var problems = ProductionConfigurations
            .SelectMany(name => InheritedNonProductionValues(name, baseConfig, LoadObject($"deploy.{name}.json")))
            .ToList();
        Assert.True(problems.Count == 0, "Production inherits non-production values:\n" + string.Join("\n", problems));

        foreach (var name in ProductionConfigurations)
        {
            var merged = Merged(name);
            Assert.NotEqual(Text(baseConfig, "names.resourceGroup"), Text(merged, "names.resourceGroup"));
            Assert.Empty(NodeAt(merged, "gateway.legacyVerificationTargets")!.AsArray());
        }
    }

    [Fact]
    public void Production_inheritance_check_names_a_leaked_client_id()
    {
        // Scenario "Non-production client id leaks": prdshared omits entra.clientId and the base holds a real one.
        var baseConfig = LoadObject("deploy.base.json");
        baseConfig["entra"]!["clientId"] = "00000000-0000-0000-0000-000000000001";
        var own = LoadObject("deploy.prdshared.json");
        own["entra"]!.AsObject().Remove("clientId");

        var problems = InheritedNonProductionValues("prdshared", baseConfig, own).ToList();

        Assert.Contains(problems, p => p.Contains("prdshared") && p.Contains("entra.clientId"));
    }

    [Fact]
    public void Production_has_no_legacy_section()
    {
        Assert.False(LoadObject("deploy.base.json").ContainsKey("legacy"), "deploy.base.json must not have a legacy section; production would inherit it.");
        foreach (var name in ProductionConfigurations)
        {
            Assert.False(Merged(name).ContainsKey("legacy"), $"deploy.{name}.json must not have a legacy section.");
            Assert.Empty(LegacyNames(Load($"deploy.{name}.json")));
        }
    }

    [Fact]
    public void Production_simulation_is_off_and_nonproduction_on()
    {
        foreach (var name in ConfigurationNames())
        {
            var merged = Merged(name);
            var enabled = NodeAt(merged, "dashboard.simulationEnabled")?.GetValue<bool>();
            var prefix = Text(merged, "dashboard.simulationQueuePrefix");

            if (IsProduction(merged))
            {
                Assert.True(enabled == false, $"{name}: dashboard.simulationEnabled must be false on production.");
                Assert.True(prefix == "", $"{name}: dashboard.simulationQueuePrefix must be \"\" on production.");
            }
            else
            {
                Assert.True(enabled == true, $"{name}: dashboard.simulationEnabled must be true on non-production.");
                Assert.False(string.IsNullOrWhiteSpace(prefix) || IsPlaceholder(prefix), $"{name}: dashboard.simulationQueuePrefix must be set.");
            }
        }
    }

    [Fact]
    public void Production_plan_is_P0v3()
    {
        foreach (var name in ProductionConfigurations)
            Assert.Equal("P0v3", Text(Merged(name), "appServicePlan.sku"));
    }

    [Fact]
    public void Each_stamp_has_one_shared_configuration()
    {
        var stamps = ConfigurationNames()
            .Select(n => (Name: n, Config: Merged(n)))
            .GroupBy(c => Text(c.Config, "sharedSuffix"))
            .ToDictionary(g => g.Key!, g => g.ToList());

        Assert.Equal(["nonprod", "prd"], stamps.Keys.Order(StringComparer.Ordinal));
        foreach (var (suffix, members) in stamps)
        {
            var shared = members.Where(m => Text(m.Config, "scope") == "shared").Select(m => m.Name).ToList();
            Assert.True(shared.Count == 1, $"The stamp '{suffix}' must have exactly one shared configuration; found [{string.Join(", ", shared)}].");
            Assert.True(members.All(m => IsProduction(m.Config) == IsProduction(members[0].Config)),
                $"The stamp '{suffix}' mixes production and non-production configurations.");
        }
        Assert.Equal("nonprod", stamps["nonprod"].Single(m => Text(m.Config, "scope") == "shared").Name);
        Assert.Equal("prdshared", stamps["prd"].Single(m => Text(m.Config, "scope") == "shared").Name);
    }
}
