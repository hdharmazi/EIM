using Microsoft.Extensions.Logging;
using Npgsql;
using System.Text.Json;



namespace ARM.Integration.Core.Helpers
{
    public class PostgresHelper
    {
        private ILogger? logger;
        private readonly string connectionString;

        public void setlogger(ILogger _logger) => logger = _logger;

        public PostgresHelper(string configuration)
        {
            using var doc = JsonDocument.Parse(configuration);
            connectionString = doc.RootElement.TryGetProperty("DBConn", out var prop)
                ? prop.GetString() ?? throw new ArgumentException("DBConn value is null in configuration JSON.")
                : throw new ArgumentException("DBConn not found in configuration JSON.");
        }

        /// <summary>Executes a query and invokes <paramref name="handleRow"/> for each row returned.</summary>
        public void ExecuteReader(string sql, Action<NpgsqlDataReader> handleRow, Dictionary<string, object>? parameters = null)
        {
            using var conn = new NpgsqlConnection(connectionString);
            conn.Open();

            using var cmd = new NpgsqlCommand(sql, conn);
            AddParameters(cmd, parameters);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                handleRow(reader);
        }

        /// <summary>Executes an INSERT / UPDATE / DELETE and returns the number of rows affected.</summary>
        public int ExecuteNonQuery(string sql, Dictionary<string, object>? parameters = null)
        {
            using var conn = new NpgsqlConnection(connectionString);
            conn.Open();

            using var cmd = new NpgsqlCommand(sql, conn);
            AddParameters(cmd, parameters);

            return cmd.ExecuteNonQuery();
        }

        /// <summary>Executes a SQL function or query asynchronously.</summary>
        public async Task ExecuteSqlFunctionAsync(string sql, int timeoutSecs = 0, Dictionary<string, object>? parameters = null)
        {
            await using var dataSource = NpgsqlDataSource.Create(connectionString);
            await using var cmd        = dataSource.CreateCommand(sql);
            AddParameters(cmd, parameters);

            if (timeoutSecs > 0)
                cmd.CommandTimeout = timeoutSecs;

            await using var reader = await cmd.ExecuteReaderAsync();
        }

        /// <summary>Sync wrapper for backward compatibility.</summary>
        public void ExecuteSqlFunction(string sql, int timeoutSecs = 0, Dictionary<string, object>? parameters = null)
            => ExecuteSqlFunctionAsync(sql, timeoutSecs, parameters).GetAwaiter().GetResult();

        private void AddParameters(NpgsqlCommand cmd, Dictionary<string, object>? parameters)
        {
            if (parameters == null) return;
            foreach (var param in parameters)
                cmd.Parameters.AddWithValue(param.Key, param.Value ?? DBNull.Value);
        }
    }
}

