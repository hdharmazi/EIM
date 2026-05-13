using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace ARM.Integration.Core.Helpers
{
    public class SQLServerHelper
    {
        private ILogger? logger;

        private readonly string connectionString;

        public void setlogger(ILogger _logger) => logger = _logger;

        public SQLServerHelper(string configuration)
        {
            connectionString = configuration;
        }

        public SQLServerHelper(string configuration, int connectionTimeoutSeconds)
        {
            var builder = new SqlConnectionStringBuilder(configuration)
            {
                ConnectTimeout = connectionTimeoutSeconds
            };
            connectionString = builder.ConnectionString;
        }

        // --- Dictionary-based overloads (existing, for dynamic parameters) ---

        public void ExecuteReader(string sql, Action<SqlDataReader> handleRow, Dictionary<string, object>? parameters = null)
        {
            try
            {
                using var conn = new SqlConnection(connectionString);
                conn.Open();
                using var cmd = new SqlCommand(sql, conn);
                AddParameters(cmd, parameters);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    handleRow(reader);
            }
            catch (Exception ex)
            {
                throw new Exception(ex.Message, ex);
            }
        }

        public int ExecuteNonQuery(string sql, Dictionary<string, object>? parameters = null)
        {
            try
            {
                using var conn = new SqlConnection(connectionString);
                conn.Open();
                using var cmd = new SqlCommand(sql, conn);
                AddParameters(cmd, parameters);
                return cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                throw new Exception(ex.Message, ex);
            }
        }

        // --- Typed SqlParameter overloads (replaces SQLHelper) ---

        /// <summary>
        /// Executes a query (Text or StoredProcedure) and invokes <paramref name="handleRow"/> for each result row.
        /// </summary>
        public void ExecuteReader(string sql, Action<SqlDataReader> handleRow, CommandType commandType, params SqlParameter[] parameters)
        {
            try
            {
                using var conn = new SqlConnection(connectionString);
                conn.Open();
                using var cmd = new SqlCommand(sql, conn) { CommandType = commandType };
                cmd.Parameters.AddRange(parameters);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    handleRow(reader);
            }
            catch (Exception ex)
            {
                throw new Exception(ex.Message, ex);
            }
        }

        /// <summary>
        /// Executes a non-query (Text or StoredProcedure) and returns the rows-affected count.
        /// </summary>
        public int ExecuteNonQuery(string sql, CommandType commandType, params SqlParameter[] parameters)
        {
            try
            {
                using var conn = new SqlConnection(connectionString);
                conn.Open();
                using var cmd = new SqlCommand(sql, conn) { CommandType = commandType };
                cmd.Parameters.AddRange(parameters);
                return cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                throw new Exception(ex.Message, ex);
            }
        }

        // --- Private helpers ---

        private void AddParameters(SqlCommand cmd, Dictionary<string, object>? parameters)
        {
            if (parameters == null) return;
            foreach (var param in parameters)
                cmd.Parameters.AddWithValue(param.Key, param.Value ?? DBNull.Value);
        }
    }
}


