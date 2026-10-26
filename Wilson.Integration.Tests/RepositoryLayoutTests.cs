using Xunit;

namespace Wilson.Integration.Tests;

/// <summary>
/// Spec integration-host-layout: the worker lives under its client name, and the files of the old
/// container and Visual Studio publishing paths are gone.
/// </summary>
public class RepositoryLayoutTests
{
    // Built by concatenation so this file does not match its own search. The short form also catches the
    // full ARM.Integration-prefixed project name, and prose that names the project by the short form alone.
    private static readonly string OldProjectName = string.Join(".", "EIM", "Customer");

    private static readonly string[] TextExtensions =
        [".cs", ".csproj", ".slnx", ".sln", ".props", ".targets", ".json", ".ts", ".tsx", ".js", ".bat", ".cmd",
         ".ps1", ".psm1", ".sql", ".md", ".yml", ".yaml", ".config", ".xml", ".txt", ".http"];

    // Historical records keep the old name: archived changes, the #5425 change that predates the rename,
    // and this change's own artifacts, which describe the rename itself. All three end up under archive/.
    private static readonly string[] ExcludedPrefixes =
    [
        "openspec/changes/archive/",
        "openspec/changes/fix-address-source-business-key/",
        "openspec/changes/align-structure-and-deployment/",
    ];

    private static readonly string Root = RepositoryPaths.Root();

    private static string ReadFile(params string[] relative) => File.ReadAllText(Path.Combine([Root, .. relative]));

    [Fact]
    public void No_file_names_the_old_project()
    {
        var offenders = RepositoryPaths.CandidateFiles(Root)
            .Where(f => TextExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => !ExcludedPrefixes.Any(p => f.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .Where(f => File.ReadAllText(Path.Combine(Root, f)).Contains(OldProjectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Files still naming {OldProjectName} (or ARM.Integration.{OldProjectName}):\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Container_and_visual_studio_publishing_leftovers_are_absent()
    {
        Assert.False(File.Exists(Path.Combine(Root, ".dockerignore")), ".dockerignore should be deleted.");
        Assert.False(Directory.Exists(Path.Combine(Root, ".appmod")), ".appmod should be deleted.");

        var serviceDependencies = RepositoryPaths.CandidateFiles(Root)
            .Where(f => f.Contains("Properties/ServiceDependencies/", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(f).StartsWith("serviceDependencies", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(serviceDependencies.Count == 0,
            "Visual Studio service dependency files should be deleted:\n" + string.Join("\n", serviceDependencies));
        Assert.False(Directory.Exists(Path.Combine(Root, "Wilson.Integration.Automation", "Properties", "ServiceDependencies")));
    }

    [Fact]
    public void Polling_trigger_template_is_absent()
    {
        var pollHandlers = RepositoryPaths.CandidateFiles(Root)
            .Where(f => Path.GetFileName(f).Equals("PollSourceHandler.cs", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(pollHandlers.Count == 0, "PollSourceHandler.cs should be deleted:\n" + string.Join("\n", pollHandlers));

        var samplePoll = ReadFile("Wilson.Integration.Orchestrations", "Orchestrations", "SamplePoll_To_Target.cs");
        Assert.DoesNotContain("PollSourceHandler", samplePoll);
    }

    [Fact]
    public void Solution_has_no_crm_solution_properties()
    {
        Assert.DoesNotContain("CRMSolutionProperties", ReadFile(RepositoryPaths.SolutionFile));
    }

    [Fact]
    public void Automation_launch_settings_use_port_7071_and_no_docker_profile()
    {
        var launchSettings = ReadFile("Wilson.Integration.Automation", "Properties", "launchSettings.json");

        Assert.Contains("--port 7071", launchSettings);
        Assert.DoesNotContain("Docker", launchSettings, StringComparison.OrdinalIgnoreCase);
    }

    // Built by concatenation, like OldProjectName, so this file does not match its own search. The names of the
    // earlier gateway, Automation and plan resources, and the nonprod-suffixed names of the shared layout that
    // bat now owns under its own names (spec resource-naming-cutover).
    private static readonly string[] RetiredDeploymentNames =
    [
        string.Join("-", "azf", "d365fsc", "gateway", "wtc"),
        string.Join("-", "azf", "eim", "customer", "wtc"),
        "stwtceim" + "customer",
        string.Join("-", "appins", "eim", "customer", "wtc"),
        "stwtcd365" + "gw",
        string.Join("-", "appins", "d365fsc", "gateway", "wtc"),
        string.Join("-", "asp", "appservice", "p0v3", "linux"),
        string.Join("-", "asp", "eim", "wtc", "nonprod"),
        string.Join("-", "azf", "eim", "gateway", "wtc", "nonprod"),
        string.Join("-", "log", "eim", "wtc", "nonprod"),
        "Deploy-" + "Eim.ps1",
        "Deploy-ZipWith" + "PublishProfile.ps1",
    ];

    // Places that name the retired resources and scripts on purpose: change records, the superseded scripts, and
    // the gateway tests' fixed sample host names.
    private static readonly string[] RetiredNameExclusions =
    [
        "openspec/changes/",
        "script/Superseded/",
        "ARM.Integration.D365FSC.Gateway.Tests/",
    ];

    [Fact]
    public void No_file_references_the_retired_gateway_or_deployment_scripts()
    {
        var offenders = RepositoryPaths.CandidateFiles(Root)
            .Where(f => TextExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => !RetiredNameExclusions.Any(p => f.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(f =>
            {
                var text = File.ReadAllText(Path.Combine(Root, f));
                return RetiredDeploymentNames.Where(n => text.Contains(n, StringComparison.OrdinalIgnoreCase)).Select(n => $"{f}: {n}");
            })
            .ToList();

        Assert.True(offenders.Count == 0,
            "Files still referencing a retired resource name or deployment script:\n" + string.Join("\n", offenders));
    }

    // Spec resource-naming-cutover: the deployment describes only the current layout, with no move, overlap or
    // reversal procedure, no retired configuration keys or stages, and no secret variables. Built by
    // concatenation, like OldProjectName, so this file does not match its own search.
    private static readonly string[] RollbackTerms = ["roll" + "back", "roll " + "back"];

    private static readonly string[] DeploymentBannedTerms =
    [
        "leg" + "acy",
        "cut" + "over",
        "cut " + "over",
        .. RollbackTerms,
        "retired" + "AutomationApps",
        "Include" + "DefaultKeys",
        "shared" + "Suffix",
        "prd" + "shared",
        "Deploy_" + "shared",
        "EIM_DEPLOY_" + "EIMDB",
        "EIM_DEPLOY_" + "SERVICEBUS",
    ];

    private static readonly string[] DeploymentFolders = ["script/deployment/", "pipelines/", "docs/deployment/"];

    private static readonly string[] DeploymentFiles =
        ["README.md", "docs/README.md", "script/README.md", "CLAUDE.md", RepositoryPaths.SolutionFile];

    // The migration guide explains why the schema only moves forward, which needs the word.
    private const string RollbackExemptFile = "docs/deployment/database-migrations.md";

    [Fact]
    public void No_deployment_file_describes_a_cutover()
    {
        var files = RepositoryPaths.CandidateFiles(Root)
            .Where(f => DeploymentFolders.Any(d => f.StartsWith(d, StringComparison.OrdinalIgnoreCase))
                || DeploymentFiles.Contains(f, StringComparer.OrdinalIgnoreCase))
            .ToList();
        Assert.Contains(files, f => f.Equals("script/deployment/Deploy-Wilson.ps1", StringComparison.OrdinalIgnoreCase));

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var terms = file.Equals(RollbackExemptFile, StringComparison.OrdinalIgnoreCase)
                ? DeploymentBannedTerms.Except(RollbackTerms).ToArray()
                : DeploymentBannedTerms;

            var lines = File.ReadAllLines(Path.Combine(Root, file));
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var term in terms.Where(t => lines[i].Contains(t, StringComparison.OrdinalIgnoreCase)))
                    offenders.Add($"{file}:{i + 1}: {term}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Deployment files still describing a move, a retired key or stage, or a secret variable:\n"
            + string.Join("\n", offenders));
    }

    // Spec resource-naming-cutover, "No dev Automation resources": development runs locally, so nothing names a dev
    // stage, DevOps environment, configuration or new dev Automation resource. Built by concatenation, like
    // OldProjectName, so this file does not match its own search.
    private static readonly string[] DevDeploymentNames =
    [
        "Deploy_" + "dev",
        "eim-wtc-" + "dev",
        "deploy." + "dev.json",
        "azf-eim-automation-wtc-" + "dev",
        "stwtceimauto" + "dev",
        "appins-eim-automation-wtc-" + "dev",
    ];

    // Change records, the tests that assert these names are absent, and the self-test that asserts the same.
    private static readonly string[] DevNameExclusions =
    [
        "openspec/changes/",
        "Wilson.Integration.Tests/",
        "script/deployment/Test-DeployWilson.ps1",
    ];

    [Fact]
    public void No_file_names_a_dev_deployment()
    {
        var offenders = RepositoryPaths.CandidateFiles(Root)
            .Where(f => TextExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => !DevNameExclusions.Any(p => f.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(f =>
            {
                var text = File.ReadAllText(Path.Combine(Root, f));
                return DevDeploymentNames.Where(n => text.Contains(n, StringComparison.OrdinalIgnoreCase)).Select(n => $"{f}: {n}");
            })
            .ToList();

        Assert.True(offenders.Count == 0,
            "Files still naming a dev deployment stage, environment, configuration or Automation resource (development runs locally):\n"
            + string.Join("\n", offenders));
    }

    // A cheap text check covering both hosts. LocalSettingsPublishTests proves the behaviour for the
    // Automation host with a real publish.
    [Theory]
    [InlineData("Wilson.Integration.Automation", "Wilson.Integration.Automation.csproj")]
    [InlineData("ARM.Integration.D365FSC.Gateway", "ARM.Integration.D365FSC.Gateway.csproj")]
    public void Local_settings_are_never_published(string folder, string csproj)
    {
        var project = ReadFile(folder, csproj);

        Assert.Contains("local.settings.json", project);
        Assert.Contains("<CopyToPublishDirectory>Never</CopyToPublishDirectory>", project);
    }
}
