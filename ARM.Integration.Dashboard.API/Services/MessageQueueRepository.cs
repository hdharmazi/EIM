using ARM.Integration.Dashboard.API.Models;
using ARM.Integration.EIM;
using ARM.Integration.EIM.Orchestration;
using Microsoft.Data.SqlClient;

namespace ARM.Integration.Dashboard.API.Services;

public class MessageQueueRepository(IConfiguration config, ILoggerFactory loggerFactory)
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
            WHERE STATUS IN (3, 4)
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
                CREATEDDATETIME, MODIFIEDDATETIME
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
            ModifiedDateTime  = reader["MODIFIEDDATETIME"] as DateTime?
        };
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

    public async Task<int> CleanupAsync(int? status, int? olderThanDays)
    {
        var conditions = new List<string>();

        if (status.HasValue)
            conditions.Add("STATUS = @STATUS");
        if (olderThanDays.HasValue)
            conditions.Add("CREATEDDATETIME < @CUTOFF");

        if (conditions.Count == 0)
            throw new InvalidOperationException("At least one filter (status or olderThanDays) is required.");

        var where = string.Join(" AND ", conditions);

        // Delete log details first (child records)
        var deleteLogsSql = $"DELETE FROM MESSAGELOGDETAIL WHERE MESSAGEID IN (SELECT ID FROM MESSAGEQUEUETABLE WHERE {where})";
        var deleteMessagesSql = $"DELETE FROM MESSAGEQUEUETABLE WHERE {where}";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var tx = conn.BeginTransaction();

        try
        {
            await using (var cmd = new SqlCommand(deleteLogsSql, conn, tx))
            {
                if (status.HasValue) cmd.Parameters.AddWithValue("@STATUS", status.Value);
                if (olderThanDays.HasValue) cmd.Parameters.AddWithValue("@CUTOFF", DateTime.UtcNow.AddDays(-olderThanDays.Value));
                await cmd.ExecuteNonQueryAsync();
            }

            int deleted;
            await using (var cmd = new SqlCommand(deleteMessagesSql, conn, tx))
            {
                if (status.HasValue) cmd.Parameters.AddWithValue("@STATUS", status.Value);
                if (olderThanDays.HasValue) cmd.Parameters.AddWithValue("@CUTOFF", DateTime.UtcNow.AddDays(-olderThanDays.Value));
                deleted = await cmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            return deleted;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }
}
