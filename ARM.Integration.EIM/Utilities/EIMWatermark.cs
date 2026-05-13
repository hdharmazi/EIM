using Microsoft.Data.SqlClient;

namespace ARM.Integration.EIM;

/// <summary>
/// Tracks the last successful poll time per integration in EIMDB.
/// Uses a simple WATERMARKTABLE: INTEGRATION (PK) + LASTRUN (datetime).
/// 
/// SQL to create the table:
/// <code>
/// CREATE TABLE [dbo].[WATERMARKTABLE](
///     [INTEGRATION] [nvarchar](100) NOT NULL,
///     [LASTRUN] [datetime] NOT NULL,
///     CONSTRAINT [PK_WATERMARKTABLE] PRIMARY KEY CLUSTERED ([INTEGRATION] ASC)
/// )
/// </code>
/// </summary>
public static class EIMWatermark
{
    /// <summary>
    /// Gets the last successful run time for the given integration.
    /// Returns null if the integration has never run.
    /// </summary>
    public static DateTime? GetLastRun(string connectionString, string integrationName)
    {
        const string sql = "SELECT LASTRUN FROM WATERMARKTABLE WHERE INTEGRATION = @Integration";

        using var conn = new SqlConnection(connectionString);
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Integration", integrationName);

        conn.Open();
        var result = cmd.ExecuteScalar();

        return result is DateTime dt ? dt : null;
    }

    /// <summary>
    /// Updates (or inserts) the last successful run time for the given integration.
    /// </summary>
    public static void UpdateLastRun(string connectionString, string integrationName, DateTime lastRun)
    {
        const string sql = @"
            MERGE WATERMARKTABLE AS target
            USING (SELECT @Integration AS INTEGRATION) AS source
            ON target.INTEGRATION = source.INTEGRATION
            WHEN MATCHED THEN UPDATE SET LASTRUN = @LastRun
            WHEN NOT MATCHED THEN INSERT (INTEGRATION, LASTRUN) VALUES (@Integration, @LastRun);";

        using var conn = new SqlConnection(connectionString);
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Integration", integrationName);
        cmd.Parameters.AddWithValue("@LastRun", lastRun);

        conn.Open();
        cmd.ExecuteNonQuery();
    }
}
