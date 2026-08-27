using Microsoft.Data.SqlClient;

namespace ARM.Integration.EIM.Routing;

public sealed class SqlReleaseSweepStore(string connectionString) : IReleaseSweepStore
{
    private readonly string _connectionString = !string.IsNullOrWhiteSpace(connectionString)
        ? connectionString
        : throw new ArgumentException("A connection string is required.", nameof(connectionString));

    public async Task<IReadOnlyList<RouteKey>> GetDrainableRoutesAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT DISTINCT m.ENVIRONMENT, m.SOURCESYSTEM, m.SOURCEENTITY
            FROM dbo.MESSAGEQUEUETABLE AS m
            INNER JOIN dbo.ROUTETABLE AS r
                ON  r.ENVIRONMENT  = m.ENVIRONMENT
                AND r.SOURCESYSTEM = m.SOURCESYSTEM
                AND r.SOURCEENTITY = m.SOURCEENTITY
            WHERE m.STATUS = @HELD
              AND r.ENABLED = 1
            """;

        var routes = new List<RouteKey>();

        await using var connection = new SqlConnection(_connectionString);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@HELD", MessageStatus.Held);

        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            routes.Add(new RouteKey(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return routes;
    }

    public async Task<IReadOnlyList<ClaimedMessage>> ClaimHeldAsync(
        RouteKey key, int batchSize, string claimLog, CancellationToken cancellationToken = default)
    {
        if (batchSize < 1)
            throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "Batch size must be at least 1.");

        // Ascending ID is the whole point: held messages must reach the destination in the order
        // they were captured. UPDLOCK plus READPAST make two sweeps skip each other's rows rather
        // than claim the same message or block. The results go through a table variable because
        // the MODIFIEDDATETIME trigger forbids OUTPUT without INTO.
        //
        // The retry barrier stops the drain at the oldest message awaiting retry on this route.
        // Without it a held message is applied a minute after arrival while an older message for
        // the same record is still waiting to retry, and the older one then lands on top of it.
        // Draining up to the barrier keeps ordering without stalling the whole route.
        const string sql = """
            DECLARE @Ids TABLE (ID BIGINT);

            DECLARE @Barrier BIGINT = (
                SELECT MIN(r.ID)
                FROM dbo.MESSAGEQUEUETABLE AS r
                WHERE r.STATUS = @RETRYSCHEDULED
                  AND r.ENVIRONMENT  = @ENVIRONMENT
                  AND r.SOURCESYSTEM = @SOURCESYSTEM
                  AND r.SOURCEENTITY = @SOURCEENTITY
            );

            WITH held AS (
                SELECT TOP (@BATCH) m.ID, m.STATUS, m.MESSAGELOG
                FROM dbo.MESSAGEQUEUETABLE AS m WITH (UPDLOCK, READPAST)
                WHERE m.STATUS = @HELD
                  AND m.ENVIRONMENT  = @ENVIRONMENT
                  AND m.SOURCESYSTEM = @SOURCESYSTEM
                  AND m.SOURCEENTITY = @SOURCEENTITY
                  AND (@Barrier IS NULL OR m.ID < @Barrier)
                ORDER BY m.ID
            )
            UPDATE held
            SET STATUS = @PROCESSING, MESSAGELOG = @LOG
            OUTPUT inserted.ID INTO @Ids;

            SELECT m.ID, m.SOURCEID
            FROM @Ids AS i
            INNER JOIN dbo.MESSAGEQUEUETABLE AS m ON m.ID = i.ID
            ORDER BY m.ID;
            """;

        var claimed = new List<ClaimedMessage>();

        await using var connection = new SqlConnection(_connectionString);
        await using var command = new SqlCommand(sql, connection);

        command.Parameters.AddWithValue("@BATCH", batchSize);
        command.Parameters.AddWithValue("@HELD", MessageStatus.Held);
        command.Parameters.AddWithValue("@RETRYSCHEDULED", MessageStatus.RetryScheduled);
        command.Parameters.AddWithValue("@PROCESSING", MessageStatus.Processing);
        command.Parameters.AddWithValue("@ENVIRONMENT", key.Environment);
        command.Parameters.AddWithValue("@SOURCESYSTEM", key.SourceSystem);
        command.Parameters.AddWithValue("@SOURCEENTITY", key.SourceEntity);
        command.Parameters.AddWithValue("@LOG", claimLog);

        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            claimed.Add(new ClaimedMessage(
                reader.GetInt64(0).ToString(),
                reader.IsDBNull(1) ? "" : reader.GetString(1)));
        }

        return claimed;
    }
}
