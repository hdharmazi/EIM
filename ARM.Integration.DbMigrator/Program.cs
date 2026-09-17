using System.Reflection;
using DbUp;
using DbUp.Engine;
using DbUp.Helpers;
using Microsoft.Data.SqlClient;

// Applies pending versioned migrations, then re-applies every repeatable script.
//
//   ARM.Integration.DbMigrator --connection "<connection string>" [--whatif]
//
// The connection may also come from the EIMDB_MIGRATOR_CONNECTION environment variable so a
// pipeline can supply it without it appearing in a command line. It is never echoed.

const string VersionedPrefix = "Migrations.";
const string RepeatablePrefix = "Repeatable.";

// Every environment shares one database and the deploy script runs migrations as a component, so
// two environment deployments overlapping means two runners against the same database. Without a
// lock the loser reports "There is already an object named ...", which reads as a broken migration
// rather than a race. Nothing is applied twice either way; this is about the diagnosis.
const string ApplicationLockName = "ARM.Integration.DbMigrator";

// SqlClient defaults to 30 seconds, which is not enough for a data migration: backfilling
// 78k rows of MESSAGEQUEUETABLE exceeded it and rolled back.
const int DefaultTimeoutSeconds = 600;

var assembly = Assembly.GetExecutingAssembly();

string? connectionString = null;
var whatIf = false;
var timeoutSeconds = DefaultTimeoutSeconds;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--connection" or "-c" when i + 1 < args.Length:
            connectionString = args[++i];
            break;
        case "--whatif" or "--dry-run":
            whatIf = true;
            break;
        case "--timeout" when i + 1 < args.Length:
            if (!int.TryParse(args[++i], out timeoutSeconds) || timeoutSeconds < 1)
            {
                Console.Error.WriteLine("--timeout expects a positive whole number of seconds.");
                return 2;
            }
            break;
        case "--help" or "-h":
            Console.WriteLine("Usage: ARM.Integration.DbMigrator --connection \"<connection string>\" [--whatif] [--timeout <seconds>]");
            Console.WriteLine("       EIMDB_MIGRATOR_CONNECTION may be used instead of --connection.");
            Console.WriteLine($"       --timeout defaults to {DefaultTimeoutSeconds} seconds and applies per script.");
            return 0;
    }
}

var versionedScripts = assembly.GetManifestResourceNames()
    .Where(n => n.StartsWith(VersionedPrefix, StringComparison.Ordinal))
    .OrderBy(n => n, StringComparer.Ordinal)
    .ToArray();

var repeatableScripts = assembly.GetManifestResourceNames()
    .Where(n => n.StartsWith(RepeatablePrefix, StringComparison.Ordinal))
    .OrderBy(n => n, StringComparer.Ordinal)
    .ToArray();

// Build integrity, checked before the connection so a bad package fails the same way everywhere.
if (versionedScripts.Length == 0)
{
    Console.Error.WriteLine("No embedded migration scripts found. The build is not carrying script/SQL/Migrations.");
    return 2;
}

Console.WriteLine($"Embedded: {versionedScripts.Length} versioned, {repeatableScripts.Length} repeatable.");
foreach (var name in versionedScripts.Concat(repeatableScripts))
{
    Console.WriteLine($"  {name}");
}

connectionString ??= Environment.GetEnvironmentVariable("EIMDB_MIGRATOR_CONNECTION");

if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine(
        "No connection supplied. Pass --connection \"<connection string>\" or set EIMDB_MIGRATOR_CONNECTION.");
    return 2;
}

// Versioned: journaled, applied once, in filename order, each in its own transaction.
var versioned = DeployChanges.To
    .SqlDatabase(connectionString)
    .WithScriptsEmbeddedInAssembly(assembly, n => n.StartsWith(VersionedPrefix, StringComparison.Ordinal))
    .JournalToSqlTable("dbo", "SchemaVersions")
    .WithTransactionPerScript()
    .WithExecutionTimeout(TimeSpan.FromSeconds(timeoutSeconds))
    .LogToConsole()
    .Build();

// Session-scoped, so it survives DbUp's transaction-per-script. A transaction-scoped lock would
// release after the first script and serialise nothing. Held on its own connection for the whole
// run and released in the finally below.
SqlConnection? lockConnection = null;

if (!whatIf)
{
    lockConnection = new SqlConnection(connectionString);
    try
    {
        lockConnection.Open();

        using var acquire = lockConnection.CreateCommand();
        acquire.CommandText = "sp_getapplock";
        acquire.CommandType = System.Data.CommandType.StoredProcedure;
        acquire.CommandTimeout = timeoutSeconds + 60;
        acquire.Parameters.AddWithValue("@Resource", ApplicationLockName);
        acquire.Parameters.AddWithValue("@LockMode", "Exclusive");
        acquire.Parameters.AddWithValue("@LockOwner", "Session");
        // Tied to --timeout rather than a second number: a run may legitimately hold the lock for
        // as long as its slowest script is allowed to take.
        acquire.Parameters.AddWithValue("@LockTimeout", timeoutSeconds * 1000);

        var status = acquire.Parameters.Add("@ReturnValue", System.Data.SqlDbType.Int);
        status.Direction = System.Data.ParameterDirection.ReturnValue;

        acquire.ExecuteNonQuery();

        // 0 granted, 1 granted after waiting. Negative values are timeout, deadlock or error.
        var result = (int)status.Value;
        if (result < 0)
        {
            Console.Error.WriteLine(result == -1
                ? $"Another migration is in progress and did not finish within {timeoutSeconds}s. Nothing was applied; re-run once it completes."
                : $"Could not acquire the migration lock (sp_getapplock returned {result}). Nothing was applied.");
            lockConnection.Dispose();
            return 1;
        }

        if (result == 1)
        {
            Console.WriteLine("Waited for another migration to finish.");
        }
    }
    catch
    {
        lockConnection.Dispose();
        throw;
    }
}

try
{
    var pending = versioned.GetScriptsToExecute().Select(s => s.Name).ToArray();

    Console.WriteLine(pending.Length == 0
        ? "Versioned: nothing pending."
        : $"Versioned: {pending.Length} pending -> {string.Join(", ", pending)}");

    if (whatIf)
    {
        Console.WriteLine(repeatableScripts.Length == 0
            ? "Repeatable: none."
            : $"Repeatable (always re-applied): {string.Join(", ", repeatableScripts)}");
        Console.WriteLine("--whatif specified. No changes made.");
        return 0;
    }

    if (!Report(versioned.PerformUpgrade(), "versioned"))
    {
        return 1;
    }

    // Repeatable: never journaled, so they re-apply on every run. They run after versioned
    // scripts so a definition may reference a column a pending migration just added.
    if (repeatableScripts.Length > 0)
    {
        var repeatable = DeployChanges.To
            .SqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(assembly, n => n.StartsWith(RepeatablePrefix, StringComparison.Ordinal))
            .JournalTo(new NullJournal())
            .WithTransactionPerScript()
            .WithExecutionTimeout(TimeSpan.FromSeconds(timeoutSeconds))
            .LogToConsole()
            .Build();

        if (!Report(repeatable.PerformUpgrade(), "repeatable"))
        {
            return 1;
        }
    }

    Console.WriteLine("Migration complete.");
    return 0;
}
catch (SqlException ex)
{
    // A pipeline step should get an exit code and one readable line, not a CLR stack trace.
    Console.Error.WriteLine($"Database error {ex.Number}: {ex.Message.Split('\n')[0].Trim()}");

    if (ex.Number is 4060 or 18456)
    {
        // 4060 covers both causes and does not distinguish them; check the name first.
        Console.Error.WriteLine(
            "Either the database does not exist under that name, or the login has no user in it. "
            + "In Azure SQL a server login also needs: CREATE USER [<login>] FOR LOGIN [<login>];");
    }
    else if (ex.Number == -2)
    {
        Console.Error.WriteLine(
            $"The script exceeded the {timeoutSeconds}s execution timeout and was rolled back. "
            + "Re-run with a larger --timeout, or split the migration into smaller steps.");
    }

    return 1;
}
finally
{
    // Closing the connection releases a session-scoped lock, but releasing explicitly means a
    // pooled connection cannot carry the lock into someone else's session.
    if (lockConnection is not null)
    {
        try
        {
            using var release = lockConnection.CreateCommand();
            release.CommandText = "sp_releaseapplock";
            release.CommandType = System.Data.CommandType.StoredProcedure;
            release.Parameters.AddWithValue("@Resource", ApplicationLockName);
            release.Parameters.AddWithValue("@LockOwner", "Session");
            release.ExecuteNonQuery();
        }
        catch (SqlException)
        {
            // Already gone if the connection dropped, which is the only way this fails.
        }

        lockConnection.Dispose();
    }
}

static bool Report(DatabaseUpgradeResult result, string phase)
{
    if (result.Successful)
    {
        Console.WriteLine($"Applied {result.Scripts.Count()} {phase} script(s).");
        return true;
    }

    // Name the failing script: "it failed" without the filename is the common complaint.
    Console.Error.WriteLine($"FAILED during {phase} phase: {result.ErrorScript?.Name ?? "(unknown script)"}");
    Console.Error.WriteLine(result.Error?.Message ?? "(no error detail)");
    return false;
}
