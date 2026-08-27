using Microsoft.Data.SqlClient;

namespace ARM.Integration.EIM.Routing;

public sealed class SqlDuplicateProbe(string connectionString) : IDuplicateProbe
{
    private readonly string _connectionString = !string.IsNullOrWhiteSpace(connectionString)
        ? connectionString
        : throw new ArgumentException("A connection string is required.", nameof(connectionString));

    public async Task<ExistingMessage?> FindAsync(
        RouteKey key, string dedupeKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dedupeKey))
        {
            // Nothing stable to key on, so nothing can be a duplicate.
            return null;
        }

        // Column order matches IX_MESSAGEQUEUETABLE_DEDUPEKEY. Newest first, because it is the
        // newest row's status that decides between duplicate and re-drive.
        const string sql = """
            SELECT TOP 1 ID, STATUS
            FROM dbo.MESSAGEQUEUETABLE
            WHERE SOURCESYSTEM = @sourceSystem
              AND ENVIRONMENT = @environment
              AND DEDUPEKEY = @dedupeKey
            ORDER BY ID DESC
            """;

        await using var connection = new SqlConnection(_connectionString);
        await using var command = new SqlCommand(sql, connection);

        command.Parameters.AddWithValue("@sourceSystem", key.SourceSystem);
        command.Parameters.AddWithValue("@environment", key.Environment);
        command.Parameters.AddWithValue("@dedupeKey", dedupeKey);

        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ExistingMessage(
            reader.GetInt64(0).ToString(),
            reader.IsDBNull(1) ? MessageStatus.New : reader.GetInt32(1));
    }
}
