using ARM.Integration.EIM;
using ARM.Integration.EIM.Routing;
using Microsoft.Data.SqlClient;

namespace ARM.Integration.Dashboard.API.Services;

/// <param name="HeldCount">Messages waiting at Held for this route.</param>
/// <param name="RetryScheduledCount">Messages waiting at Retry Scheduled for this route.</param>
/// <param name="OldestHeldUtc">Null when nothing is held. Drives the age shown on the console.</param>
public sealed record RouteRecord(
    string Environment,
    string SourceSystem,
    string SourceEntity,
    string OrchestrationType,
    bool Enabled,
    string OnPause,
    string? ModifiedBy,
    DateTime? ModifiedDateTime,
    int HeldCount,
    int RetryScheduledCount,
    DateTime? OldestHeldUtc)
{
    /// <summary>
    /// Paused, Draining or Live. Derived rather than stored: a route is only live once its backlog
    /// has cleared, otherwise a new message would overtake older ones still waiting.
    /// </summary>
    public string Lifecycle =>
        !Enabled ? "Paused"
        : HeldCount > 0 || RetryScheduledCount > 0 ? "Draining"
        : "Live";
}

/// <param name="Count">Messages stranded on this non-existent route.</param>
/// <param name="OldestUtc">How long they have been stuck.</param>
public sealed record OrphanedBacklog(
    string Environment,
    string SourceSystem,
    string SourceEntity,
    int Count,
    DateTime? OldestUtc);

public class RouteRepository(IConfiguration configuration, IRouteResolver resolver)
{
    private readonly string _connectionString = configuration.GetConnectionString("EIMDB")
        ?? throw new InvalidOperationException("ConnectionStrings:EIMDB is not configured.");

    public async Task<List<RouteRecord>> GetAllAsync()
    {
        // Counts come from one grouped pass rather than a correlated subquery per route.
        const string sql = """
            WITH backlog AS (
                SELECT ENVIRONMENT, SOURCESYSTEM, SOURCEENTITY,
                       SUM(CASE WHEN STATUS = @HELD THEN 1 ELSE 0 END) AS HELDCOUNT,
                       SUM(CASE WHEN STATUS = @RETRY THEN 1 ELSE 0 END) AS RETRYCOUNT,
                       MIN(CASE WHEN STATUS = @HELD THEN CREATEDDATETIME END) AS OLDESTHELD
                FROM MESSAGEQUEUETABLE
                WHERE STATUS IN (@HELD, @RETRY)
                GROUP BY ENVIRONMENT, SOURCESYSTEM, SOURCEENTITY
            )
            SELECT r.ENVIRONMENT, r.SOURCESYSTEM, r.SOURCEENTITY, r.ORCHESTRATIONTYPE,
                   r.ENABLED, r.ONPAUSE, r.MODIFIEDBY, r.MODIFIEDDATETIME,
                   ISNULL(b.HELDCOUNT, 0) AS HELDCOUNT,
                   ISNULL(b.RETRYCOUNT, 0) AS RETRYCOUNT,
                   b.OLDESTHELD
            FROM ROUTETABLE AS r
            LEFT JOIN backlog AS b
                ON  b.ENVIRONMENT  = r.ENVIRONMENT
                AND b.SOURCESYSTEM = r.SOURCESYSTEM
                AND b.SOURCEENTITY = r.SOURCEENTITY
            ORDER BY r.ENVIRONMENT, r.SOURCESYSTEM, r.SOURCEENTITY
            """;

        var routes = new List<RouteRecord>();

        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@HELD", MessageStatus.Held);
        cmd.Parameters.AddWithValue("@RETRY", MessageStatus.RetryScheduled);

        await conn.OpenAsync();
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            routes.Add(new RouteRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetBoolean(4),
                reader.GetString(5),
                reader["MODIFIEDBY"] as string,
                reader["MODIFIEDDATETIME"] as DateTime?,
                Convert.ToInt32(reader["HELDCOUNT"]),
                Convert.ToInt32(reader["RETRYCOUNT"]),
                reader["OLDESTHELD"] as DateTime?));
        }

        return routes;
    }

    /// <summary>
    /// Backlog sitting on a route that no longer exists. Both sweeps inner join ROUTETABLE, so
    /// these messages are never claimed by anything and would otherwise wait indefinitely.
    /// </summary>
    public async Task<List<OrphanedBacklog>> GetOrphanedBacklogAsync()
    {
        const string sql = """
            SELECT m.ENVIRONMENT, m.SOURCESYSTEM, m.SOURCEENTITY,
                   COUNT(*) AS STUCKCOUNT, MIN(m.CREATEDDATETIME) AS OLDEST
            FROM MESSAGEQUEUETABLE AS m
            WHERE m.STATUS IN (@HELD, @RETRY)
              AND NOT EXISTS (
                  SELECT 1 FROM ROUTETABLE AS r
                  WHERE r.ENVIRONMENT  = m.ENVIRONMENT
                    AND r.SOURCESYSTEM = m.SOURCESYSTEM
                    AND r.SOURCEENTITY = m.SOURCEENTITY)
            GROUP BY m.ENVIRONMENT, m.SOURCESYSTEM, m.SOURCEENTITY
            ORDER BY MIN(m.CREATEDDATETIME)
            """;

        var orphans = new List<OrphanedBacklog>();

        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@HELD", MessageStatus.Held);
        cmd.Parameters.AddWithValue("@RETRY", MessageStatus.RetryScheduled);

        await conn.OpenAsync();
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            orphans.Add(new OrphanedBacklog(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                Convert.ToInt32(reader["STUCKCOUNT"]),
                reader["OLDEST"] as DateTime?));
        }

        return orphans;
    }

    /// <returns>False when no such route exists.</returns>
    public async Task<bool> SetEnabledAsync(RouteKey key, bool enabled, string? modifiedBy)
    {
        const string sql = """
            UPDATE ROUTETABLE
            SET ENABLED = @ENABLED,
                MODIFIEDBY = @MODIFIEDBY,
                MODIFIEDDATETIME = SYSUTCDATETIME()
            WHERE ENVIRONMENT = @ENVIRONMENT
              AND SOURCESYSTEM = @SOURCESYSTEM
              AND SOURCEENTITY = @SOURCEENTITY
            """;

        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@ENABLED", enabled);
        cmd.Parameters.AddWithValue("@MODIFIEDBY", (object?)modifiedBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ENVIRONMENT", key.Environment);
        cmd.Parameters.AddWithValue("@SOURCESYSTEM", key.SourceSystem);
        cmd.Parameters.AddWithValue("@SOURCEENTITY", key.SourceEntity);

        await conn.OpenAsync();
        var affected = await cmd.ExecuteNonQueryAsync();

        if (affected == 0)
        {
            return false;
        }

        // Without this the change would not take effect until the resolver's cache expired, and an
        // operator pausing a route would watch it keep processing for another minute.
        resolver.Invalidate();
        return true;
    }
}
