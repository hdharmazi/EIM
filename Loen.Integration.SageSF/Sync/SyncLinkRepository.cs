using Microsoft.Data.SqlClient;

namespace Loen.Integration.SageSF.Sync;

/// <summary>
/// CRUD operations on the sync_link table — one row per synced record
/// tracking both-side hashes, modification times, and sync state.
/// </summary>
public sealed class SyncLinkRepository
{
    private readonly string _connectionString;

    public SyncLinkRepository(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("EIMDB")
            ?? throw new InvalidOperationException("ConnectionStrings:EIMDB is not configured.");
    }

    public async Task<SyncLink?> GetAsync(string entity, string sageKey, CancellationToken ct = default)
    {
        const string sql = """
            SELECT entity, sage_key, sf_id, sage_hash, sf_hash,
                   sage_modified, sf_modified, last_synced_at, last_direction, state
            FROM sync_link
            WHERE entity = @Entity AND sage_key = @SageKey
            """;

        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Entity", entity);
        cmd.Parameters.AddWithValue("@SageKey", sageKey);

        await conn.OpenAsync(ct);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct)) return null;

        return new SyncLink
        {
            Entity = reader.GetString(0),
            SageKey = reader.GetString(1),
            SfId = reader["sf_id"] as string,
            SageHash = reader["sage_hash"] as string,
            SfHash = reader["sf_hash"] as string,
            SageModified = reader["sage_modified"] as DateTime?,
            SfModified = reader["sf_modified"] as DateTime?,
            LastSyncedAt = reader["last_synced_at"] as DateTime?,
            LastDirection = reader["last_direction"] as string,
            State = reader.GetString(9),
        };
    }

    public async Task UpsertAsync(SyncLink link, CancellationToken ct = default)
    {
        const string sql = """
            MERGE sync_link AS target
            USING (SELECT @Entity AS entity, @SageKey AS sage_key) AS source
            ON target.entity = source.entity AND target.sage_key = source.sage_key
            WHEN MATCHED THEN UPDATE SET
                sf_id = @SfId, sage_hash = @SageHash, sf_hash = @SfHash,
                sage_modified = @SageModified, sf_modified = @SfModified,
                last_synced_at = @LastSyncedAt, last_direction = @LastDirection, state = @State
            WHEN NOT MATCHED THEN INSERT
                (entity, sage_key, sf_id, sage_hash, sf_hash,
                 sage_modified, sf_modified, last_synced_at, last_direction, state)
            VALUES
                (@Entity, @SageKey, @SfId, @SageHash, @SfHash,
                 @SageModified, @SfModified, @LastSyncedAt, @LastDirection, @State);
            """;

        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Entity", link.Entity);
        cmd.Parameters.AddWithValue("@SageKey", link.SageKey);
        cmd.Parameters.AddWithValue("@SfId", (object?)link.SfId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@SageHash", (object?)link.SageHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@SfHash", (object?)link.SfHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@SageModified", (object?)link.SageModified ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@SfModified", (object?)link.SfModified ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@LastSyncedAt", (object?)link.LastSyncedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@LastDirection", (object?)link.LastDirection ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@State", link.State);

        await conn.OpenAsync(ct);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

public sealed class SyncLink
{
    public required string Entity { get; init; }
    public required string SageKey { get; init; }
    public string? SfId { get; set; }
    public string? SageHash { get; set; }
    public string? SfHash { get; set; }
    public DateTime? SageModified { get; set; }
    public DateTime? SfModified { get; set; }
    public DateTime? LastSyncedAt { get; set; }
    public string? LastDirection { get; set; }
    public required string State { get; init; }
}
