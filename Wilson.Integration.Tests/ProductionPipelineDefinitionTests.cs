using System.Text.RegularExpressions;
using Xunit;

namespace Wilson.Integration.Tests;

/// <summary>
/// Spec deployment-pipeline: the production pipeline pipelines/eim-prd.yml. It builds nothing and promotes the package
/// of an eim run to the isolated production configuration prd, in one stage, with production credentials only.
/// </summary>
public class ProductionPipelineDefinitionTests
{
    private static readonly string[] StageOrder = ["Deploy_prd"];

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
    public void Production_has_one_stage()
    {
        Assert.Equal(StageOrder, PipelineDefinitionTests.StagesOf(Pipeline).Select(s => s.Name));

        Assert.Null(PipelineDefinitionTests.DependsOn(Stage("Deploy_prd")));
        Assert.Matches(Line("condition: succeeded\\(\\)"), Stage("Deploy_prd"));
    }

    [Fact]
    public void The_production_stage_uses_production_credentials_and_the_promoted_package()
    {
        var block = Stage("Deploy_prd");

        Assert.Contains("template: templates/deploy-environment.yml", block);
        Assert.Matches(Line("environment: prd"), block);
        Assert.Matches(Line("devopsEnvironment: eim-wtc-prd"), block);
        Assert.Matches(Line("serviceConnection: sc-eim-wtc-prd"), block);
        Assert.Matches(Line("variableGroup: eim-wtc-prd"), block);
        Assert.Matches(Line("packageSource: eim"), block);
        Assert.Matches(new Regex(@"^ {10}\$\{\{ if parameters\.deployCode \}\}:\s*\r?\n {12}components: gateway, backend, frontend, automation\s*$", RegexOptions.Multiline), block);
    }

    [Fact]
    public void Migrations_can_run_only_in_owner_stages()
    {
        var block = Stage("Deploy_prd");

        Assert.Equal("${{ parameters.runMigrations }}", PipelineDefinitionTests.StageParameter(block, "runMigrations"));
        Assert.Equal("true", PipelineDefinitionTests.StageParameter(block, "ownsResources"));
    }

    [Fact]
    public void Only_the_owner_stage_runs_the_sql_component()
    {
        // prd owns every resource it uses; nothing in production uses another configuration.
        Assert.All(PipelineDefinitionTests.StagesOf(Pipeline), s => Assert.Equal("true", PipelineDefinitionTests.StageParameter(s.Block, "ownsResources")));
    }

    [Fact]
    public void Database_access_is_a_manual_parameter_defaulting_to_false()
    {
        Assert.Equal("false", PipelineDefinitionTests.ParameterDefault(Pipeline, "grantDatabaseAccess"));
        Assert.Equal("${{ parameters.grantDatabaseAccess }}", PipelineDefinitionTests.StageParameter(Stage("Deploy_prd"), "grantDatabaseAccess"));
    }

    [Fact]
    public void Production_has_the_same_parameters_and_defaults_as_eim()
    {
        var eim = PipelineDefinitionTests.ReadFile("pipelines", "eim.yml");
        string[] names = ["deployInfra", "applySettings", "runMigrations", "grantDatabaseAccess", "deployCode"];

        foreach (var name in names)
            Assert.Equal(PipelineDefinitionTests.ParameterDefault(eim, name), PipelineDefinitionTests.ParameterDefault(Pipeline, name));

        static IEnumerable<string> ParameterNames(string text) =>
            Regex.Matches(text, @"^  - name: (\w+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value);
        Assert.Equal(names, ParameterNames(eim));
        Assert.Equal(names, ParameterNames(Pipeline));
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
