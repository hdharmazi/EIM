using Microsoft.Data.SqlClient;

namespace ARM.Integration.EIM.Routing;

public sealed class SqlRouteRepository(string connectionString) : IRouteRepository
{
    private readonly string _connectionString = !string.IsNullOrWhiteSpace(connectionString)
        ? connectionString
        : throw new ArgumentException("A connection string is required.", nameof(connectionString));

    public async Task<IReadOnlyDictionary<RouteKey, RouteDefinition>> LoadAllAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT ENVIRONMENT, SOURCESYSTEM, SOURCEENTITY, ORCHESTRATIONTYPE, ENABLED, ONPAUSE
            FROM dbo.ROUTETABLE
            """;

        var routes = new Dictionary<RouteKey, RouteDefinition>();

        await using var connection = new SqlConnection(_connectionString);
        await using var command = new SqlCommand(sql, connection);
        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var key = new RouteKey(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2));

            routes[key] = new RouteDefinition(
                reader.GetString(3),
                reader.GetBoolean(4),
                ParsePausePolicy(reader.GetString(5), key));
        }

        return routes;
    }

    private static PausePolicy ParsePausePolicy(string value, RouteKey key) =>
        Enum.TryParse<PausePolicy>(value, ignoreCase: true, out var policy)
            ? policy
            // The column has a CHECK constraint, so this means the constraint was widened without
            // the enum being extended. Guessing a policy would silently change what a pause does.
            : throw new InvalidOperationException(
                $"Route '{key}' has unrecognised ONPAUSE value '{value}'.");
}
