using Wilson.Integration.Orchestrations;
using ARM.Integration.Dashboard.API.Models;
using ARM.Integration.EIM;
using ARM.Integration.EIM.Orchestration;
using Microsoft.Data.SqlClient;
using System.Diagnostics;

namespace ARM.Integration.Dashboard.API.Services;

/// <remarks>
/// Cleanup batch size and time budget are constructor parameters so tests can force multi-batch and
/// budget-expiry behaviour without seeding tens of thousands of rows. Production uses the defaults.
/// </remarks>
public class MessageQueueRepository(
    IConfiguration config,
    ILoggerFactory loggerFactory,
    int cleanupBatchSize = 2000,
    int cleanupBudgetSeconds = 60)
{
    private readonly string _connectionString = config.GetConnectionString("EIMDB")
        ?? throw new InvalidOperationException("EIMDB connection string is missing.");

    public async Task<List<MessageQueueRecord>> GetAllAsync(int? statusFilter = null, string? search = null, int top = 500)
    {
        var conditions = new List<string>();
        if (statusFilter.HasValue) conditions.Add("STATUS = @STATUS");
        if (!string.IsNullOrWhiteSpace(search)) conditions.Add("(SOURCEID LIKE @SEARCH OR TYPE LIKE @SEARCH OR SOURCESYSTEM LIKE @SEARCH OR DESTINATIONSYSTEM LIKE @SEARCH)");

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : string.Empty;

        var sql = $"""
            SELECT TOP {top}
                ID, SOURCEID, SOURCESYSTEM, SOURCEENTITY,
                DESTINATIONSYSTEM, DESTINATIONENTITY, TYPE, ENVIRONMENT,
                QUEUE, RESPONSEQUEUE, PAYLOAD, MESSAGELOG, STATUS,
                CREATEDDATETIME, MODIFIEDDATETIME
            FROM MESSAGEQUEUETABLE
            {where}
            ORDER BY ID DESC
            """;

        var records = new List<MessageQueueRecord>();
        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);

        if (statusFilter.HasValue)
            cmd.Parameters.AddWithValue("@STATUS", statusFilter.Value);
        if (!string.IsNullOrWhiteSpace(search))
            cmd.Parameters.AddWithValue("@SEARCH", $"%{search}%");

        await conn.OpenAsync();
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
            records.Add(MapRecord(reader));

        return records;
    }

    public async Task<Dictionary<int, int>> GetStatusCountsAsync()
    {
        const string sql = "SELECT STATUS, COUNT(*) AS CNT FROM MESSAGEQUEUETABLE GROUP BY STATUS";
        var counts = new Dictionary<int, int>();

        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);
        await conn.OpenAsync();
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
            counts[reader.GetInt32(0)] = reader.GetInt32(1);

        return counts;
    }

    public async Task<List<MessageQueueRecord>> GetRecentFailedAsync(int top = 10)
    {
        var sql = $"""
            SELECT TOP {top}
                ID, SOURCEID, SOURCESYSTEM, SOURCEENTITY,
                DESTINATIONSYSTEM, DESTINATIONENTITY, TYPE, ENVIRONMENT,
                QUEUE, RESPONSEQUEUE, PAYLOAD, MESSAGELOG, STATUS,
                CREATEDDATETIME, MODIFIEDDATETIME
            FROM MESSAGEQUEUETABLE
            WHERE STATUS IN ({MessageStatus.Failed}, {MessageStatus.DeadLetter})
            ORDER BY ID DESC
            """;

        var records = new List<MessageQueueRecord>();
        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);
        await conn.OpenAsync();
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
            records.Add(MapRecord(reader));

        return records;
    }

    public async Task<MessageQueueRecord?> GetByIdAsync(long id)
    {
        const string sql = """
            SELECT
                ID, SOURCEID, SOURCESYSTEM, SOURCEENTITY,
                DESTINATIONSYSTEM, DESTINATIONENTITY, TYPE, ENVIRONMENT,
                QUEUE, RESPONSEQUEUE, PAYLOAD, MESSAGELOG, STATUS,
                CREATEDDATETIME, MODIFIEDDATETIME, RETRYCOUNT, NEXTATTEMPTDATETIME
            FROM MESSAGEQUEUETABLE
            WHERE ID = @ID
            """;

        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@ID", id);

        await conn.OpenAsync();
        await using var reader = await cmd.ExecuteReaderAsync();

        return await reader.ReadAsync() ? MapRecord(reader) : null;
    }

    private static MessageQueueRecord MapRecord(SqlDataReader reader)
    {
        return new MessageQueueRecord
        {
            Id                = reader.GetInt64(reader.GetOrdinal("ID")),
            SourceId          = reader["SOURCEID"]?.ToString() ?? string.Empty,
            SourceSystem      = reader["SOURCESYSTEM"]?.ToString() ?? string.Empty,
            SourceEntity      = reader["SOURCEENTITY"]?.ToString() ?? string.Empty,
            DestinationSystem = reader["DESTINATIONSYSTEM"]?.ToString() ?? string.Empty,
            DestinationEntity = reader["DESTINATIONENTITY"]?.ToString() ?? string.Empty,
            Type              = reader["TYPE"]?.ToString() ?? string.Empty,
            Environment       = reader["ENVIRONMENT"]?.ToString() ?? string.Empty,
            Queue             = reader["QUEUE"]?.ToString() ?? string.Empty,
            ResponseQueue     = reader["RESPONSEQUEUE"]?.ToString() ?? string.Empty,
            Payload           = reader["PAYLOAD"]?.ToString() ?? string.Empty,
            MessageLog        = reader["MESSAGELOG"]?.ToString() ?? string.Empty,
            Status            = Convert.ToInt32(reader["STATUS"]),
            CreatedAt         = reader["CREATEDDATETIME"] as DateTime?,
            ModifiedDateTime  = reader["MODIFIEDDATETIME"] as DateTime?,
            // Absent from the list queries, which do not select them.
            RetryCount        = HasColumn(reader, "RETRYCOUNT") ? Convert.ToInt32(reader["RETRYCOUNT"]) : 0,
            NextAttemptDateTime = HasColumn(reader, "NEXTATTEMPTDATETIME")
                ? reader["NEXTATTEMPTDATETIME"] as DateTime?
                : null
        };
    }

    private static bool HasColumn(SqlDataReader reader, string name)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (string.Equals(reader.GetName(i), name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public async Task<string> ReprocessAsync(long id)
    {
        var logger = loggerFactory.CreateLogger<EIMController>();
        var controller = new EIMController(_connectionString, logger, OrchestrationRegistry.CreateDefault());

        return await controller.ProcessMessageAsync(id.ToString());
    }

    public async Task UpdatePayloadAsync(long id, string payload)
    {
        const string sql = "UPDATE MESSAGEQUEUETABLE SET PAYLOAD = @PAYLOAD WHERE ID = @ID";

        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@ID", id);
        cmd.Parameters.AddWithValue("@PAYLOAD", payload);

        await conn.OpenAsync();
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<MessageLogDetailRecord>> GetLogDetailsAsync(long messageId)
    {
        const string sql = """
            SELECT ID, MESSAGEID, NAME, DETAIL, CREATEDDATETIME
            FROM MESSAGELOGDETAIL
            WHERE MESSAGEID = @MESSAGEID
            ORDER BY ID ASC
            """;

        var records = new List<MessageLogDetailRecord>();
        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@MESSAGEID", messageId);

        await conn.OpenAsync();
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            records.Add(new MessageLogDetailRecord
            {
                Id              = reader.GetInt64(reader.GetOrdinal("ID")),
                MessageId       = reader.GetInt64(reader.GetOrdinal("MESSAGEID")),
                Name            = reader["NAME"]?.ToString() ?? string.Empty,
                Detail          = reader["DETAIL"]?.ToString(),
                CreatedDateTime = reader.GetDateTime(reader.GetOrdinal("CREATEDDATETIME"))
            });
        }

        return records;
    }

    /// <summary>Bounded per call: what this call deleted, and what is still eligible.</summary>
    public sealed record CleanupResult(int Deleted, int Remaining);

    private const int CleanupCommandTimeoutSeconds = 120;

    /// <summary>
    /// Deletes eligible records in bounded batches, each committed independently, until none remain
    /// or the time budget elapses.
    /// </summary>
    /// <remarks>
    /// One unbounded transaction cannot clear a large backlog inside any command timeout, and
    /// rolling back on expiry meant nothing was ever deleted however often it was retried.
    /// Committed batches are the progress, so a caller repeats until <c>Remaining</c> is zero.
    /// <paramref name="statuses"/> already encodes any operator-selected status, so age is the only
    /// other condition and the two combine with AND.
    /// </remarks>
    public async Task<CleanupResult> CleanupAsync(IReadOnlyList<int> statuses, int? olderThanDays)
    {
        if (statuses.Count == 0)
            return new CleanupResult(0, 0);

        var statusParams = statuses.Select((_, i) => $"@S{i}").ToArray();
        var where = $"STATUS IN ({string.Join(", ", statusParams)})"
                  + (olderThanDays.HasValue ? " AND CREATEDDATETIME < @CUTOFF" : string.Empty);

        // Evaluated once so every batch shares a cutoff; a per-batch UtcNow would let rows created
        // mid-run drift into scope.
        var cutoff = olderThanDays.HasValue ? DateTime.UtcNow.AddDays(-olderThanDays.Value) : (DateTime?)null;

        // Children are deleted with their parents inside one batch. There is no foreign key, so
        // splitting these into separate passes would orphan log details on any partial run.
        var batchSql = $"""
            DECLARE @Ids TABLE (ID BIGINT);

            INSERT INTO @Ids (ID)
            SELECT TOP (@BATCH) ID
            FROM dbo.MESSAGEQUEUETABLE
            WHERE {where}
            ORDER BY ID;

            DELETE FROM dbo.MESSAGELOGDETAIL  WHERE MESSAGEID IN (SELECT ID FROM @Ids);
            DELETE FROM dbo.MESSAGEQUEUETABLE WHERE ID        IN (SELECT ID FROM @Ids);

            SELECT COUNT(*) FROM @Ids;
            """;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        var deleted = 0;
        var elapsed = Stopwatch.StartNew();
        var budget = TimeSpan.FromSeconds(cleanupBudgetSeconds);

        while (elapsed.Elapsed < budget)
        {
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
            int claimed;

            try
            {
                await using var cmd = new SqlCommand(batchSql, conn, tx)
                {
                    CommandTimeout = CleanupCommandTimeoutSeconds
                };
                AddFilterParameters(cmd, statuses, cutoff);
                cmd.Parameters.AddWithValue("@BATCH", cleanupBatchSize);

                claimed = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }

            if (claimed == 0) break;
            deleted += claimed;
        }

        await using var remainingCmd = new SqlCommand(
            $"SELECT COUNT(*) FROM dbo.MESSAGEQUEUETABLE WHERE {where}", conn)
        {
            CommandTimeout = CleanupCommandTimeoutSeconds
        };
        AddFilterParameters(remainingCmd, statuses, cutoff);

        return new CleanupResult(deleted, Convert.ToInt32(await remainingCmd.ExecuteScalarAsync()));
    }

    private static void AddFilterParameters(SqlCommand cmd, IReadOnlyList<int> statuses, DateTime? cutoff)
    {
        for (var i = 0; i < statuses.Count; i++)
            cmd.Parameters.AddWithValue($"@S{i}", statuses[i]);

        if (cutoff.HasValue)
            cmd.Parameters.AddWithValue("@CUTOFF", cutoff.Value);
    }
}
