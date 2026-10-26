using System.Text.RegularExpressions;
using Xunit;

namespace Wilson.Integration.Tests;

/// <summary>
/// Spec deployment-automation: the Azure DevOps pipeline in pipelines/. Text assertions over eim.yml and its
/// templates, so a reordered stage, a mapped secret or a migration in a stage that does not own its resources fails
/// the build rather than a deployment.
/// </summary>
public partial class PipelineDefinitionTests
{
    private static readonly string Root = RepositoryPaths.Root();

    // Development runs locally: there is no dev stage. bat owns the non-production resources; test uses them.
    private static readonly string[] StageOrder = ["Build", "Deploy_bat", "Deploy_test"];

    // Split so no file in the repository names the removed secret variables.
    internal static readonly string[] RemovedSecretVariables = ["EIM_DEPLOY_" + "EIMDB", "EIM_DEPLOY_" + "SERVICEBUS"];

    internal static string ReadFile(params string[] relative) => File.ReadAllText(Path.Combine([Root, .. relative]));

    private static string Pipeline => ReadFile("pipelines", "eim.yml");

    private static string BuildTemplate => ReadFile("pipelines", "templates", "build-package.yml");

    internal static string DeployTemplate => ReadFile("pipelines", "templates", "deploy-environment.yml");

    [GeneratedRegex(@"^  - stage: (?<name>\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex StageLine();

    /// <summary>Each stage's name and its block of text, up to the next stage.</summary>
    internal static List<(string Name, string Block)> StagesOf(string text)
    {
        var matches = StageLine().Matches(text);
        return matches
            .Select((m, i) => (m.Groups["name"].Value, text[m.Index..(i + 1 < matches.Count ? matches[i + 1].Index : text.Length)]))
            .ToList();
    }

    private static List<(string Name, string Block)> Stages() => StagesOf(Pipeline);

    private static string Stage(string name) =>
        Stages().SingleOrDefault(s => s.Name == name).Block ?? throw new Xunit.Sdk.XunitException($"Stage '{name}' not found in pipelines/eim.yml.");

    internal static string? DependsOn(string block) =>
        Regex.Match(block, @"^\s+dependsOn: (\S+)\s*$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>The default of a top-level template or pipeline parameter, or null when it has none.</summary>
    internal static string? ParameterDefault(string text, string name)
    {
        var parameter = Regex.Match(text, $@"^  - name: {name}\b.*$(?<body>(?:\r?\n {{4,}}.*)*)", RegexOptions.Multiline);
        Assert.True(parameter.Success, $"Parameter '{name}' not found.");
        return Regex.Match(parameter.Groups["body"].Value, @"^\s+default: (.*?)\s*$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : null;
    }

    /// <summary>Each AzureCLI@2 step of the deploy template: its display name and its text, up to the next step.</summary>
    internal static List<(string Name, string Block)> DeploySteps() =>
        DeployTemplate.Split("task: AzureCLI@2")[1..]
            .Select(step => (Regex.Match(step, @"displayName: (.+)").Groups[1].Value.Trim(), step))
            .ToList();

    /// <summary>The value a stage passes for a template parameter, or null when it passes none.</summary>
    internal static string? StageParameter(string block, string name) =>
        Regex.Match(block, $@"^\s+{name}: (.+?)\s*$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : null;

    private static string DeployStep(string name) =>
        DeploySteps().SingleOrDefault(s => s.Name == name).Block ?? throw new Xunit.Sdk.XunitException($"Step '{name}' not found in deploy-environment.yml.");

    [Fact]
    public void Stages_run_in_order()
    {
        Assert.Equal(StageOrder, Stages().Select(s => s.Name));

        Assert.Null(DependsOn(Stage("Build")));
        for (var i = 1; i < StageOrder.Length; i++)
            Assert.Equal(StageOrder[i - 1], DependsOn(Stage(StageOrder[i])));
    }

    [Fact]
    public void Deploy_stages_need_the_previous_stage_to_succeed_and_never_run_for_pull_requests()
    {
        for (var i = 1; i < StageOrder.Length; i++)
        {
            var block = Stage(StageOrder[i]);
            Assert.Contains($"eq(dependencies.{StageOrder[i - 1]}.result, 'Succeeded')", block);
            Assert.Contains("ne(variables['Build.Reason'], 'PullRequest')", block);
        }
    }

    [Fact]
    public void Bat_follows_the_build_directly()
    {
        var bat = Stage("Deploy_bat");

        Assert.Equal("Build", DependsOn(bat));
        Assert.Contains("eq(dependencies.Build.result, 'Succeeded')", bat);
    }

    [Fact]
    public void No_pipeline_file_names_a_dev_stage_or_devops_environment()
    {
        // Split so this file does not match its own search.
        string[] forbidden = ["Deploy_" + "dev", "eim-wtc-" + "dev"];

        foreach (var file in Directory.GetFiles(Path.Combine(Root, "pipelines"), "*", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (var word in forbidden)
                Assert.False(text.Contains(word, StringComparison.Ordinal), $"{Path.GetRelativePath(Root, file)} contains '{word}'; development runs locally.");
        }
    }

    [Fact]
    public void A_newer_run_replaces_an_older_waiting_run()
    {
        Assert.Matches(new Regex(@"^lockBehavior: runLatest\s*$", RegexOptions.Multiline), Pipeline);
    }

    [Theory]
    [InlineData("Deploy_bat", "bat", "eim-wtc-bat", "gateway, backend, frontend, automation")]
    [InlineData("Deploy_test", "test", "eim-wtc-test", "automation")]
    public void Each_deploy_stage_targets_its_configuration_and_devops_environment(string stage, string configuration, string devopsEnvironment, string components)
    {
        var block = Stage(stage);

        Assert.Contains("template: templates/deploy-environment.yml", block);
        Assert.Matches(new Regex($@"^\s+environment: {configuration}\s*$", RegexOptions.Multiline), block);
        Assert.Matches(new Regex($@"^\s+devopsEnvironment: {devopsEnvironment}\s*$", RegexOptions.Multiline), block);
        Assert.Matches(new Regex(@"^ {10}\$\{\{ if parameters\.deployCode \}\}:\s*\r?\n {12}components: " + Regex.Escape(components) + @"\s*$", RegexOptions.Multiline), block);
    }

    [Fact]
    public void Migrations_can_run_only_in_owner_stages()
    {
        Assert.Equal("${{ parameters.runMigrations }}", StageParameter(Stage("Deploy_bat"), "runMigrations"));

        var runMigrations = Regex.Matches(Stage("Deploy_test"), @"runMigrations: (.+)$", RegexOptions.Multiline).Select(m => m.Groups[1].Value.Trim()).ToList();
        Assert.True(runMigrations.SequenceEqual(["false"]), $"Deploy_test must pass runMigrations: false, found [{string.Join(", ", runMigrations)}].");

        // The template refuses runMigrations for a stage that does not own its resources, at template expansion.
        Assert.Matches(new Regex(
            @"^  - \$\{\{ if and\(parameters\.runMigrations, not\(parameters\.ownsResources\)\) \}\}:\s*\r?\n    - '[^']*ownsResources[^']*': error\s*$", RegexOptions.Multiline), DeployTemplate);
    }

    [Fact]
    public void Only_the_owner_stage_runs_the_sql_component()
    {
        Assert.Equal("true", StageParameter(Stage("Deploy_bat"), "ownsResources"));
        Assert.Equal("false", StageParameter(Stage("Deploy_test"), "ownsResources"));
        Assert.Equal("false", ParameterDefault(DeployTemplate, "ownsResources"));

        // 'sql' is added only for an owner, in the preview and in the infrastructure step.
        var infra = DeployStep("Infrastructure");
        Assert.Contains("$components = @('infra')", infra);
        Assert.Contains("if ('${{ parameters.ownsResources }}' -eq 'True') { $components += 'sql' }", infra);
        Assert.Contains("'${{ parameters.ownsResources }}' -eq 'True') { $components += 'sql' }", DeployStep("Preflight and preview (changes nothing)"));
        foreach (var (name, block) in DeploySteps())
        {
            var sqlAdds = Regex.Matches(block, @"'sql'").Count;
            var ownerChecks = Regex.Matches(block, @"parameters\.ownsResources \}\}' -eq 'True'\) \{ \$components \+= 'sql' \}").Count;
            Assert.True(sqlAdds == ownerChecks, $"Step '{name}' adds 'sql' outside the ownsResources check.");
        }
    }

    [Fact]
    public void Database_access_is_a_manual_parameter_defaulting_to_false()
    {
        Assert.Equal("false", ParameterDefault(Pipeline, "grantDatabaseAccess"));
        Assert.Equal("false", ParameterDefault(DeployTemplate, "grantDatabaseAccess"));
        foreach (var stage in StageOrder[1..])
            Assert.Equal("${{ parameters.grantDatabaseAccess }}", StageParameter(Stage(stage), "grantDatabaseAccess"));
        Assert.Contains("if ('${{ parameters.grantDatabaseAccess }}' -eq 'True') { $components += 'sqlusers' }", DeployStep("Database"));
    }

    [Fact]
    public void Build_checks_the_scripts_and_the_package_before_publishing_it()
    {
        var build = BuildTemplate;

        // Positions of the steps themselves, not of the header comment that names the scripts.
        static int StepAt(string text, string pattern) =>
            Regex.Match(text, pattern, RegexOptions.Multiline) is { Success: true } m ? m.Index : -1;

        var selfTest = StepAt(build, @"^\s+- pwsh: pwsh -NoProfile -File script/deployment/Test-DeployWilson\.ps1\s*$");
        var package = StepAt(build, @"^\s+\./script/deployment/New-DeploymentPackage\.ps1 -Environment ");
        var packageCheck = StepAt(build, @"^\s+- pwsh: pwsh -NoProfile -File script/deployment/Test-DeployWilson\.ps1 -PackagePath ");
        var publish = StepAt(build, @"^\s+- publish: ");

        Assert.True(selfTest >= 0, "The Build template must run Test-DeployWilson.ps1 without -PackagePath.");
        Assert.True(package > selfTest, "Test-DeployWilson.ps1 must run before New-DeploymentPackage.ps1.");
        Assert.True(packageCheck > package, "Test-DeployWilson.ps1 -PackagePath must run after New-DeploymentPackage.ps1.");
        Assert.True(publish > packageCheck, "The package artifact must be published only after both checks.");
        Assert.Contains("artifact: package", build[publish..]);
        Assert.Contains("dotnet test ARM.Integration.Wilson.slnx -c Release", build);
        Assert.Contains("-Environment $environments", build);
        // The package carries the production configurations too; eim-prd.yml promotes it (spec deployment-pipeline).
        Assert.Matches(new Regex(@"^\s+environments: bat,test,prd\s*$", RegexOptions.Multiline), Stage("Build"));
    }

    [Fact]
    public void The_nonproduction_pipeline_names_no_production_stage_or_resource()
    {
        var pipeline = Pipeline;

        foreach (var word in new[] { "Deploy_prd", "sc-eim-wtc-prd", "eim-wtc-prd" })
            Assert.False(pipeline.Contains(word, StringComparison.Ordinal), $"pipelines/eim.yml contains '{word}'; production deploys only from pipelines/eim-prd.yml.");
        Assert.DoesNotContain("packageSource:", pipeline);
    }

    [Fact]
    public void Every_deploy_script_path_uses_the_package_root()
    {
        // Every line that names the script outside a comment names it under the package root.
        var code = DeployTemplate.Split('\n')
            .Select(line => Regex.Replace(line, @"(^|\s)#.*$", ""))
            .Where(line => line.Contains("Deploy-Wilson.ps1", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(code);
        Assert.All(code, line => Assert.Contains("'$(packageRoot)/Deploy-Wilson.ps1'", line));
        Assert.All(code, line => Assert.Single(Regex.Matches(line, @"Deploy-Wilson\.ps1")));
    }

    [Fact]
    public void Deploy_template_downloads_the_package_from_its_source()
    {
        var deploy = DeployTemplate;

        Assert.Equal("current", ParameterDefault(deploy, "packageSource"));
        Assert.Matches(new Regex(@"^\s+- download: \$\{\{ parameters\.packageSource \}\}\s*\r?\n\s+artifact: package\s*$", RegexOptions.Multiline), deploy);
        Assert.Contains("value: $(Pipeline.Workspace)/package", deploy);
        Assert.Contains("value: $(Pipeline.Workspace)/${{ parameters.packageSource }}/package", deploy);
        Assert.Contains("${{ if eq(parameters.packageSource, 'current') }}:", deploy);
    }

    [Fact]
    public void No_manual_validation_or_publish_profile_deployment()
    {
        string[] forbidden = ["ManualValidation", "Publish" + "Profile", "Deploy-ZipWith" + "PublishProfile"];

        foreach (var file in Directory.GetFiles(Path.Combine(Root, "pipelines"), "*.yml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (var word in forbidden)
                Assert.False(text.Contains(word, StringComparison.OrdinalIgnoreCase), $"{Path.GetRelativePath(Root, file)} contains '{word}'.");
        }
    }

    [Fact]
    public void No_pipeline_maps_a_secret_variable()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(Root, "pipelines"), "*", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (var word in RemovedSecretVariables)
                Assert.False(text.Contains(word, StringComparison.OrdinalIgnoreCase), $"{Path.GetRelativePath(Root, file)} names '{word}'; there are no deployment secrets.");
        }

        var steps = DeploySteps();
        Assert.NotEmpty(steps);
        foreach (var (name, step) in steps)
        {
            // The env: block sits between the task line and its inputs.
            var header = step[..step.IndexOf("inputs:", StringComparison.Ordinal)];
            var env = Regex.Match(header, @"^(?<indent> +)env:\s*\r?\n(?<body>(?:\k<indent> {2}\S.*\r?\n)*)", RegexOptions.Multiline);

            Assert.True(env.Success, $"AzureCLI@2 step '{name}' has no env: block.");
            var mapped = Regex.Matches(env.Groups["body"].Value, @"^\s+(\S+):", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
            Assert.True(mapped.SequenceEqual(["EIM_DEPLOY_CONFIG_OVERRIDES"]), $"AzureCLI@2 step '{name}' must map only EIM_DEPLOY_CONFIG_OVERRIDES, found [{string.Join(", ", mapped)}].");
            Assert.Contains("EIM_DEPLOY_CONFIG_OVERRIDES: $(EIM_DEPLOY_CONFIG_OVERRIDES)", header);
        }
    }

    [Fact]
    public void Deploy_template_defaults_to_the_nonproduction_variable_group_and_service_connection()
    {
        var deploy = DeployTemplate;

        Assert.Contains("default: sc-eim-wtc-nonprod", deploy);
        Assert.Contains("default: eim-wtc-nonprod", deploy);
        Assert.Contains("- group: ${{ parameters.variableGroup }}", deploy);
    }

    [Fact]
    public void Deploy_template_runs_its_steps_in_order()
    {
        Assert.Equal(
            ["Preflight and preview (changes nothing)", "Infrastructure", "App settings", "Database", "Deploy ${{ parameters.components }}", "Verify and health"],
            DeploySteps().Select(s => s.Name));

        Assert.Contains("-WhatIf", DeployStep("Preflight and preview (changes nothing)"));
        Assert.Contains("-Component $components", DeployStep("Infrastructure"));
        Assert.Contains("-Component settings", DeployStep("App settings"));
        Assert.Contains("-Component ${{ parameters.components }}", DeployStep("Deploy ${{ parameters.components }}"));

        var verify = DeployStep("Verify and health");
        var verifyAt = verify.IndexOf("-Component verify", StringComparison.Ordinal);
        Assert.True(verifyAt >= 0, "The last step must run 'verify'.");
        Assert.True(verify.IndexOf("-Component health", StringComparison.Ordinal) > verifyAt, "The last step must run 'health' after 'verify'.");
    }

    [Fact]
    public void Preflight_always_runs_and_verify_and_health_run_only_when_something_is_deployed()
    {
        var deploy = DeployTemplate;

        // Preflight sits at the step-list indentation, under no condition.
        Assert.Matches(new Regex(@"^ {12}- task: AzureCLI@2\s*\r?\n {14}displayName: Preflight and preview", RegexOptions.Multiline), deploy);

        // A preflight-only run (deployCode cleared, nothing ticked) must pass before the apps exist, so health is skipped.
        Assert.Matches(new Regex(
            @"^ {12}- \$\{\{ if or\(parameters\.deployInfra, parameters\.applySettings, parameters\.runMigrations, parameters\.grantDatabaseAccess, ne\(parameters\.components, ''\)\) \}\}:\s*\r?\n" +
            @" {14}- task: AzureCLI@2\s*\r?\n {16}displayName: Verify and health\s*$", RegexOptions.Multiline), deploy);

        // In eim.yml, deployCode reaches the template as a non-empty components.
        foreach (var stage in StageOrder[1..])
            Assert.Matches(new Regex(@"^ {10}\$\{\{ if parameters\.deployCode \}\}:\s*\r?\n {12}components: \S", RegexOptions.Multiline), Stage(stage));
    }

    [Fact]
    public void The_database_step_removes_the_firewall_rule_in_a_finally()
    {
        Assert.Matches(new Regex(@"^ {12}- \$\{\{ if or\(parameters\.runMigrations, parameters\.grantDatabaseAccess\) \}\}:\s*\r?\n {14}- task: AzureCLI@2\s*\r?\n {16}displayName: Database\s*$", RegexOptions.Multiline), DeployTemplate);

        var database = DeployStep("Database");
        var myip = Regex.Match(database, @"-Component myip\s*$", RegexOptions.Multiline);
        var tryAt = database.IndexOf("try {", StringComparison.Ordinal);
        var tokenAt = database.IndexOf("az account get-access-token --resource https://database.windows.net/", StringComparison.Ordinal);
        var runAt = database.IndexOf("-Component $components", StringComparison.Ordinal);
        var finallyMatch = Regex.Match(database, @"^\s*finally \{ & \$deploy -Environment \$\{\{ parameters\.environment \}\} -Component myipremove \}\s*$", RegexOptions.Multiline);

        Assert.True(myip.Success && myip.Index < tryAt, "'myip' must run before the try.");
        Assert.True(tokenAt > tryAt && runAt > tokenAt, "The SQL token must be primed inside the try, before schema and sqlusers run.");
        Assert.True(finallyMatch.Success && finallyMatch.Index > runAt, "'myipremove' must run in a finally after schema and sqlusers.");
        Assert.Contains("if ('${{ parameters.runMigrations }}' -eq 'True') { $components += 'schema' }", database);
        Assert.Single(Regex.Matches(database, @"-Component myipremove\b"));
    }

    [Fact]
    public void Only_code_is_deployed_by_default()
    {
        var pipeline = Pipeline;

        Assert.Equal("true", ParameterDefault(pipeline, "deployCode"));
        foreach (var name in new[] { "deployInfra", "applySettings", "runMigrations", "grantDatabaseAccess" })
        {
            Assert.Equal("false", ParameterDefault(pipeline, name));
            Assert.Equal("false", ParameterDefault(DeployTemplate, name));
        }
        Assert.Equal("''", ParameterDefault(DeployTemplate, "components"));
    }

    [Fact]
    public void The_old_pipeline_folder_is_gone()
    {
        Assert.False(Directory.Exists(Path.Combine(Root, ".azure-pipelines")), ".azure-pipelines/ should be removed; pipelines live in pipelines/.");
        Assert.DoesNotContain(RepositoryPaths.CandidateFiles(Root), f => f.StartsWith(".azure-pipelines/", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(Path.Combine(Root, "pipelines", "auto-ai-pull-request.yml")));
    }
}
