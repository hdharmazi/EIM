using System.Data;
using ARM.Integration.Core.Model;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using ARM.Integration.Core.Helpers;
using ARM.Integration.EIM.Orchestration;

namespace ARM.Integration.EIM
{
    /// <summary>
    /// Stateless controller for the EIM message queue pipeline.
    /// Manages message insertion, status updates, and orchestration dispatch.
    /// </summary>
    public class EIMController
    {
        private readonly string _connectionString;
        private readonly ILogger _logger;
        private readonly IReadOnlyDictionary<string, IOrchestration> _orchestrations;

        /// <summary>
        /// Creates a new EIMController with DI-resolved orchestrations.
        /// </summary>
        public EIMController(
            string connectionString,
            ILogger logger,
            IReadOnlyDictionary<string, IOrchestration> orchestrations)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _orchestrations = orchestrations ?? throw new ArgumentNullException(nameof(orchestrations));
        }

        /// <summary>
        /// Inserts a new message into the MESSAGEQUEUETABLE via stored procedure.
        /// </summary>
        public string InsertMessage(MessageRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            SqlParameter[] parameters =
            [
                new("@ENVIRONMENT", request.Environment ?? (object)DBNull.Value),
                new("@SOURCEID", request.SourceId ?? (object)DBNull.Value),
                new("@SOURCESYSTEM", request.SourceSystem ?? (object)DBNull.Value),
                new("@DESTINATIONID", request.DestinationId ?? (object)DBNull.Value),
                new("@DESTINATIONSYSTEM", request.DestinationSystem ?? (object)DBNull.Value),
                new("@TYPE", request.Type ?? (object)DBNull.Value),
                new("@PAYLOAD", request.Payload ?? (object)DBNull.Value),
                new("@SOURCEENTITY", request.SourceEntity ?? (object)DBNull.Value),
                new("@DESTINATIONENTITY", request.DestinationEntity ?? (object)DBNull.Value),
                new("@RESPONSEQUEUE", request.ResponseQueue ?? (object)DBNull.Value),
                new("@QUEUE", request.Queue ?? (object)DBNull.Value),
            ];

            string id = "";
            new SQLServerHelper(_connectionString).ExecuteReader(
                "SPINSERTMESSAGEQUEUE",
                r => { id = r["ID"].ToString() ?? ""; },
                CommandType.StoredProcedure,
                parameters);
            return id;
        }

        /// <summary>
        /// Processes a message by ID: updates status, dispatches to orchestration, and records the result.
        /// </summary>
        public async Task<string> ProcessMessageAsync(string messageId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(messageId))
                throw new ArgumentException("Message ID is required.", nameof(messageId));

            string responseMessage = "";

            try
            {
                UpdateMessageStatus(messageId, "Processing..", 1);

                var message = GetMessage(messageId);
                responseMessage = await DispatchOrchestrationAsync(message, messageId, ct);

                UpdateMessageStatus(messageId, responseMessage, 2);
            }
            catch (ApplicationException exception)
            {
                responseMessage = $"ERROR:{ExceptionHelper.GetMessages(exception)}";
                UpdateMessageStatus(messageId, responseMessage, 3);
            }
            catch (Exception exception)
            {
                responseMessage = $"ERROR:{ExceptionHelper.GetMessages(exception)}";
                UpdateMessageStatus(messageId, responseMessage, 4);
            }

            return responseMessage;
        }

        /// <summary>
        /// Retrieves a message from MESSAGEQUEUETABLE by ID.
        /// </summary>
        public QueueMessageContract GetMessage(string messageId)
        {
            const string sql = "SELECT * FROM MESSAGEQUEUETABLE WHERE ID = @ID";

            QueueMessageContract? result = null;
            new SQLServerHelper(_connectionString).ExecuteReader(sql, reader =>
            {
                result = new QueueMessageContract
                {
                    Id = reader["ID"].ToString(),
                    SourceId = reader["SOURCEID"].ToString(),
                    SourceSystem = reader["SOURCESYSTEM"].ToString(),
                    SourceEntity = reader["SOURCEENTITY"].ToString(),
                    DestinationSystem = reader["DESTINATIONSYSTEM"].ToString(),
                    DestinationEntity = reader["DESTINATIONENTITY"].ToString(),
                    Payload = reader["PAYLOAD"].ToString(),
                    Type = reader["TYPE"].ToString(),
                    Environment = reader["ENVIRONMENT"].ToString(),
                    ResponseQueue = reader["RESPONSEQUEUE"]?.ToString(),
                    Queue = reader["QUEUE"]?.ToString(),
                };
            }, CommandType.Text, new SqlParameter("@ID", messageId));

            if (result is null)
                throw new ApplicationException($"No MESSAGEQUEUETABLE record found for ID '{messageId}'.");

            return result;
        }

        private async Task<string> DispatchOrchestrationAsync(
            QueueMessageContract message, string messageId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(message.Type))
                throw new ApplicationException("Message Type is missing.");

            var stepLog = new OrchestrationLogger(_connectionString, long.Parse(messageId), _logger);

            // Try DI-registered orchestrations first
            if (_orchestrations.TryGetValue(message.Type, out var orchestration))
            {
                return await orchestration.ProcessAsync(message, _connectionString, stepLog, ct);
            }

            // Fallback: reflection-based dispatch for backward compatibility
            _logger.LogWarning("No DI-registered orchestration for type '{Type}'. Falling back to reflection.", message.Type);

            var type = Type.GetType($"ARM.Integration.EIM.Orchestration.{message.Type}")
                ?? throw new ApplicationException($"Cannot find orchestration type '{message.Type}'.");

            var instance = Activator.CreateInstance(type)
                ?? throw new ApplicationException($"Cannot create instance of '{message.Type}'.");

            var methodInfo = type.GetMethod("ProcessAsync")
                ?? throw new ApplicationException($"Method 'ProcessAsync' not found on '{message.Type}'.");

            var eimMessagesJson = JsonSerializer.Serialize(message);
            var task = (Task<string>)methodInfo.Invoke(instance, [eimMessagesJson, _connectionString, stepLog]);
            return await task;
        }

        private void UpdateMessageStatus(string messageId, string message, int status)
        {
            const string sql = @"UPDATE MESSAGEQUEUETABLE 
                SET STATUS = @STATUS, MESSAGELOG = @MESSAGELOG, MODIFIEDDATETIME = @MODIFIEDDATETIME 
                WHERE ID = @ID";

            new SQLServerHelper(_connectionString).ExecuteNonQuery(sql, CommandType.Text,
                new SqlParameter("@STATUS", status),
                new SqlParameter("@MESSAGELOG", message),
                new SqlParameter("@MODIFIEDDATETIME", DateTime.UtcNow),
                new SqlParameter("@ID", messageId));
        }
    }

    /// <summary>
    /// Immutable request object for inserting a new message into the queue.
    /// Replaces the old mutable public property pattern on EIMController.
    /// </summary>
    public sealed class MessageRequest
    {
        public required string Environment { get; init; }
        public required string SourceId { get; init; }
        public required string SourceSystem { get; init; }
        public string? DestinationId { get; init; }
        public required string DestinationSystem { get; init; }
        public required string Type { get; init; }
        public required string Payload { get; init; }
        public string? SourceEntity { get; init; }
        public string? DestinationEntity { get; init; }
        public string? ResponseQueue { get; init; }
        public string? Queue { get; init; }
    }
}
