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

    // Built by concatenation, like OldProjectName, so this file does not match its own search.
    private static readonly string[] RetiredDeploymentNames =
    [
        string.Join("-", "azf", "d365fsc", "gateway", "wtc", "nonprod"),
        "Deploy-" + "Eim.ps1",
        "Deploy-ZipWith" + "PublishProfile.ps1",
    ];

    // Places that name the retired gateway and scripts on purpose: change records, the cutover runbook (the
    // old -> new table), the configs' legacy sections, and the gateway tests' fixed sample host names.
    private static readonly string[] RetiredNameExclusions =
    [
        "openspec/changes/",
        "docs/deployment/resource-rename-cutover.md",
        "ARM.Integration.D365FSC.Gateway.Tests/",
    ];

    private static bool IsDeploymentConfig(string file) =>
        file.StartsWith("script/deployment/config/", StringComparison.OrdinalIgnoreCase)
        && Path.GetExtension(file).Equals(".json", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void No_file_references_the_retired_gateway_or_deployment_scripts()
    {
        var offenders = RepositoryPaths.CandidateFiles(Root)
            .Where(f => TextExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => !RetiredNameExclusions.Any(p => f.StartsWith(p, StringComparison.OrdinalIgnoreCase)) && !IsDeploymentConfig(f))
            .SelectMany(f =>
            {
                var text = File.ReadAllText(Path.Combine(Root, f));
                return RetiredDeploymentNames.Where(n => text.Contains(n, StringComparison.OrdinalIgnoreCase)).Select(n => $"{f}: {n}");
            })
            .ToList();

        Assert.True(offenders.Count == 0,
            "Files still referencing the retired gateway name or deployment scripts (see docs/deployment/resource-rename-cutover.md):\n"
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
