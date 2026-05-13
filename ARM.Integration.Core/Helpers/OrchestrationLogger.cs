using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace ARM.Integration.Core.Helpers;

/// <summary>
/// Writes step-level logs to the MESSAGELOGDETAIL table and forwards to <see cref="ILogger"/>.
/// </summary>
public sealed class OrchestrationLogger(string connectionString, long messageId, ILogger logger) : IOrchestrationLogger
{
    public ILogger Logger => logger;

    public void Log(string name, string? detail = null)
    {
        logger.LogInformation("[{StepName}] {Detail}", name, detail ?? "Started");
        InsertRow(name, detail);
    }

    public void LogWarning(string name, string? detail = null)
    {
        logger.LogWarning("[{StepName}] {Detail}", name, detail);
        InsertRow(name, detail);
    }

    public void LogError(string name, string? detail = null)
    {
        logger.LogError("[{StepName}] {Detail}", name, detail);
        InsertRow(name, detail);
    }

    private void InsertRow(string name, string? detail)
    {
        const string sql = """
            INSERT INTO MESSAGELOGDETAIL (MESSAGEID, NAME, DETAIL)
            VALUES (@MessageId, @Name, @Detail)
            """;

        SqlParameter[] parameters =
        [
            new("@MessageId", SqlDbType.BigInt)        { Value = messageId },
            new("@Name",      SqlDbType.NVarChar, 200) { Value = name },
            new("@Detail",    SqlDbType.NVarChar, -1)  { Value = (object?)detail ?? DBNull.Value }
        ];

        new SQLServerHelper(connectionString).ExecuteNonQuery(sql, CommandType.Text, parameters);
    }
}
