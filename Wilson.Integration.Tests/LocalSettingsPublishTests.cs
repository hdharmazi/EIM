using System.Diagnostics;
using Xunit;

namespace Wilson.Integration.Tests;

/// <summary>
/// Spec integration-host-layout, "Named settings file selected" and "Local settings not published":
/// publishes the Automation host for real, rather than reading the csproj text.
/// </summary>
/// <remarks>
/// Runs a full <c>dotnet publish</c> (about 30 seconds), so it carries the Slow category; filter it out
/// with <c>--filter "Category!=Slow"</c> for a quick loop. Build and intermediate output go to a temp
/// folder through <c>--artifacts-path</c>, so the developer's own bin and obj are untouched.
/// </remarks>
public class LocalSettingsPublishTests
{
    private const string Project = "Wilson.Integration.Automation";
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromMinutes(10);

    [Fact]
    [Trait("Category", "Slow")]
    public void Publish_with_named_local_settings_copies_them_to_build_output_and_never_to_publish_output()
    {
        var projectDir = Path.Combine(RepositoryPaths.Root(), Project);
        // Letters and digits only, so the name is safe as an MSBuild property value. Git-ignored by
        // local.settings.*.json, so a file left behind by a killed run can never be committed.
        var name = "publishtest" + Guid.NewGuid().ToString("N")[..8];
        var planted = Path.Combine(projectDir, $"local.settings.{name}.json");
        var content = $$"""{ "IsEncrypted": false, "Values": { "PublishTestMarker": "{{name}}" } }""";
        var temp = Directory.CreateTempSubdirectory("wilson-publish-test-").FullName;
        var artifacts = Path.Combine(temp, "artifacts");
        var publish = Path.Combine(temp, "publish");

        try
        {
            File.WriteAllText(planted, content);

            var (exitCode, output) = RunDotnet(projectDir,
                "publish", $"{Project}.csproj",
                "--artifacts-path", artifacts,
                "-o", publish,
                $"-p:LocalSettings={name}",
                "--disable-build-servers", "-nodeReuse:false", "-v:q", "-nologo");
            Assert.True(exitCode == 0, $"dotnet publish failed with exit code {exitCode}:\n{output}");

            // Build output: the named file, under the name the Functions host reads.
            var built = Directory.GetFiles(Path.Combine(artifacts, "bin", Project), "local.settings.json", SearchOption.AllDirectories);
            var builtFile = Assert.Single(built);
            Assert.Equal(content, File.ReadAllText(builtFile));

            // Publish output: no local settings file of any name.
            Assert.True(Directory.Exists(publish), $"Publish output {publish} was not created.");
            var published = Directory.GetFiles(publish, "local.settings*.json", SearchOption.AllDirectories);
            Assert.True(published.Length == 0,
                "Local settings reached the publish output:\n" + string.Join("\n", published));
        }
        finally
        {
            // Nested, so a failed delete of the planted file still removes the temp folder.
            try
            {
                File.Delete(planted);
            }
            finally
            {
                DeleteDirectory(temp);
            }
        }
    }

    private static (int ExitCode, string Output) RunDotnet(string workingDirectory, params string[] arguments)
    {
        // The test host runs under the dotnet CLI, which points DOTNET_HOST_PATH at the same SDK.
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        // MSBuild settings inherited from the test run's own build must not steer the child build.
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToList())
            start.Environment.Remove(key);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("dotnet could not be started.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(PublishTimeout))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"dotnet {string.Join(' ', arguments)} did not finish within {PublishTimeout}.");
        }

        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    // A just-exited build can hold a handle for a moment; retry briefly rather than leave the folder.
    private static void DeleteDirectory(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 5)
            {
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
        }
    }
}
