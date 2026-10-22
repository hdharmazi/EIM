using System.Xml.Linq;
using Xunit;

namespace Wilson.Integration.Tests;

/// <summary>
/// Spec integration-host-layout, "Engine and client tests are separate": the engine's tests must build
/// without any client project, so a client-specific test cannot drift back into the engine suite.
/// </summary>
public class ProjectReferenceTests
{
    [Fact]
    public void Engine_tests_reference_no_client_project()
    {
        var csproj = Path.Combine(RepositoryPaths.Root(), "ARM.Integration.EIM.Tests", "ARM.Integration.EIM.Tests.csproj");

        var clientReferences = XDocument.Load(csproj)
            .Descendants()
            .Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => (string?)e.Attribute("Include") ?? "")
            .Where(path => Path.GetFileName(path.Replace('\\', '/')).StartsWith("Wilson.", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(clientReferences.Count == 0,
            "ARM.Integration.EIM.Tests references client projects: " + string.Join(", ", clientReferences)
            + ". Client tests belong in Wilson.Integration.Tests.");
    }
}
