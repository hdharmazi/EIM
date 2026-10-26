using System.Text.RegularExpressions;
using Xunit;

namespace Wilson.Integration.Tests;

/// <summary>
/// Spec deployment-pipeline: the production pipeline pipelines/eim-prd.yml. It builds nothing and promotes the package
/// of an eim run through Deploy_prdshared, then Deploy_prd, with production credentials only.
/// </summary>
public class ProductionPipelineDefinitionTests
{
    private static readonly string[] StageOrder = ["Deploy_prdshared", "Deploy_prd"];

    private static string Pipeline => PipelineDefinitionTests.ReadFile("pipelines", "eim-prd.yml");

    private static string Stage(string name) =>
        PipelineDefinitionTests.StagesOf(Pipeline).SingleOrDefault(s => s.Name == name).Block
        ?? throw new Xunit.Sdk.XunitException($"Stage '{name}' not found in pipelines/eim-prd.yml.");

    private static Regex Line(string text) => new($@"^\s*{text}\s*$", RegexOptions.Multiline);

    [Fact]
    public void Production_runs_only_manually()
    {
        Assert.Matches(new Regex(@"^trigger: none\s*$", RegexOptions.Multiline), Pipeline);
        Assert.Matches(new Regex(@"^pr: none\s*$", RegexOptions.Multiline), Pipeline);
    }

    [Fact]
    public void Production_promotes_the_eim_package_and_builds_nothing()
    {
        var pipeline = Pipeline;

        // branch: main makes the latest eim run from main the default; pull request and feature runs publish a package too.
        Assert.Matches(new Regex(@"^resources:\s*\r?\n  pipelines:\s*\r?\n(?:\s*#.*\r?\n)*    - pipeline: eim\s*\r?\n      source: eim\s*\r?\n      branch: main\s*\r?\n      trigger: none\s*$", RegexOptions.Multiline), pipeline);
        Assert.DoesNotContain(PipelineDefinitionTests.StagesOf(pipeline), s => s.Name == "Build");
        Assert.DoesNotContain("build-package.yml", pipeline);
        Assert.False(Regex.IsMatch(pipeline, @"^\s*-?\s*(script|pwsh|bash|powershell):.*\b(dotnet|npm)\b", RegexOptions.Multiline), "eim-prd.yml must not run dotnet or npm.");
        Assert.False(Regex.IsMatch(pipeline, @"^\s*-\s*task: (DotNetCoreCLI|Npm)@", RegexOptions.Multiline), "eim-prd.yml must not run dotnet or npm tasks.");
    }

    [Fact]
    public void Production_stages_run_in_order()
    {
        Assert.Equal(StageOrder, PipelineDefinitionTests.StagesOf(Pipeline).Select(s => s.Name));

        Assert.Null(PipelineDefinitionTests.DependsOn(Stage("Deploy_prdshared")));
        Assert.Equal("Deploy_prdshared", PipelineDefinitionTests.DependsOn(Stage("Deploy_prd")));
        foreach (var stage in StageOrder)
            Assert.Matches(Line("condition: succeeded\\(\\)"), Stage(stage));
    }

    [Theory]
    [InlineData("Deploy_prdshared", "prdshared", "eim-wtc-prdshared", "gateway, backend, frontend")]
    [InlineData("Deploy_prd", "prd", "eim-wtc-prd", "automation")]
    public void Each_production_stage_uses_production_credentials_and_the_promoted_package(string stage, string configuration, string devopsEnvironment, string components)
    {
        var block = Stage(stage);

        Assert.Contains("template: templates/deploy-environment.yml", block);
        Assert.Matches(Line($"environment: {configuration}"), block);
        Assert.Matches(Line($"devopsEnvironment: {devopsEnvironment}"), block);
        Assert.Matches(Line("serviceConnection: sc-eim-wtc-prd"), block);
        Assert.Matches(Line("variableGroup: eim-wtc-prd"), block);
        Assert.Matches(Line("packageSource: eim"), block);
        Assert.Matches(new Regex(@"^ {10}\$\{\{ if parameters\.deployCode \}\}:\s*\r?\n {12}components: " + Regex.Escape(components) + @"\s*$", RegexOptions.Multiline), block);
    }

    [Fact]
    public void Migrations_can_run_only_in_the_production_shared_stage()
    {
        Assert.Matches(Line(Regex.Escape("runMigrations: ${{ parameters.runMigrations }}")), Stage("Deploy_prdshared"));

        var runMigrations = Regex.Matches(Stage("Deploy_prd"), @"runMigrations: (.+)$", RegexOptions.Multiline).Select(m => m.Groups[1].Value.Trim()).ToList();
        Assert.True(runMigrations.SequenceEqual(["false"]), $"Deploy_prd must pass runMigrations: false, found [{string.Join(", ", runMigrations)}].");
    }

    [Fact]
    public void Production_has_the_same_parameters_and_defaults_as_eim()
    {
        var eim = PipelineDefinitionTests.ReadFile("pipelines", "eim.yml");

        foreach (var name in new[] { "deployInfra", "applySettings", "runMigrations", "deployCode" })
            Assert.Equal(PipelineDefinitionTests.ParameterDefault(eim, name), PipelineDefinitionTests.ParameterDefault(Pipeline, name));
    }

    [Fact]
    public void A_newer_production_run_replaces_an_older_waiting_run()
    {
        Assert.Matches(new Regex(@"^lockBehavior: runLatest\s*$", RegexOptions.Multiline), Pipeline);
    }

    [Fact]
    public void Production_names_no_nonproduction_credentials()
    {
        var pipeline = Pipeline;

        foreach (var word in new[] { "sc-eim-wtc-nonprod", "eim-wtc-nonprod", "eim-wtc-bat", "eim-wtc-test" })
            Assert.False(pipeline.Contains(word, StringComparison.Ordinal), $"pipelines/eim-prd.yml contains '{word}'.");
    }
}
