using System.Data;
using System.Security.Cryptography;
using System.Text;
using ARM.Integration.D365FSC.Gateway.Configuration;
using Microsoft.Data.SqlClient;

namespace ARM.Integration.D365FSC.Gateway.Idempotency;

public enum IdempotencyOutcome
{
    /// <summary>No prior record: forward the request.</summary>
    Proceed,

    /// <summary>Same key and same request: return the stored response without calling D365.</summary>
    Replay,

    /// <summary>Same key, different request. Replaying would return the wrong record.</summary>
    Conflict,

    /// <summary>Another instance is mid-flight with this key.</summary>
    InProgress
}

public sealed record IdempotencyDecision(
    IdempotencyOutcome Outcome,
    int? ResponseStatus = null,
    string? ResponseBody = null);

public interface IIdempotencyStore
{
    Task<IdempotencyDecision> BeginAsync(
        string consumerId, string idempotencyKey, string requestHash, DateTimeOffset expiresOn,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(
        string consumerId, string idempotencyKey, int responseStatus, string responseBody,
        CancellationToken cancellationToken = default);

    Task AbandonAsync(string consumerId, string idempotencyKey, CancellationToken cancellationToken = default);

    Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default);
}

public sealed class SqlIdempotencyStore(GatewayOptions options) : IIdempotencyStore
{
    /// <remarks>
    /// The race is resolved by the primary key, not by a read-then-write in application code. Two
    /// instances handling the same retry both attempt the insert; exactly one wins, and the loser
    /// reads back what the winner recorded. A SELECT-then-INSERT would let both through.
    /// </remarks>
    private const string BeginSql = """
        SET NOCOUNT ON;

        DELETE FROM dbo.GATEWAYIDEMPOTENCY
        WHERE CONSUMERID = @CONSUMERID AND IDEMPOTENCYKEY = @KEY AND EXPIRESON <= SYSUTCDATETIME();

        BEGIN TRY
            INSERT INTO dbo.GATEWAYIDEMPOTENCY
                (CONSUMERID, IDEMPOTENCYKEY, REQUESTHASH, STATUS, EXPIRESON)
            VALUES (@CONSUMERID, @KEY, @HASH, N'INPROGRESS', @EXPIRESON);

            SELECT N'PROCEED' AS Outcome, NULL AS ResponseStatus, NULL AS ResponseBody;
        END TRY
        BEGIN CATCH
            IF ERROR_NUMBER() IN (2601, 2627)
                SELECT CASE
                           WHEN REQUESTHASH <> @HASH THEN N'CONFLICT'
                           WHEN STATUS = N'INPROGRESS' THEN N'INPROGRESS'
                           ELSE N'REPLAY'
                       END AS Outcome,
                       RESPONSESTATUS AS ResponseStatus,
                       RESPONSEBODY AS ResponseBody
                FROM dbo.GATEWAYIDEMPOTENCY
                WHERE CONSUMERID = @CONSUMERID AND IDEMPOTENCYKEY = @KEY;
            ELSE
                THROW;
        END CATCH;
        """;

    public async Task<IdempotencyDecision> BeginAsync(
        string consumerId, string idempotencyKey, string requestHash, DateTimeOffset expiresOn,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(options.EimDbConnectionString);
        await using var command = new SqlCommand(BeginSql, connection);
        command.Parameters.Add("@CONSUMERID", SqlDbType.NVarChar, 50).Value = consumerId;
        command.Parameters.Add("@KEY", SqlDbType.NVarChar, 100).Value = idempotencyKey;
        command.Parameters.Add("@HASH", SqlDbType.Char, 64).Value = requestHash;
        command.Parameters.Add("@EXPIRESON", SqlDbType.DateTime2).Value = expiresOn.UtcDateTime;

        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return new IdempotencyDecision(IdempotencyOutcome.Proceed);
        }

        var outcome = reader.GetString(0) switch
        {
            "REPLAY" => IdempotencyOutcome.Replay,
            "CONFLICT" => IdempotencyOutcome.Conflict,
            "INPROGRESS" => IdempotencyOutcome.InProgress,
            _ => IdempotencyOutcome.Proceed
        };

        return new IdempotencyDecision(
            outcome,
            await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetInt32(1),
            await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2));
    }

    public async Task CompleteAsync(
        string consumerId, string idempotencyKey, int responseStatus, string responseBody,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE dbo.GATEWAYIDEMPOTENCY
            SET STATUS = N'COMPLETED', RESPONSESTATUS = @STATUS, RESPONSEBODY = @BODY
            WHERE CONSUMERID = @CONSUMERID AND IDEMPOTENCYKEY = @KEY
            """;

        await using var connection = new SqlConnection(options.EimDbConnectionString);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@CONSUMERID", SqlDbType.NVarChar, 50).Value = consumerId;
        command.Parameters.Add("@KEY", SqlDbType.NVarChar, 100).Value = idempotencyKey;
        command.Parameters.Add("@STATUS", SqlDbType.Int).Value = responseStatus;
        command.Parameters.Add("@BODY", SqlDbType.NVarChar).Value = responseBody;

        await connection.OpenAsync(cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Removes the in-progress marker when the request failed before D365 could answer, so the
    /// client's retry is not blocked by a reservation for something that never happened.
    /// </summary>
    public async Task AbandonAsync(
        string consumerId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        const string sql = """
            DELETE FROM dbo.GATEWAYIDEMPOTENCY
            WHERE CONSUMERID = @CONSUMERID AND IDEMPOTENCYKEY = @KEY AND STATUS = N'INPROGRESS'
            """;

        await using var connection = new SqlConnection(options.EimDbConnectionString);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@CONSUMERID", SqlDbType.NVarChar, 50).Value = consumerId;
        command.Parameters.Add("@KEY", SqlDbType.NVarChar, 100).Value = idempotencyKey;

        await connection.OpenAsync(cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        const string sql = "DELETE FROM dbo.GATEWAYIDEMPOTENCY WHERE EXPIRESON <= SYSUTCDATETIME()";

        await using var connection = new SqlConnection(options.EimDbConnectionString);
        await using var command = new SqlCommand(sql, connection);

        await connection.OpenAsync(cancellationToken);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Fingerprints the request so that reusing a key for different content is detectable. Without
    /// it, a replay could confidently return the wrong record.
    /// </summary>
    public static string ComputeRequestHash(string method, string path, string query, byte[] body)
    {
        var prefix = Encoding.UTF8.GetBytes($"{method.ToUpperInvariant()}\n{path}\n{query}\n");
        var buffer = new byte[prefix.Length + body.Length];
        prefix.CopyTo(buffer, 0);
        body.CopyTo(buffer, prefix.Length);

        return Convert.ToHexStringLower(SHA256.HashData(buffer));
    }
}
