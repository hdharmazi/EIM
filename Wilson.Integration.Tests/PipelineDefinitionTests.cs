using System.Text.RegularExpressions;
using Xunit;

namespace Wilson.Integration.Tests;

/// <summary>
/// Spec deployment-automation: the Azure DevOps pipeline in pipelines/. Text assertions over eim.yml and its
/// templates, so a reordered stage, a lost secret mapping or a migration in an environment stage fails the build
/// rather than a deployment.
/// </summary>
public partial class PipelineDefinitionTests
{
    private static readonly string Root = RepositoryPaths.Root();

    // Development runs locally: there is no dev stage.
    private static readonly string[] StageOrder = ["Build", "Deploy_shared", "Deploy_bat", "Deploy_test"];

    private static string ReadFile(params string[] relative) => File.ReadAllText(Path.Combine([Root, .. relative]));

    private static string Pipeline => ReadFile("pipelines", "eim.yml");

    private static string BuildTemplate => ReadFile("pipelines", "templates", "build-package.yml");

    private static string DeployTemplate => ReadFile("pipelines", "templates", "deploy-environment.yml");

    [GeneratedRegex(@"^  - stage: (?<name>\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex StageLine();

    /// <summary>Each stage's name and its block of text, up to the next stage.</summary>
    private static List<(string Name, string Block)> Stages()
    {
        var text = Pipeline;
        var matches = StageLine().Matches(text);
        return matches
            .Select((m, i) => (m.Groups["name"].Value, text[m.Index..(i + 1 < matches.Count ? matches[i + 1].Index : text.Length)]))
            .ToList();
    }

    private static string Stage(string name) =>
        Stages().SingleOrDefault(s => s.Name == name).Block ?? throw new Xunit.Sdk.XunitException($"Stage '{name}' not found in pipelines/eim.yml.");

    private static string? DependsOn(string block) =>
        Regex.Match(block, @"^\s+dependsOn: (\S+)\s*$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>The default of a top-level template or pipeline parameter, or null when it has none.</summary>
    private static string? ParameterDefault(string text, string name)
    {
        var parameter = Regex.Match(text, $@"^  - name: {name}\b.*$(?<body>(?:\r?\n {{4,}}.*)*)", RegexOptions.Multiline);
        Assert.True(parameter.Success, $"Parameter '{name}' not found.");
        return Regex.Match(parameter.Groups["body"].Value, @"^\s+default: (.*?)\s*$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : null;
    }

    /// <summary>Each AzureCLI@2 step of the deploy template: its display name and its text, up to the next step.</summary>
    private static List<(string Name, string Block)> DeploySteps() =>
        DeployTemplate.Split("task: AzureCLI@2")[1..]
            .Select(step => (Regex.Match(step, @"displayName: (.+)").Groups[1].Value.Trim(), step))
            .ToList();

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
    public void Bat_follows_the_shared_stage_directly()
    {
        var bat = Stage("Deploy_bat");

        Assert.Equal("Deploy_shared", DependsOn(bat));
        Assert.Contains("eq(dependencies.Deploy_shared.result, 'Succeeded')", bat);
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
    [InlineData("Deploy_shared", "nonprod", "eim-wtc-nonprod")]
    [InlineData("Deploy_bat", "bat", "eim-wtc-bat")]
    [InlineData("Deploy_test", "test", "eim-wtc-test")]
    public void Each_deploy_stage_targets_its_configuration_and_devops_environment(string stage, string configuration, string devopsEnvironment)
    {
        var block = Stage(stage);

        Assert.Contains("template: templates/deploy-environment.yml", block);
        Assert.Matches(new Regex($@"^\s+environment: {configuration}\s*$", RegexOptions.Multiline), block);
        Assert.Matches(new Regex($@"^\s+devopsEnvironment: {devopsEnvironment}\s*$", RegexOptions.Multiline), block);
    }

    [Fact]
    public void Migrations_can_run_only_in_the_shared_stage()
    {
        Assert.Contains("runMigrations: ${{ parameters.runMigrations }}", Stage("Deploy_shared"));

        foreach (var stage in new[] { "Deploy_bat", "Deploy_test" })
        {
            var runMigrations = Regex.Matches(Stage(stage), @"runMigrations: (.+)$", RegexOptions.Multiline).Select(m => m.Groups[1].Value.Trim()).ToList();
            Assert.True(runMigrations.SequenceEqual(["false"]), $"{stage} must pass runMigrations: false, found [{string.Join(", ", runMigrations)}].");
        }
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
        Assert.Contains("environments: nonprod,bat,test", Stage("Build"));
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
    public void Every_azure_cli_step_maps_the_deployment_secrets()
    {
        var steps = DeployTemplate.Split("task: AzureCLI@2")[1..];

        Assert.NotEmpty(steps);
        foreach (var step in steps)
        {
            // The env: block sits between the task line and its inputs.
            var header = step[..step.IndexOf("inputs:", StringComparison.Ordinal)];
            var name = Regex.Match(header, @"displayName: (.+)").Groups[1].Value.Trim();

            Assert.True(header.Contains("env:"), $"AzureCLI@2 step '{name}' has no env: block.");
            Assert.True(header.Contains("EIM_DEPLOY_EIMDB: $(EIM_DEPLOY_EIMDB)"), $"AzureCLI@2 step '{name}' does not map EIM_DEPLOY_EIMDB.");
            Assert.True(header.Contains("EIM_DEPLOY_SERVICEBUS: $(EIM_DEPLOY_SERVICEBUS)"), $"AzureCLI@2 step '{name}' does not map EIM_DEPLOY_SERVICEBUS.");
        }
    }

    [Fact]
    public void Deploy_template_uses_the_shared_variable_group_and_service_connection()
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
            ["Preflight and preview (changes nothing)", "Infrastructure", "App settings", "Database migrations", "Deploy ${{ parameters.components }}", "Verify and health"],
            DeploySteps().Select(s => s.Name));

        Assert.Contains("-WhatIf", DeployStep("Preflight and preview (changes nothing)"));
        Assert.Contains("-Component infra", DeployStep("Infrastructure"));
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
            @"^ {12}- \$\{\{ if or\(parameters\.deployInfra, parameters\.applySettings, parameters\.runMigrations, ne\(parameters\.components, ''\)\) \}\}:\s*\r?\n" +
            @" {14}- task: AzureCLI@2\s*\r?\n {16}displayName: Verify and health\s*$", RegexOptions.Multiline), deploy);

        // In eim.yml, deployCode reaches the template as a non-empty components.
        foreach (var stage in StageOrder[1..])
            Assert.Matches(new Regex(@"^ {10}\$\{\{ if parameters\.deployCode \}\}:\s*\r?\n {12}components: \S", RegexOptions.Multiline), Stage(stage));
    }

    [Fact]
    public void The_firewall_rule_is_removed_in_a_finally()
    {
        var migrations = DeployStep("Database migrations");
        var myip = Regex.Match(migrations, @"-Component myip\s*$", RegexOptions.Multiline);

        Assert.Matches(new Regex(
            @"^\s*try \{ & \$deploy -Environment \$\{\{ parameters\.environment \}\} -Component schema \}\s*\r?\n" +
            @"\s*finally \{ & \$deploy -Environment \$\{\{ parameters\.environment \}\} -Component myipremove \}", RegexOptions.Multiline), migrations);
        Assert.True(myip.Success && myip.Index < migrations.IndexOf("try {", StringComparison.Ordinal), "'myip' must run before the try that runs 'schema'.");
        Assert.Single(Regex.Matches(migrations, @"-Component myipremove\b"));
    }

    [Fact]
    public void Only_code_is_deployed_by_default()
    {
        var pipeline = Pipeline;

        Assert.Equal("true", ParameterDefault(pipeline, "deployCode"));
        foreach (var name in new[] { "deployInfra", "applySettings", "runMigrations" })
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
