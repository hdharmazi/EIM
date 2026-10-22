using System.Diagnostics;

namespace Wilson.Integration.Tests;

/// <summary>Locates the repository root and its files, for tests that inspect the tree.</summary>
internal static class RepositoryPaths
{
    public const string SolutionFile = "ARM.Integration.Wilson.slnx";

    /// <summary>Build output, tooling state and local emulator data; never source.</summary>
    public static readonly string[] SkippedDirectories =
        ["bin", "obj", "node_modules", "dist", ".git", ".vs", ".azurite", "TestResults", "artifacts"];

    public static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, SolutionFile)))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException($"Repository root not found (no {SolutionFile} above {AppContext.BaseDirectory}).");
    }

    /// <summary>
    /// Files that are or could be committed, as paths relative to <paramref name="root"/> with '/'
    /// separators: tracked files plus untracked files git does not ignore. Ignored developer files
    /// (local.settings.json, .env) are deliberately out of scope, because they can never reach the
    /// repository. Only git knows which files those are, so there is no filesystem fallback: without
    /// git a walk would read ignored files holding real credentials. Throws when git cannot list files.
    /// </summary>
    public static IReadOnlyList<string> CandidateFiles(string root)
    {
        return GitFiles(root)
            .Where(f => !f.Split('/').SkipLast(1).Any(part => SkippedDirectories.Contains(part, StringComparer.OrdinalIgnoreCase)))
            // A file deleted in the working tree but not yet staged is still listed by git.
            .Where(f => File.Exists(Path.Combine(root, f)))
            .ToList();
    }

    private static List<string> GitFiles(string root)
    {
        const string required = "git is required for repository scans";
        var start = new ProcessStartInfo("git", "ls-files --cached --others --exclude-standard -z")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        Process? git;
        try
        {
            git = Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"{required}: git could not be started ({ex.Message}).", ex);
        }

        using (git)
        {
            if (git is null)
                throw new InvalidOperationException($"{required}: git could not be started.");

            // Read stderr concurrently, so a full stderr pipe cannot block git while stdout is drained.
            var error = git.StandardError.ReadToEndAsync();
            var output = git.StandardOutput.ReadToEnd();
            git.WaitForExit();

            if (git.ExitCode != 0)
                throw new InvalidOperationException(
                    $"{required}: 'git ls-files' failed in {root} with exit code {git.ExitCode}. {error.Result.Trim()}");

            return output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToList();
        }
    }
}
