using ARM.Integration.Dashboard.API.Models;
using Microsoft.Data.SqlClient;

namespace ARM.Integration.Dashboard.API.Services;

public class UserRepository(IConfiguration config)
{
    private readonly string _connectionString = config.GetConnectionString("EIMDB")
        ?? throw new InvalidOperationException("EIMDB connection string is missing.");

    public async Task<List<AppUser>> GetAllAsync()
    {
        const string sql = "SELECT EMAIL, DISPLAYNAME, ROLE, ISACTIVE, CREATEDDATETIME FROM USERTABLE ORDER BY EMAIL";
        var users = new List<AppUser>();
        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);
        await conn.OpenAsync();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            users.Add(MapUser(reader));
        }
        return users;
    }

    public async Task<AppUser?> GetByEmailAsync(string email)
    {
        const string sql = "SELECT EMAIL, DISPLAYNAME, ROLE, ISACTIVE, CREATEDDATETIME FROM USERTABLE WHERE EMAIL = @EMAIL";
        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@EMAIL", email);
        await conn.OpenAsync();
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapUser(reader) : null;
    }

    public async Task CreateAsync(AppUser user)
    {
        const string sql = "INSERT INTO USERTABLE (EMAIL, DISPLAYNAME, ROLE, ISACTIVE) VALUES (@EMAIL, @DISPLAYNAME, @ROLE, @ISACTIVE)";
        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@EMAIL", user.Email);
        cmd.Parameters.AddWithValue("@DISPLAYNAME", (object?)user.DisplayName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ROLE", user.Role);
        cmd.Parameters.AddWithValue("@ISACTIVE", user.IsActive);
        await conn.OpenAsync();
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<bool> UpdateAsync(string email, AppUser user)
    {
        const string sql = "UPDATE USERTABLE SET DISPLAYNAME = @DISPLAYNAME, ROLE = @ROLE, ISACTIVE = @ISACTIVE WHERE EMAIL = @EMAIL";
        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@EMAIL", email);
        cmd.Parameters.AddWithValue("@DISPLAYNAME", (object?)user.DisplayName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ROLE", user.Role);
        cmd.Parameters.AddWithValue("@ISACTIVE", user.IsActive);
        await conn.OpenAsync();
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> DeleteAsync(string email)
    {
        const string sql = "DELETE FROM USERTABLE WHERE EMAIL = @EMAIL";
        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@EMAIL", email);
        await conn.OpenAsync();
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    private static AppUser MapUser(SqlDataReader reader) => new()
    {
        Email = reader.GetString(reader.GetOrdinal("EMAIL")),
        DisplayName = reader.IsDBNull(reader.GetOrdinal("DISPLAYNAME")) ? null : reader.GetString(reader.GetOrdinal("DISPLAYNAME")),
        Role = reader.GetString(reader.GetOrdinal("ROLE")),
        IsActive = reader.GetBoolean(reader.GetOrdinal("ISACTIVE")),
        CreatedDateTime = reader.IsDBNull(reader.GetOrdinal("CREATEDDATETIME")) ? null : reader.GetDateTime(reader.GetOrdinal("CREATEDDATETIME"))
    };
}
