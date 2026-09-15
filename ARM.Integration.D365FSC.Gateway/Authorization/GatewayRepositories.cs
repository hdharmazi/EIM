using System.Data;
using ARM.Integration.D365FSC.Gateway.Configuration;
using ARM.Integration.D365FSC.Gateway.Routing;
using Microsoft.Data.SqlClient;

namespace ARM.Integration.D365FSC.Gateway.Authorization;

public sealed record GatewayConsumer(
    string ConsumerId,
    string? KeyHash,
    string SystemProfile,
    bool Enabled,
    DateTimeOffset? ExpiresOn);

public interface IGatewayConsumerRepository
{
    Task<GatewayConsumer?> FindAsync(string consumerId, CancellationToken cancellationToken = default);
}

public interface IGatewayPermissionRepository
{
    /// <summary>
    /// Returns the stored canonical resource spelling when the grant exists, otherwise <c>null</c>.
    /// The stored spelling is returned rather than a boolean because D365 entity names are
    /// case-sensitive in the URL, so the caller's spelling must not be the one forwarded.
    /// </summary>
    Task<string?> FindCanonicalResourceAsync(
        string consumerId,
        string environment,
        GatewayResourceType resourceType,
        string resource,
        string verb,
        CancellationToken cancellationToken = default);
}

public sealed class SqlGatewayConsumerRepository(GatewayOptions options) : IGatewayConsumerRepository
{
    private const string Sql = """
        SELECT CONSUMERID, KEYHASH, SYSTEMPROFILE, ENABLED, EXPIRESON
        FROM dbo.GATEWAYCONSUMER
        WHERE CONSUMERID = @CONSUMERID
        """;

    public async Task<GatewayConsumer?> FindAsync(string consumerId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(options.EimDbConnectionString);
        await using var command = new SqlCommand(Sql, connection);
        command.Parameters.Add("@CONSUMERID", SqlDbType.NVarChar, 50).Value = consumerId;

        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new GatewayConsumer(
            reader.GetString(0),
            await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1),
            reader.GetString(2),
            reader.GetBoolean(3),
            await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetDateTime(4));
    }
}

public sealed class SqlGatewayPermissionRepository(GatewayOptions options) : IGatewayPermissionRepository
{
    private const string Sql = """
        SELECT RESOURCE
        FROM dbo.GATEWAYPERMISSION
        WHERE CONSUMERID = @CONSUMERID
          AND ENVIRONMENT = @ENVIRONMENT
          AND RESOURCETYPE = @RESOURCETYPE
          AND RESOURCE = @RESOURCE
          AND VERB = @VERB
        """;

    public async Task<string?> FindCanonicalResourceAsync(
        string consumerId,
        string environment,
        GatewayResourceType resourceType,
        string resource,
        string verb,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(options.EimDbConnectionString);
        await using var command = new SqlCommand(Sql, connection);
        command.Parameters.Add("@CONSUMERID", SqlDbType.NVarChar, 50).Value = consumerId;
        command.Parameters.Add("@ENVIRONMENT", SqlDbType.NVarChar, 30).Value = environment;
        command.Parameters.Add("@RESOURCETYPE", SqlDbType.NVarChar, 20).Value = resourceType.ToString().ToUpperInvariant();
        command.Parameters.Add("@RESOURCE", SqlDbType.NVarChar, 200).Value = resource;
        command.Parameters.Add("@VERB", SqlDbType.NVarChar, 10).Value = verb;

        await connection.OpenAsync(cancellationToken);

        // The database collation is case-insensitive, so this matches whatever casing the caller
        // sent; the value read back is the stored spelling, which is what gets forwarded.
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }
}
