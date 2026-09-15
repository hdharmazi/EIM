using System.Data;
using Microsoft.Data.SqlClient;

namespace ARM.Integration.Core.Helpers;

/// <summary>
/// Reads configuration records from <c>ENVIRONMENTTABLE</c>, keyed by (SYSTEM, ENVIRONMENT).
/// SYSTEM is a profile axis — <c>CAPTURE</c>, <c>SOURCESYSTEM</c>, <c>D365FSC</c>,
/// <c>D365FSC-GW</c> — so one environment can hold several unrelated connections.
/// </summary>
/// <remarks>
/// The synchronous overload is the long-standing implementation, relocated here from
/// <c>EIMUtilities.GetEnvironment</c> so that callers outside ARM.Integration.EIM can use it. It
/// still goes through <see cref="SQLServerHelper"/>, which wraps every exception in a plain
/// <see cref="Exception"/>; that is preserved deliberately, because the orchestrations' failure
/// classifier walks whatever chain it is handed and changing the shape would change retry
/// behaviour. Do not "modernise" it without checking <c>DefaultFailureClassifier</c>.
///
/// The asynchronous overloads are new and have no legacy callers, so they use SqlClient directly.
/// Anything serving HTTP requests should prefer them: a blocking database call on a request thread
/// starves the thread pool at the concurrency an HTTP front end is expected to reach.
/// </remarks>
public static class EnvironmentStore
{
    private const string LegacySelectAll =
        "SELECT * FROM ENVIRONMENTTABLE WHERE SYSTEM = @SYSTEM AND ENVIRONMENT = @ENVIRONMENT";

    private const string SelectValue =
        "SELECT [VALUE] FROM dbo.ENVIRONMENTTABLE WHERE [SYSTEM] = @SYSTEM AND [ENVIRONMENT] = @ENVIRONMENT";

    /// <summary>
    /// Returns the configured value, throwing when no record exists. When
    /// <paramref name="connectionString"/> is omitted the <c>EIMDB</c> environment variable is used.
    /// </summary>
    public static string GetValue(string system = "", string environment = "", string? connectionString = null)
    {
        var conn = connectionString ?? Environment.GetEnvironmentVariable("EIMDB");

        string value = "";

        new SQLServerHelper(conn!).ExecuteReader(LegacySelectAll, reader =>
        {
            value = reader["VALUE"].ToString()!;
        }, CommandType.Text,
            new SqlParameter("@SYSTEM", system),
            new SqlParameter("@ENVIRONMENT", environment));

        if (string.IsNullOrEmpty(value))
        {
            throw new ApplicationException($"No ENVIRONMENTTABLE record could be found for environment '{environment}' and SYSTEM '{system}'.");
        }

        return value;
    }

    /// <summary>Returns the configured value, or <c>null</c> when no record exists.</summary>
    public static async Task<string?> TryGetValueAsync(
        string system,
        string environment,
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        await using var connection = new SqlConnection(connectionString);
        await using var command = new SqlCommand(SelectValue, connection);

        command.Parameters.Add("@SYSTEM", SqlDbType.NVarChar, 30).Value = system;
        command.Parameters.Add("@ENVIRONMENT", SqlDbType.NVarChar, 30).Value = environment;

        await connection.OpenAsync(cancellationToken);

        var result = await command.ExecuteScalarAsync(cancellationToken);

        // An empty value is treated as absent, matching the synchronous overload: a row whose
        // VALUE never got populated is not a usable configuration.
        return result is string value && !string.IsNullOrEmpty(value) ? value : null;
    }

    /// <summary>Returns the configured value, throwing when no record exists.</summary>
    public static async Task<string> GetValueAsync(
        string system,
        string environment,
        string connectionString,
        CancellationToken cancellationToken = default)
        => await TryGetValueAsync(system, environment, connectionString, cancellationToken)
           ?? throw new ApplicationException(
               $"No ENVIRONMENTTABLE record could be found for environment '{environment}' and SYSTEM '{system}'.");
}
