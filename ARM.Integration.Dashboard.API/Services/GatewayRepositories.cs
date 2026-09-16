using System.Data;
using ARM.Integration.Dashboard.API.Models;
using Microsoft.Data.SqlClient;

namespace ARM.Integration.Dashboard.API.Services;

public class GatewayConsumerRepository(IConfiguration config)
{
    private readonly string _connectionString = config.GetConnectionString("EIMDB")
        ?? throw new InvalidOperationException("EIMDB connection string is missing.");

    // KEYHASH is projected only as a boolean, so no query path can leak it to the API surface.
    private const string Projection = """
        SELECT CONSUMERID, SYSTEMPROFILE, ENABLED, EXPIRESON, DESCRIPTION,
               CAST(CASE WHEN KEYHASH IS NULL THEN 0 ELSE 1 END AS bit) AS IDENTITYVERIFIED,
               CREATEDBY, CREATEDON, MODIFIEDBY, MODIFIEDON, VERIFIEDAPP, VERIFIEDON, GATEWAYURL
        FROM dbo.GATEWAYCONSUMER
        """;

    public async Task<List<GatewayConsumerRecord>> GetAllAsync()
    {
        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand($"{Projection} ORDER BY CONSUMERID", conn);

        await conn.OpenAsync();
        await using var reader = await cmd.ExecuteReaderAsync();

        var records = new List<GatewayConsumerRecord>();
        while (await reader.ReadAsync()) records.Add(Map(reader));
        return records;
    }

    public async Task<GatewayConsumerRecord?> GetAsync(string consumerId)
    {
        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand($"{Projection} WHERE CONSUMERID = @ID", conn);
        cmd.Parameters.Add("@ID", SqlDbType.NVarChar, 50).Value = consumerId;

        await conn.OpenAsync();
        await using var reader = await cmd.ExecuteReaderAsync();

        return await reader.ReadAsync() ? Map(reader) : null;
    }

    /// <summary>
    /// Counts enabled consumers whose identity cannot be verified. More than one such consumer means
    /// spoofing the <c>X-Consumer-Id</c> header would select somebody else's grants.
    /// </summary>
    public async Task<int> CountEnabledUnverifiedAsync(string? excludingConsumerId = null)
    {
        const string sql = """
            SELECT COUNT(*) FROM dbo.GATEWAYCONSUMER
            WHERE ENABLED = 1 AND KEYHASH IS NULL
              AND (@EXCLUDE IS NULL OR CONSUMERID <> @EXCLUDE)
            """;

        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@EXCLUDE", SqlDbType.NVarChar, 50).Value = (object?)excludingConsumerId ?? DBNull.Value;

        await conn.OpenAsync();
        return (int)(await cmd.ExecuteScalarAsync() ?? 0);
    }

    public async Task CreateAsync(GatewayConsumerInput input, string createdBy)
    {
        const string sql = """
            INSERT INTO dbo.GATEWAYCONSUMER
                (CONSUMERID, SYSTEMPROFILE, ENABLED, EXPIRESON, DESCRIPTION, GATEWAYURL, CREATEDBY, CREATEDON)
            VALUES (@ID, @PROFILE, @ENABLED, @EXPIRES, @DESCRIPTION, @URL, @BY, SYSUTCDATETIME())
            """;

        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);
        Bind(cmd, input);
        cmd.Parameters.Add("@BY", SqlDbType.NVarChar, 100).Value = createdBy;

        await conn.OpenAsync();
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<bool> UpdateAsync(string consumerId, GatewayConsumerInput input, string modifiedBy)
    {
        const string sql = """
            UPDATE dbo.GATEWAYCONSUMER
            SET SYSTEMPROFILE = @PROFILE, ENABLED = @ENABLED, EXPIRESON = @EXPIRES,
                DESCRIPTION = @DESCRIPTION, GATEWAYURL = @URL,
                MODIFIEDBY = @BY, MODIFIEDON = SYSUTCDATETIME()
            WHERE CONSUMERID = @ID
            """;

        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);
        Bind(cmd, input with { ConsumerId = consumerId });
        cmd.Parameters.Add("@BY", SqlDbType.NVarChar, 100).Value = modifiedBy;

        await conn.OpenAsync();
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> DeleteAsync(string consumerId)
    {
        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand("DELETE FROM dbo.GATEWAYCONSUMER WHERE CONSUMERID = @ID", conn);
        cmd.Parameters.Add("@ID", SqlDbType.NVarChar, 50).Value = consumerId;

        await conn.OpenAsync();
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    /// <summary>
    /// Stores a key's hash together with what it was verified against. The caller is responsible for
    /// never retaining the key itself.
    /// </summary>
    /// <remarks>
    /// Hash and verification are written in one statement so a row can never claim to be verified
    /// against an application while holding a different key's hash.
    /// </remarks>
    public async Task<bool> SetKeyHashAsync(
        string consumerId, string keyHash, string modifiedBy, string? verifiedApp, DateTime? verifiedOn)
    {
        const string sql = """
            UPDATE dbo.GATEWAYCONSUMER
            SET KEYHASH = @HASH, VERIFIEDAPP = @APP, VERIFIEDON = @ON,
                MODIFIEDBY = @BY, MODIFIEDON = SYSUTCDATETIME()
            WHERE CONSUMERID = @ID
            """;

        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@ID", SqlDbType.NVarChar, 50).Value = consumerId;
        cmd.Parameters.Add("@HASH", SqlDbType.Char, 64).Value = keyHash;
        cmd.Parameters.Add("@APP", SqlDbType.NVarChar, 100).Value = (object?)verifiedApp ?? DBNull.Value;
        cmd.Parameters.Add("@ON", SqlDbType.DateTime2).Value = (object?)verifiedOn ?? DBNull.Value;
        cmd.Parameters.Add("@BY", SqlDbType.NVarChar, 100).Value = modifiedBy;

        await conn.OpenAsync();
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    private static void Bind(SqlCommand cmd, GatewayConsumerInput input)
    {
        cmd.Parameters.Add("@ID", SqlDbType.NVarChar, 50).Value = input.ConsumerId;
        cmd.Parameters.Add("@PROFILE", SqlDbType.NVarChar, 30).Value = input.SystemProfile;
        cmd.Parameters.Add("@ENABLED", SqlDbType.Bit).Value = input.Enabled;
        cmd.Parameters.Add("@EXPIRES", SqlDbType.DateTime2).Value = (object?)input.ExpiresOn ?? DBNull.Value;
        cmd.Parameters.Add("@DESCRIPTION", SqlDbType.NVarChar, 200).Value = (object?)input.Description ?? DBNull.Value;
        cmd.Parameters.Add("@URL", SqlDbType.NVarChar, 200).Value =
            string.IsNullOrWhiteSpace(input.GatewayUrl) ? DBNull.Value : input.GatewayUrl;
    }

    private static GatewayConsumerRecord Map(SqlDataReader reader) => new()
    {
        ConsumerId = reader.GetString(0),
        SystemProfile = reader.GetString(1),
        Enabled = reader.GetBoolean(2),
        ExpiresOn = reader.IsDBNull(3) ? null : reader.GetDateTime(3),
        Description = reader.IsDBNull(4) ? null : reader.GetString(4),
        IdentityVerified = reader.GetBoolean(5),
        CreatedBy = reader.IsDBNull(6) ? null : reader.GetString(6),
        CreatedOn = reader.IsDBNull(7) ? null : reader.GetDateTime(7),
        ModifiedBy = reader.IsDBNull(8) ? null : reader.GetString(8),
        ModifiedOn = reader.IsDBNull(9) ? null : reader.GetDateTime(9),
        VerifiedApp = reader.IsDBNull(10) ? null : reader.GetString(10),
        VerifiedOn = reader.IsDBNull(11) ? null : reader.GetDateTime(11),
        GatewayUrl = reader.IsDBNull(12) ? null : reader.GetString(12),
    };
}

public class GatewayPermissionRepository(IConfiguration config)
{
    private readonly string _connectionString = config.GetConnectionString("EIMDB")
        ?? throw new InvalidOperationException("EIMDB connection string is missing.");

    public async Task<List<GatewayPermissionRecord>> GetAsync(string consumerId, string? environment)
    {
        const string sql = """
            SELECT CONSUMERID, ENVIRONMENT, RESOURCETYPE, RESOURCE, VERB, CREATEDBY, CREATEDON
            FROM dbo.GATEWAYPERMISSION
            WHERE CONSUMERID = @ID AND (@ENV IS NULL OR ENVIRONMENT = @ENV)
            ORDER BY ENVIRONMENT, RESOURCETYPE, RESOURCE, VERB
            """;

        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@ID", SqlDbType.NVarChar, 50).Value = consumerId;
        cmd.Parameters.Add("@ENV", SqlDbType.NVarChar, 30).Value = (object?)environment ?? DBNull.Value;

        await conn.OpenAsync();
        await using var reader = await cmd.ExecuteReaderAsync();

        var records = new List<GatewayPermissionRecord>();
        while (await reader.ReadAsync())
        {
            records.Add(new GatewayPermissionRecord
            {
                ConsumerId = reader.GetString(0),
                Environment = reader.GetString(1),
                ResourceType = reader.GetString(2),
                Resource = reader.GetString(3),
                Verb = reader.GetString(4),
                CreatedBy = reader.IsDBNull(5) ? null : reader.GetString(5),
                CreatedOn = reader.IsDBNull(6) ? null : reader.GetDateTime(6),
            });
        }

        return records;
    }

    public async Task CreateAsync(string consumerId, GatewayPermissionInput input, string createdBy)
    {
        const string sql = """
            INSERT INTO dbo.GATEWAYPERMISSION
                (CONSUMERID, ENVIRONMENT, RESOURCETYPE, RESOURCE, VERB, CREATEDBY, CREATEDON)
            VALUES (@ID, @ENV, @TYPE, @RESOURCE, @VERB, @BY, SYSUTCDATETIME())
            """;

        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);
        Bind(cmd, consumerId, input);
        cmd.Parameters.Add("@BY", SqlDbType.NVarChar, 100).Value = createdBy;

        await conn.OpenAsync();
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<bool> DeleteAsync(string consumerId, GatewayPermissionInput input)
    {
        const string sql = """
            DELETE FROM dbo.GATEWAYPERMISSION
            WHERE CONSUMERID = @ID AND ENVIRONMENT = @ENV AND RESOURCETYPE = @TYPE
              AND RESOURCE = @RESOURCE AND VERB = @VERB
            """;

        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);
        Bind(cmd, consumerId, input);

        await conn.OpenAsync();
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    private static void Bind(SqlCommand cmd, string consumerId, GatewayPermissionInput input)
    {
        cmd.Parameters.Add("@ID", SqlDbType.NVarChar, 50).Value = consumerId;
        cmd.Parameters.Add("@ENV", SqlDbType.NVarChar, 30).Value = input.Environment.Trim().ToUpperInvariant();
        cmd.Parameters.Add("@TYPE", SqlDbType.NVarChar, 20).Value = input.ResourceType.Trim().ToUpperInvariant();
        cmd.Parameters.Add("@RESOURCE", SqlDbType.NVarChar, 200).Value = input.Resource.Trim();
        cmd.Parameters.Add("@VERB", SqlDbType.NVarChar, 10).Value = input.Verb.Trim().ToUpperInvariant();
    }
}
