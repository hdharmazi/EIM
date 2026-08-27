using Microsoft.Data.SqlClient;

namespace ARM.Integration.EIM.Routing;

public sealed class SqlRouteBacklogProbe(string connectionString) : IRouteBacklogProbe
{
    private readonly string _connectionString = !string.IsNullOrWhiteSpace(connectionString)
        ? connectionString
        : throw new ArgumentException("A connection string is required.", nameof(connectionString));

    public async Task<bool> HasBacklogAsync(RouteKey key, CancellationToken cancellationToken = default)
    {
        // Column order matches IX_MESSAGEQUEUETABLE_STATUS, which exists for this predicate and the
        // sweeps. TOP 1 because the count is irrelevant - only whether any exist.
        const string sql = """
            SELECT TOP 1 1
            FROM dbo.MESSAGEQUEUETABLE
            WHERE STATUS IN (@held, @retryScheduled)
              AND ENVIRONMENT = @environment
              AND SOURCESYSTEM = @sourceSystem
              AND SOURCEENTITY = @sourceEntity
            """;

        await using var connection = new SqlConnection(_connectionString);
        await using var command = new SqlCommand(sql, connection);

        command.Parameters.AddWithValue("@held", MessageStatus.Held);
        command.Parameters.AddWithValue("@retryScheduled", MessageStatus.RetryScheduled);
        command.Parameters.AddWithValue("@environment", key.Environment);
        command.Parameters.AddWithValue("@sourceSystem", key.SourceSystem);
        command.Parameters.AddWithValue("@sourceEntity", key.SourceEntity);

        await connection.OpenAsync(cancellationToken);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }
}
