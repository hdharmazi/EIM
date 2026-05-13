using ARM.Integration.Dashboard.API.Models;
using Microsoft.Data.SqlClient;

namespace ARM.Integration.Dashboard.API.Services;

public class EnvironmentRepository(IConfiguration config)
{
    private readonly string _connectionString = config.GetConnectionString("EIMDB")
        ?? throw new InvalidOperationException("EIMDB connection string is missing.");

    public async Task<List<EnvironmentRecord>> GetAllAsync(string? system = null, string? environment = null)
    {
        var conditions = new List<string>();
        if (!string.IsNullOrWhiteSpace(system)) conditions.Add("SYSTEM = @SYSTEM");
        if (!string.IsNullOrWhiteSpace(environment)) conditions.Add("ENVIRONMENT = @ENVIRONMENT");

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : string.Empty;
        var sql = $"SELECT SYSTEM, ENVIRONMENT, VALUE FROM ENVIRONMENTTABLE {where} ORDER BY SYSTEM, ENVIRONMENT";

        var records = new List<EnvironmentRecord>();
        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);

        if (!string.IsNullOrWhiteSpace(system))
            cmd.Parameters.AddWithValue("@SYSTEM", system);
        if (!string.IsNullOrWhiteSpace(environment))
            cmd.Parameters.AddWithValue("@ENVIRONMENT", environment);

        await conn.OpenAsync();
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
            records.Add(MapRecord(reader));

        return records;
    }

    public async Task<EnvironmentRecord?> GetByKeyAsync(string system, string environment)
    {
        const string sql = """
            SELECT SYSTEM, ENVIRONMENT, VALUE
            FROM ENVIRONMENTTABLE
            WHERE SYSTEM = @SYSTEM AND ENVIRONMENT = @ENVIRONMENT
            """;

        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@SYSTEM", system);
        cmd.Parameters.AddWithValue("@ENVIRONMENT", environment);

        await conn.OpenAsync();
        await using var reader = await cmd.ExecuteReaderAsync();

        return await reader.ReadAsync() ? MapRecord(reader) : null;
    }

    public async Task<bool> CreateAsync(EnvironmentRecord record)
    {
        const string sql = """
            INSERT INTO ENVIRONMENTTABLE (SYSTEM, ENVIRONMENT, VALUE)
            VALUES (@SYSTEM, @ENVIRONMENT, @VALUE)
            """;

        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@SYSTEM", record.System);
        cmd.Parameters.AddWithValue("@ENVIRONMENT", record.Environment);
        cmd.Parameters.AddWithValue("@VALUE", record.Value);

        await conn.OpenAsync();
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> UpdateAsync(string system, string environment, string value)
    {
        const string sql = """
            UPDATE ENVIRONMENTTABLE
            SET VALUE = @VALUE
            WHERE SYSTEM = @SYSTEM AND ENVIRONMENT = @ENVIRONMENT
            """;

        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@SYSTEM", system);
        cmd.Parameters.AddWithValue("@ENVIRONMENT", environment);
        cmd.Parameters.AddWithValue("@VALUE", value);

        await conn.OpenAsync();
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> DeleteAsync(string system, string environment)
    {
        const string sql = "DELETE FROM ENVIRONMENTTABLE WHERE SYSTEM = @SYSTEM AND ENVIRONMENT = @ENVIRONMENT";

        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@SYSTEM", system);
        cmd.Parameters.AddWithValue("@ENVIRONMENT", environment);

        await conn.OpenAsync();
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    private static EnvironmentRecord MapRecord(SqlDataReader reader) => new()
    {
        System      = reader["SYSTEM"]?.ToString()      ?? string.Empty,
        Environment = reader["ENVIRONMENT"]?.ToString() ?? string.Empty,
        Value       = reader["VALUE"]?.ToString()       ?? string.Empty
    };
}
