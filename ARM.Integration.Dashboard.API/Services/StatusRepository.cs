using Microsoft.Data.SqlClient;

namespace ARM.Integration.Dashboard.API.Services;

public sealed record StatusRecord(int Status, string Label);

public class StatusRepository(IConfiguration configuration)
{
    private readonly string _connectionString = configuration.GetConnectionString("EIMDB")
        ?? throw new InvalidOperationException("ConnectionStrings:EIMDB is not configured.");

    public async Task<List<StatusRecord>> GetAllAsync()
    {
        const string sql = "SELECT STATUS, STATUSVALUE FROM STATUSTABLE ORDER BY STATUS";

        var statuses = new List<StatusRecord>();

        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);

        await conn.OpenAsync();
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            // Trimmed: the column is NVARCHAR now, but a client seeded from the older NCHAR
            // definition would return padded labels.
            statuses.Add(new StatusRecord(reader.GetInt32(0), reader.GetString(1).Trim()));
        }

        return statuses;
    }
}
