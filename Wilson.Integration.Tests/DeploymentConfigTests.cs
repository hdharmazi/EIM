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

    // Development runs locally: there is no dev deployment configuration. bat owns the non-production resources;
    // test uses them.
    private static readonly string[] EnvironmentConfigurations = ["bat", "test"];

    private static readonly string[] ProductionConfigurations = ["prd"];

    private const string ProductionResourceGroup = "d365fsc-integration-prod";

    // Split so no file in the repository names the removed keys.
    private static readonly string RemovedSuffixKey = "shared" + "Suffix";
    private static readonly string RemovedSection = "leg" + "acy";

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

    // ---- Merged configurations, mirroring Deploy-Wilson.ps1 (Merge-ConfigObject, Get-ResourceNames) ----

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

    private static bool? Flag(JsonNode? root, string path) => NodeAt(root, path) is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static bool IsProduction(JsonObject config) => Flag(config, "production") == true;

    private static bool IsPlaceholder(string? value) => value is not null && Regex.IsMatch(value, "^<.*>$");

    private static string? Uses(JsonObject config) => string.IsNullOrWhiteSpace(Text(config, "uses")) ? null : Text(config, "uses");

    // The names a configuration with "uses" always takes from its owner (Deploy-Wilson.ps1 $LockedNameKeys).
    private static readonly string[] LockedNameKeys =
    [
        "resourceGroup", "serviceBusNamespace", "sqlServer", "sqlDatabase", "appServicePlan",
        "gatewayApp", "gatewayStorage", "gatewayInsights", "dashboardApi", "staticWebApp",
    ];

    // The names a configuration with "uses" takes from its owner unless names.<key> sets its own
    // (Deploy-Wilson.ps1 $OverridableNameKeys).
    private static readonly string[] OverridableNameKeys = ["keyVault", "logAnalytics"];

    // Design D3: every name derives from the configuration's own environment; a non-blank names.<key> wins. A
    // configuration with "uses" takes the locked keys from its owner's resolved names, and an overridable key too
    // when names.<key> is blank or equals the owner's name (ignoring case).
    private static Dictionary<string, string> ResourceNames(JsonObject config)
    {
        var c = Text(config, "client");
        var w = Text(config, "workload");
        var e = Text(config, "environment");
        string Pick(string key, string pattern)
        {
            var value = Text(config, $"names.{key}");
            return string.IsNullOrWhiteSpace(value) ? pattern : value;
        }

        var names = new Dictionary<string, string>
        {
            ["resourceGroup"] = Pick("resourceGroup", $"rg-{w}-{c}-{e}"),
            ["serviceBusNamespace"] = Pick("serviceBusNamespace", $"sbns-{w}-{c}-{e}"),
            ["keyVault"] = Pick("keyVault", $"kv-{w}-{c}-{e}"),
            ["sqlServer"] = Pick("sqlServer", $"sql-{w}-{c}-{e}"),
            ["sqlDatabase"] = Pick("sqlDatabase", $"sqldb-{w}-{c}-{e}"),
            ["appServicePlan"] = Pick("appServicePlan", $"asp-{w}-{c}-{e}"),
            ["logAnalytics"] = Pick("logAnalytics", $"log-{w}-{c}-{e}"),
            ["gatewayApp"] = Pick("gatewayApp", $"azf-{w}-gateway-{c}-{e}"),
            ["gatewayStorage"] = Pick("gatewayStorage", $"st{c}{w}gw{e}"),
            ["gatewayInsights"] = Pick("gatewayInsights", $"appins-{w}-gateway-{c}-{e}"),
            ["dashboardApi"] = Pick("dashboardApi", $"app-{w}-dashboard-api-{c}-{e}"),
            ["staticWebApp"] = Pick("staticWebApp", $"swa-{w}-dashboard-{c}-{e}"),
            ["automationApp"] = Pick("automationApp", $"azf-{w}-automation-{c}-{e}"),
            ["automationStorage"] = Pick("automationStorage", $"st{c}{w}auto{e}"),
            ["automationInsights"] = Pick("automationInsights", $"appins-{w}-automation-{c}-{e}"),
            ["queuePrefix"] = Pick("queuePrefix", e!),
        };

        if (Uses(config) is { } owner)
        {
            var ownerNames = ResourceNames(Merged(owner));
            foreach (var key in LockedNameKeys)
                names[key] = ownerNames[key];
            foreach (var key in OverridableNameKeys)
            {
                var own = Text(config, $"names.{key}");
                if (string.IsNullOrWhiteSpace(own) || own.Equals(ownerNames[key], StringComparison.OrdinalIgnoreCase))
                    names[key] = ownerNames[key];
            }
        }
        return names;
    }

    // Every resource name a configuration resolves to; the queue prefix is not a resource.
    private static HashSet<string> NamesInUse(JsonObject config) =>
        ResourceNames(config)
            .Where(kv => kv.Key != "queuePrefix")
            .Select(kv => kv.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // The deploy.base.json values that identify non-production (production-stamp spec). Each production file must
    // set them itself, and the merged value must differ from the base. entra.tenantId may equal the base (same
    // tenant); a value that is a placeholder in both is not yet known on either side and is not a leak.
    private static readonly string[] NonProductionIdentifyingPaths =
    [
        "subscriptionId",
        "entra.tenantId", "entra.clientId", "entra.audience",
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
        Assert.Equal(["base", "bat", "prd", "test"], names);
        Assert.Equal(EnvironmentConfigurations.Concat(ProductionConfigurations).Order(StringComparer.Ordinal), ConfigurationNames());
    }

    [Fact]
    public void No_configuration_deploys_a_dev_environment()
    {
        Assert.False(File.Exists(Path.Combine(ConfigDirectory, "deploy.dev.json")), "deploy.dev.json must not exist; development runs locally.");
        foreach (var fileName in ConfigFileNames())
            Assert.NotEqual("dev", StringAt(Load(fileName), "environment"));
    }

    [Theory]
    [MemberData(nameof(ConfigFiles))]
    public void Environment_matches_the_file_name(string fileName)
    {
        var config = Load(fileName);
        var name = ConfigName(fileName);

        if (name == "base")
        {
            Assert.False(config.TryGetProperty("environment", out _), "deploy.base.json must not set 'environment'.");
            return;
        }

        Assert.Equal(name, StringAt(config, "environment"));
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
    public void Bat_keeps_the_existing_resource_names()
    {
        var own = LoadObject("deploy.bat.json")["names"]!.AsObject();
        var expected = new Dictionary<string, string>
        {
            ["resourceGroup"] = "d365fsc-integration-nonprod",
            ["serviceBusNamespace"] = "asb-eim-wtc-nonprod",
            ["keyVault"] = "kv-d365fsc-wtc-nonprod",
            ["sqlServer"] = "azql-wtc-sandbox",
            ["sqlDatabase"] = "eimdb",
            ["dashboardApi"] = "wtc-dashboard-api-dev",
            ["staticWebApp"] = "wtc-dashboard-frontend-dev",
        };

        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), own.Select(p => p.Key).Order(StringComparer.Ordinal));
        foreach (var (key, value) in expected)
            Assert.Equal(value, Text(own, key));

        // Everything else derives from the environment, bat.
        var names = ResourceNames(Merged("bat"));
        Assert.Equal("asp-eim-wtc-bat", names["appServicePlan"]);
        Assert.Equal("log-eim-wtc-bat", names["logAnalytics"]);
        Assert.Equal("azf-eim-gateway-wtc-bat", names["gatewayApp"]);
        Assert.Equal("stwtceimgwbat", names["gatewayStorage"]);
        Assert.Equal("appins-eim-gateway-wtc-bat", names["gatewayInsights"]);
        Assert.Equal("azf-eim-automation-wtc-bat", names["automationApp"]);
        Assert.Equal("stwtceimautobat", names["automationStorage"]);
        Assert.Equal("appins-eim-automation-wtc-bat", names["automationInsights"]);
        Assert.Equal("bat", names["queuePrefix"]);
    }

    [Fact]
    public void Test_resolves_to_bat_resources_and_its_own_automation()
    {
        var bat = ResourceNames(Merged("bat"));
        var test = ResourceNames(Merged("test"));

        foreach (var key in LockedNameKeys.Concat(OverridableNameKeys))
            Assert.Equal(bat[key], test[key]);
        Assert.Equal("azf-eim-automation-wtc-test", test["automationApp"]);
        Assert.Equal("stwtceimautotest", test["automationStorage"]);
        Assert.Equal("appins-eim-automation-wtc-test", test["automationInsights"]);
        Assert.Equal("test", test["queuePrefix"]);
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
    public void No_secrets_in_configuration_covers_every_file()
    {
        Assert.Equal(["deploy.base.json", "deploy.bat.json", "deploy.prd.json", "deploy.test.json"], ConfigFileNames());
    }

    // ---- Spec deployment-automation: owners, uses, and the configuration keys of design D1 ----

    [Fact]
    public void Base_has_no_names_and_no_shared_suffix()
    {
        var baseConfig = LoadObject("deploy.base.json");

        Assert.False(baseConfig.ContainsKey("names"), "deploy.base.json must not set names; every name derives from the environment.");
        Assert.False(baseConfig.ContainsKey(RemovedSuffixKey), $"deploy.base.json must not set {RemovedSuffixKey}.");
        Assert.False(baseConfig.ContainsKey("uses"), "deploy.base.json must not set 'uses'.");
    }

    [Theory]
    [MemberData(nameof(ConfigFiles))]
    public void No_configuration_has_scope_shared_suffix_or_retired_sections(string fileName)
    {
        var config = LoadObject(fileName);

        // entra.scope is the dashboard's OAuth scope, not the removed top-level "scope".
        Assert.False(config.ContainsKey("scope"), $"{fileName} sets the removed top-level 'scope'.");
        Assert.False(config.ContainsKey(RemovedSuffixKey), $"{fileName} sets the removed '{RemovedSuffixKey}'.");
        Assert.False(config.ContainsKey(RemovedSection), $"{fileName} has the removed '{RemovedSection}' section.");
        Assert.False(HasPath(config, $"gateway.{RemovedSection}VerificationTargets"), $"{fileName} sets the removed gateway verification targets.");

        var retiredApps = "retired" + "AutomationApps";
        var keys = Walk(Load(fileName)).Select(node => node.Path.Split('.', '[')[^1]).ToList();
        Assert.DoesNotContain(retiredApps, keys);
        Assert.DoesNotContain(keys, key => key.Contains(RemovedSection, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Test_uses_bat_and_overrides_no_locked_name()
    {
        var test = LoadObject("deploy.test.json");

        Assert.Equal("bat", Text(test, "uses"));
        var overridden = test["names"] is JsonObject names
            ? names.Where(p => LockedNameKeys.Contains(p.Key) && !string.IsNullOrWhiteSpace(AsText(p.Value))).Select(p => p.Key).ToList()
            : [];
        Assert.True(overridden.Count == 0, $"deploy.test.json overrides names it takes from bat: {string.Join(", ", overridden)}.");
    }

    [Fact]
    public void Locked_and_overridable_name_keys_split_the_inherited_names()
    {
        Assert.Equal(10, LockedNameKeys.Length);
        Assert.Contains("appServicePlan", LockedNameKeys);
        Assert.Equal(["keyVault", "logAnalytics"], OverridableNameKeys);
        Assert.Empty(LockedNameKeys.Intersect(OverridableNameKeys));
    }

    // A merged test configuration with names.<key> set, as a configuration that owns that resource.
    private static JsonObject TestWithName(string key, string value)
    {
        var test = Merged("test");
        if (test["names"] is not JsonObject names)
            test["names"] = names = new JsonObject();
        names[key] = value;
        return test;
    }

    [Fact]
    public void Overridable_key_vault_resolves_to_its_own_name()
    {
        var bat = ResourceNames(Merged("bat"));
        var test = ResourceNames(TestWithName("keyVault", "kv-eim-wtc-test"));

        Assert.Equal("kv-eim-wtc-test", test["keyVault"]);
        foreach (var key in LockedNameKeys)
            Assert.Equal(bat[key], test[key]);
        Assert.Equal(bat["logAnalytics"], test["logAnalytics"]);
    }

    [Fact]
    public void Overridable_name_that_is_blank_or_equal_inherits_the_owner_name()
    {
        var bat = ResourceNames(Merged("bat"));

        Assert.Equal(bat["logAnalytics"], ResourceNames(TestWithName("logAnalytics", ""))["logAnalytics"]);
        Assert.Equal(bat["keyVault"], ResourceNames(TestWithName("keyVault", bat["keyVault"].ToUpperInvariant()))["keyVault"]);
    }

    [Fact]
    public void Bat_and_prd_have_no_uses()
    {
        foreach (var name in new[] { "bat", "prd" })
            Assert.Null(Uses(Merged(name)));

        // An owner is never itself a user of another configuration, and production never uses one.
        foreach (var name in ConfigurationNames())
        {
            var merged = Merged(name);
            if (Uses(merged) is not { } owner)
                continue;
            Assert.False(IsProduction(merged), $"{name}: a production configuration must own its resources.");
            Assert.Null(Uses(Merged(owner)));
            Assert.False(IsProduction(Merged(owner)), $"{name} uses the production configuration {owner}.");
        }
    }

    [Fact]
    public void Trigger_switch_is_off_in_base_and_explicit_per_environment()
    {
        Assert.True(Flag(LoadObject("deploy.base.json"), "automation.triggersEnabled") == false,
            "deploy.base.json must set automation.triggersEnabled to false.");

        var expected = new Dictionary<string, bool> { ["bat"] = true, ["test"] = true, ["prd"] = false };
        foreach (var (name, value) in expected)
        {
            var own = LoadObject($"deploy.{name}.json");
            Assert.True(HasPath(own, "automation.triggersEnabled"), $"deploy.{name}.json must set automation.triggersEnabled itself.");
            Assert.Equal(value, Flag(own, "automation.triggersEnabled"));
        }
    }

    [Fact]
    public void KeepDisabled_names_only_gated_functions()
    {
        foreach (var name in ConfigurationNames())
        {
            var merged = Merged(name);
            var gated = Strings(JsonDocument.Parse(merged.ToJsonString()).RootElement, "automation", "gatedFunctions");
            var keepDisabled = Strings(JsonDocument.Parse(merged.ToJsonString()).RootElement, "automation", "keepDisabled");
            var unknown = keepDisabled.Except(gated, StringComparer.Ordinal).ToList();
            Assert.True(unknown.Count == 0, $"{name}: automation.keepDisabled names functions that are not gated: {string.Join(", ", unknown)}.");
        }
    }

    [Fact]
    public void Sql_defaults_mirror_wastequip()
    {
        var baseConfig = LoadObject("deploy.base.json");

        Assert.Equal("S0", Text(baseConfig, "sql.sku"));
        Assert.Equal("Local", Text(baseConfig, "sql.backupRedundancy"));
        Assert.Equal("appOutboundIps", Text(baseConfig, "sql.firewall"));
        Assert.Equal("Group", Text(baseConfig, "sql.entraAdminType"));
    }

    [Fact]
    public void Prd_sql_admin_is_a_placeholder_or_filled()
    {
        // Deploy-Wilson.ps1 refuses to deploy sql while either value is a placeholder; neither may be blank.
        var own = LoadObject("deploy.prd.json");
        foreach (var path in new[] { "sql.entraAdmin", "sql.entraAdminObjectId" })
        {
            Assert.True(HasPath(own, path), $"deploy.prd.json must set {path} itself.");
            Assert.False(string.IsNullOrWhiteSpace(Text(own, path)), $"deploy.prd.json {path} must be a placeholder or the production group.");
        }

        var objectId = Text(own, "sql.entraAdminObjectId")!;
        Assert.True(IsPlaceholder(objectId) || Guid.TryParse(objectId, out _),
            $"deploy.prd.json sql.entraAdminObjectId must be a placeholder or a GUID, not '{objectId}'.");
    }

    // ---- Spec production-stamp: production is an isolated configuration that shares nothing with non-production ----

    [Fact]
    public void Production_configuration_is_exactly_prd()
    {
        var production = ConfigurationNames().Where(n => IsProduction(Merged(n))).Order(StringComparer.Ordinal);

        Assert.Equal(ProductionConfigurations, production);
        Assert.True(Flag(LoadObject("deploy.base.json"), "production") == false, "deploy.base.json must set production to false.");
    }

    [Fact]
    public void Production_names_resolve_to_the_pattern()
    {
        var own = LoadObject("deploy.prd.json")["names"]!.AsObject();
        var overridden = own.Where(p => !string.IsNullOrWhiteSpace(AsText(p.Value))).Select(p => p.Key).ToList();
        Assert.True(overridden.SequenceEqual(["resourceGroup"]),
            $"deploy.prd.json may override only names.resourceGroup; it overrides [{string.Join(", ", overridden)}].");

        var names = ResourceNames(Merged("prd"));
        var expected = new Dictionary<string, string>
        {
            ["resourceGroup"] = ProductionResourceGroup,
            ["serviceBusNamespace"] = "sbns-eim-wtc-prd",
            ["keyVault"] = "kv-eim-wtc-prd",
            ["sqlServer"] = "sql-eim-wtc-prd",
            ["sqlDatabase"] = "sqldb-eim-wtc-prd",
            ["appServicePlan"] = "asp-eim-wtc-prd",
            ["logAnalytics"] = "log-eim-wtc-prd",
            ["gatewayApp"] = "azf-eim-gateway-wtc-prd",
            ["gatewayStorage"] = "stwtceimgwprd",
            ["gatewayInsights"] = "appins-eim-gateway-wtc-prd",
            ["dashboardApi"] = "app-eim-dashboard-api-wtc-prd",
            ["staticWebApp"] = "swa-eim-dashboard-wtc-prd",
            ["automationApp"] = "azf-eim-automation-wtc-prd",
            ["automationStorage"] = "stwtceimautoprd",
            ["automationInsights"] = "appins-eim-automation-wtc-prd",
            ["queuePrefix"] = "prd",
        };
        foreach (var (key, value) in expected)
            Assert.Equal(value, names[key]);
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
    public void Disjoint_names_check_catches_a_shared_sql_server()
    {
        // prd pointed at the non-production SQL server shares a resolved name with bat and with test, which uses bat.
        var prd = Merged("prd");
        prd["names"]!["sqlServer"] = "azql-wtc-sandbox";

        Assert.Contains("azql-wtc-sandbox", NamesInUse(prd).Intersect(NamesInUse(Merged("bat"))));
        Assert.Contains("azql-wtc-sandbox", NamesInUse(prd).Intersect(NamesInUse(Merged("test"))));
    }

    [Fact]
    public void Disjoint_names_check_catches_an_owned_vault_shared_with_production()
    {
        var prdVault = ResourceNames(Merged("prd"))["keyVault"];
        var test = TestWithName("keyVault", prdVault);

        Assert.Contains(prdVault, NamesInUse(Merged("prd")).Intersect(NamesInUse(test), StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Production_does_not_inherit_nonproduction_values()
    {
        var baseConfig = LoadObject("deploy.base.json");
        var problems = ProductionConfigurations
            .SelectMany(name => InheritedNonProductionValues(name, baseConfig, LoadObject($"deploy.{name}.json")))
            .ToList();
        Assert.True(problems.Count == 0, "Production inherits non-production values:\n" + string.Join("\n", problems));
    }

    [Fact]
    public void Production_inheritance_check_names_a_leaked_client_id()
    {
        // Scenario "Non-production client id leaks": prd omits entra.clientId and the base holds a real one.
        var baseConfig = LoadObject("deploy.base.json");
        baseConfig["entra"]!["clientId"] = "00000000-0000-0000-0000-000000000001";
        var own = LoadObject("deploy.prd.json");
        own["entra"]!.AsObject().Remove("clientId");

        var problems = InheritedNonProductionValues("prd", baseConfig, own).ToList();

        Assert.Contains(problems, p => p.Contains("prd") && p.Contains("entra.clientId"));
    }

    [Fact]
    public void Production_simulation_is_off_and_nonproduction_on()
    {
        foreach (var name in ConfigurationNames())
        {
            var merged = Merged(name);
            var enabled = Flag(merged, "dashboard.simulationEnabled");
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
}
