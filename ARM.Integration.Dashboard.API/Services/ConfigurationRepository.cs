using System.Text.Json;
using ARM.Integration.Dashboard.API.Models;
using Microsoft.Data.SqlClient;

namespace ARM.Integration.Dashboard.API.Services;

/// <summary>
/// Data access for the generic configuration store (<c>CONFIGURATIONTABLE</c>).
/// Fields are stored as a JSON object in the <c>VALUE</c> column.
/// </summary>
public class ConfigurationRepository(IConfiguration config)
{
    private readonly string _connectionString = config.GetConnectionString("EIMDB")
        ?? throw new InvalidOperationException("EIMDB connection string is missing.");

    public async Task<List<ConfigurationRecord>> GetByCategoryAsync(string category, bool activeOnly = false)
    {
        var sql =
            "SELECT CATEGORY, NAME, DISPLAYNAME, VALUE, ISACTIVE FROM CONFIGURATIONTABLE " +
            "WHERE CATEGORY = @CATEGORY" + (activeOnly ? " AND ISACTIVE = 1" : "") +
            " ORDER BY NAME";

        var records = new List<ConfigurationRecord>();
        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@CATEGORY", category);

        await conn.OpenAsync();
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
            records.Add(MapRecord(reader));

        return records;
    }

    public async Task<ConfigurationRecord?> GetByKeyAsync(string category, string name)
    {
        const string sql = """
            SELECT CATEGORY, NAME, DISPLAYNAME, VALUE, ISACTIVE
            FROM CONFIGURATIONTABLE
            WHERE CATEGORY = @CATEGORY AND NAME = @NAME
            """;

        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@CATEGORY", category);
        cmd.Parameters.AddWithValue("@NAME", name);

        await conn.OpenAsync();
        await using var reader = await cmd.ExecuteReaderAsync();

        return await reader.ReadAsync() ? MapRecord(reader) : null;
    }

    public async Task<bool> CreateAsync(ConfigurationRecord record)
    {
        const string sql = """
            INSERT INTO CONFIGURATIONTABLE (CATEGORY, NAME, DISPLAYNAME, VALUE, ISACTIVE)
            VALUES (@CATEGORY, @NAME, @DISPLAYNAME, @VALUE, @ISACTIVE)
            """;

        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@CATEGORY", record.Category);
        cmd.Parameters.AddWithValue("@NAME", record.Name);
        cmd.Parameters.AddWithValue("@DISPLAYNAME", (object?)record.DisplayName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@VALUE", JsonSerializer.Serialize(record.Fields));
        cmd.Parameters.AddWithValue("@ISACTIVE", record.IsActive);

        await conn.OpenAsync();
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> UpdateAsync(ConfigurationRecord record)
    {
        const string sql = """
            UPDATE CONFIGURATIONTABLE
            SET DISPLAYNAME = @DISPLAYNAME, VALUE = @VALUE, ISACTIVE = @ISACTIVE
            WHERE CATEGORY = @CATEGORY AND NAME = @NAME
            """;

        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@CATEGORY", record.Category);
        cmd.Parameters.AddWithValue("@NAME", record.Name);
        cmd.Parameters.AddWithValue("@DISPLAYNAME", (object?)record.DisplayName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@VALUE", JsonSerializer.Serialize(record.Fields));
        cmd.Parameters.AddWithValue("@ISACTIVE", record.IsActive);

        await conn.OpenAsync();
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> DeleteAsync(string category, string name)
    {
        const string sql = "DELETE FROM CONFIGURATIONTABLE WHERE CATEGORY = @CATEGORY AND NAME = @NAME";

        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@CATEGORY", category);
        cmd.Parameters.AddWithValue("@NAME", name);

        await conn.OpenAsync();
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    private static ConfigurationRecord MapRecord(SqlDataReader reader)
    {
        var value = reader["VALUE"] as string;
        Dictionary<string, string> fields;
        try
        {
            fields = string.IsNullOrWhiteSpace(value)
                ? []
                : JsonSerializer.Deserialize<Dictionary<string, string>>(value) ?? [];
        }
        catch (JsonException)
        {
            fields = [];
        }

        return new ConfigurationRecord
        {
            Category = reader["CATEGORY"] as string ?? "",
            Name = reader["NAME"] as string ?? "",
            DisplayName = reader["DISPLAYNAME"] as string,
            Fields = fields,
            IsActive = reader["ISACTIVE"] is bool b && b,
        };
    }
}
