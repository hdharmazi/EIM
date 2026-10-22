using System.Text.RegularExpressions;
using Xunit;

namespace Wilson.Integration.Tests;

/// <summary>Spec integration-host-layout: the repository contains no credentials.</summary>
public partial class SecretScanTests
{
    private static readonly string[] ScannedExtensions =
        [".cs", ".json", ".ts", ".tsx", ".bat", ".ps1", ".sql", ".md", ".yml", ".yaml", ".config", ".env"];

    // Real development values; git-ignored by the same rule (local.settings*.json except the samples).
    private static bool IsLocalSettings(string fileName) =>
        fileName.StartsWith("local.settings.", StringComparison.OrdinalIgnoreCase)
        && fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
        && !fileName.Equals("local.settings.sample.json", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"[A-Za-z0-9_\-]{3}\d?Q~[A-Za-z0-9_\.\-~]{30,}")]
    private static partial Regex EntraClientSecret();

    [GeneratedRegex(@"SharedAccessKey=[A-Za-z0-9+/]{20,}={0,2}")]
    private static partial Regex ServiceBusKey();

    [GeneratedRegex(@"AccountKey=[A-Za-z0-9+/]{40,}={0,2}")]
    private static partial Regex StorageKey();

    // Connection strings (Password=x), attributes (Password="x"), JSON or YAML keys ("Password": "x") and
    // XML elements (<Password>x</Password>). The value may open with a quote; a value opening with '<' is
    // a placeholder.
    [GeneratedRegex(@"(?i)(password|pwd)[""']?\s*[=:]\s*[""']?(?!<)[^;""'\s]{6,}|<(password|pwd)>\s*(?!<)[^<\s]{6,}")]
    private static partial Regex ConnectionStringPassword();

    // Known false positives, matched by path suffix and exact text, so a real secret added to the same
    // file still fails. Add an entry only for text that is provably not a credential, and say why.
    private static readonly (string File, string Match)[] AllowList =
    [
        // Describes the password pattern itself. A suffix, so the entry survives the change being archived.
        ("align-structure-and-deployment/design.md", "Password=`/`Pwd=`"),
        // PowerShell expressions that read a password at runtime; neither is a literal.
        ("script/Repair-TruncatedSourceId.ps1", "password = [regex]::Match($ConnectionString,"),
        ("script/deployment/Deploy-ZipWithPublishProfile.ps1", "password = [string]"),
    ];

    private static IEnumerable<(Regex Pattern, string Value)> Matches(string text) =>
        new[] { EntraClientSecret(), ServiceBusKey(), StorageKey(), ConnectionStringPassword() }
            .SelectMany(r => r.Matches(text).Select(m => (r, m.Value)));

    // Only the first eight characters are reported, so a failure does not reprint the secret.
    private static string Describe((Regex Pattern, string Value) match) =>
        $"{match.Pattern}: {match.Value[..Math.Min(8, match.Value.Length)]}…";

    internal static IEnumerable<string> Findings(string text) => Matches(text).Select(Describe);

    [Fact]
    public void Repository_contains_no_credentials()
    {
        var root = RepositoryPaths.Root();
        var findings = RepositoryPaths.CandidateFiles(root)
            .Where(f => ScannedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => !IsLocalSettings(Path.GetFileName(f)))
            .Where(f => !f.EndsWith(nameof(SecretScanTests) + ".cs", StringComparison.Ordinal))
            .SelectMany(f => Matches(File.ReadAllText(Path.Combine(root, f)))
                .Where(m => !AllowList.Any(a => f.EndsWith(a.File, StringComparison.Ordinal) && m.Value == a.Match))
                .Select(m => $"{f} → {Describe(m)}"))
            .ToList();

        Assert.True(findings.Count == 0, "Credentials found:\n" + string.Join("\n", findings));
    }

    [Theory]
    [InlineData("var credential = new ClientSecretCredential(\"t\", \"c\", \"2Ra8Q~abcdefghijklmnopqrstuvwxyz0123456789\");")]
    [InlineData("Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKeyName=root;SharedAccessKey=abcdefghijklmnopqrstuvwxyz0123456789ABCD=")]
    [InlineData("DefaultEndpointsProtocol=https;AccountName=st;AccountKey=abcdefghijklmnopqrstuvwxyz0123456789ABCDEFGH==")]
    [InlineData("Server=db;User Id=sa;Password=Hunter2!x;")]
    [InlineData("<add name=\"EIMDB\" Password=\"Hunter2!x\" />")]
    [InlineData("{ \"Password\": \"Hunter2!x\" }")]
    [InlineData("Server=db;Pwd='Hunter2!x';")]
    [InlineData("Server=db;Password=Ab<9xYz12345;")]
    [InlineData("<Password>Hunter2!x</Password>")]
    public void Scanner_flags_a_planted_secret(string planted)
    {
        Assert.NotEmpty(Findings(planted));
    }

    [Theory]
    [InlineData("\"EIMDB\": \"<eimdb sql connection string>\"")]
    [InlineData("SharedAccessKey=<key>")]
    [InlineData("Integrated Security=true;TrustServerCertificate=true")]
    [InlineData("\"AzureWebJobsStorage\": \"UseDevelopmentStorage=true\"")]
    [InlineData("Server=db;User Id=sa;Password=<sql password>;")]
    [InlineData("<add name=\"EIMDB\" Password=\"<sql password>\" />")]
    [InlineData("{ \"Password\": \"<sql password>\" }")]
    [InlineData("<Password><sql password></Password>")]
    public void Scanner_ignores_placeholders(string placeholder)
    {
        Assert.Empty(Findings(placeholder));
    }
}
