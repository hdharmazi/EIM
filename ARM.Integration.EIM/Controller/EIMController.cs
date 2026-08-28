using System.Data;
using ARM.Integration.Core.Model;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using ARM.Integration.Core.Helpers;
using ARM.Integration.EIM.Orchestration;
using ARM.Integration.EIM.Retry;
using ARM.Integration.EIM.Routing;

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
        private readonly IFailureClassifier? _failureClassifier;
        private readonly RetryScheduleCalculator? _retrySchedule;

        /// <summary>
        /// Creates a new EIMController with DI-resolved orchestrations.
        /// </summary>
        /// <param name="failureClassifier">
        /// Supplied together with <paramref name="retrySchedule"/> to enable automatic retry. Left
        /// null by operator-initiated reprocess, which wants a definite answer rather than a
        /// scheduled retry.
        /// </param>
        public EIMController(
            string connectionString,
            ILogger logger,
            IReadOnlyDictionary<string, IOrchestration> orchestrations,
            IFailureClassifier? failureClassifier = null,
            RetryScheduleCalculator? retrySchedule = null)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _orchestrations = orchestrations ?? throw new ArgumentNullException(nameof(orchestrations));

            // Both or neither: a classifier with no schedule cannot say when to try again.
            if ((failureClassifier is null) != (retrySchedule is null))
            {
                throw new ArgumentException(
                    "Supply both a failure classifier and a retry schedule, or neither.",
                    nameof(failureClassifier));
            }

            _failureClassifier = failureClassifier;
            _retrySchedule = retrySchedule;
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
                new("@STATUS", request.Status),
                new("@DEDUPEKEY", request.DedupeKey ?? (object)DBNull.Value),
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
        /// Captures a message and processes it only if its route is live, rejecting duplicates.
        /// </summary>
        /// <remarks>
        /// Every accepted message is inserted first, whatever the route state, so nothing depends
        /// on the transport for durability. A held message is a row an operator can see, reprocess
        /// and replay; a message left on the queue is none of those things.
        /// </remarks>
        /// <returns>The message id, empty for a rejected duplicate, and the decision.</returns>
        /// <exception cref="RouteNotFoundException">The route is not registered.</exception>
        public async Task<(string MessageId, IngestDecision Decision)> InsertAndProcessAsync(
            MessageRequest request,
            IngestGate gate,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(gate);

            var key = new RouteKey(request.Environment, request.SourceSystem, request.SourceEntity ?? "");
            var decision = await gate.DecideAsync(key, request.DedupeKey, ct);

            switch (decision.Action)
            {
                case IngestAction.RejectDuplicate:
                    _logger.LogWarning(
                        "Rejected duplicate for {RouteKey} dedupe key {DedupeKey}: {Reason}.",
                        key, request.DedupeKey, decision.Reason);
                    return ("", decision);

                case IngestAction.RedriveExisting:
                {
                    var existingId = decision.ExistingMessageId!;

                    if (!decision.Process)
                    {
                        // Held rather than left at New, so the release sweep can find it.
                        UpdateMessageStatus(existingId, decision.Reason, decision.Status);
                        _logger.LogInformation("Message {MessageId} held: {Reason}.", existingId, decision.Reason);
                        return (existingId, decision);
                    }

                    await ProcessMessageAsync(existingId, ct);
                    return (existingId, decision);
                }

                default:
                {
                    var messageId = InsertMessage(
                        request with { Status = decision.Status, Type = decision.OrchestrationType });

                    if (!decision.Process)
                    {
                        _logger.LogInformation(
                            "Message {MessageId} for {RouteKey} held: {Reason}.", messageId, key, decision.Reason);
                        return (messageId, decision);
                    }

                    await ProcessMessageAsync(messageId, ct);
                    return (messageId, decision);
                }
            }
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
                UpdateMessageStatus(messageId, "Processing..", MessageStatus.Processing);

                var message = GetMessage(messageId);
                var result = await DispatchOrchestrationAsync(message, messageId, ct);

                responseMessage = result.Message;
                UpdateMessageStatus(messageId, result.Message, result.Status, result.DestinationId);
            }
            catch (ApplicationException exception)
            {
                responseMessage = $"ERROR:{ExceptionHelper.GetMessages(exception)}";
                RecordFailure(messageId, responseMessage, exception, MessageStatus.Failed);
            }
            catch (Exception exception)
            {
                responseMessage = $"ERROR:{ExceptionHelper.GetMessages(exception)}";
                RecordFailure(messageId, responseMessage, exception, MessageStatus.DeadLetter);
            }

            return responseMessage;
        }

        /// <summary>
        /// Records a failure, scheduling another attempt when the failure looks transient.
        /// </summary>
        /// <param name="terminalStatus">
        /// The status to use when the failure is not retryable, preserving the existing split
        /// between a business error and everything else.
        /// </param>
        private void RecordFailure(string messageId, string responseMessage, Exception exception, int terminalStatus)
        {
            if (_failureClassifier is null || _retrySchedule is null
                || _failureClassifier.Classify(exception) == FailureDisposition.Terminal)
            {
                UpdateMessageStatus(messageId, responseMessage, terminalStatus);
                return;
            }

            var attempt = IncrementRetryCount(messageId);

            if (!_retrySchedule.AttemptsRemain(attempt))
            {
                // Dead Letter, not Failed: attempts were exhausted rather than the data being wrong.
                UpdateMessageStatus(messageId,
                    $"{responseMessage} (retry attempts exhausted after {attempt})",
                    MessageStatus.DeadLetter);

                _logger.LogError("Message {MessageId} exhausted {Attempt} retry attempts.", messageId, attempt);
                return;
            }

            var nextAttemptUtc = _retrySchedule.NextAttemptUtc(attempt, DateTime.UtcNow);
            ScheduleRetry(messageId, $"{responseMessage} (attempt {attempt}, retrying)", nextAttemptUtc);

            _logger.LogWarning("Message {MessageId} failed retryably; attempt {Attempt} due at {NextAttempt:o}.",
                messageId, attempt, nextAttemptUtc);
        }

        /// <summary>Increments the attempt counter and returns its new value.</summary>
        private int IncrementRetryCount(string messageId)        {
            const string sql = """
                UPDATE MESSAGEQUEUETABLE
                SET RETRYCOUNT = RETRYCOUNT + 1
                OUTPUT inserted.RETRYCOUNT
                WHERE ID = @ID
                """;

            var attempt = 0;
            new SQLServerHelper(_connectionString).ExecuteReader(
                sql,
                r => { attempt = Convert.ToInt32(r["RETRYCOUNT"]); },
                CommandType.Text,
                new SqlParameter("@ID", messageId));

            return attempt;
        }

        private void ScheduleRetry(string messageId, string message, DateTime nextAttemptUtc)
        {
            const string sql = """
                UPDATE MESSAGEQUEUETABLE
                SET STATUS = @STATUS,
                    MESSAGELOG = @MESSAGELOG,
                    NEXTATTEMPTDATETIME = @NEXTATTEMPT
                WHERE ID = @ID
                """;

            new SQLServerHelper(_connectionString).ExecuteNonQuery(sql, CommandType.Text,
                new SqlParameter("@STATUS", MessageStatus.RetryScheduled),
                new SqlParameter("@MESSAGELOG", message),
                new SqlParameter("@NEXTATTEMPT", nextAttemptUtc),
                new SqlParameter("@ID", messageId));
        }

        /// <summary>
        /// Atomically claims a bounded batch of messages whose next attempt is due, skipping any
        /// whose route is not enabled.
        /// </summary>
        /// <remarks>
        /// The claim and the selection are one statement, so two sweeps running at once cannot take
        /// the same message. <c>READPAST</c> makes the second sweep skip rows the first has locked
        /// rather than block behind them. The results go through a table variable because the
        /// <c>MODIFIEDDATETIME</c> trigger forbids <c>OUTPUT</c> without <c>INTO</c>.
        /// <para>
        /// Routes that are not enabled are skipped rather than escalated: a pause must not still be
        /// writing to the destination, and the messages simply wait. The join also means a message
        /// whose route does not exist is never claimed at all — see task 9.8.
        /// </para>
        /// </remarks>
        public IReadOnlyList<string> ClaimDueRetries(int batchSize, DateTime dueUtc, string claimLog)
        {
            if (batchSize < 1)
                throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "Batch size must be at least 1.");

            const string sql = """
                DECLARE @Ids TABLE (ID BIGINT);

                WITH due AS (
                    SELECT TOP (@BATCH) m.ID, m.STATUS, m.MESSAGELOG
                    FROM MESSAGEQUEUETABLE AS m WITH (UPDLOCK, READPAST)
                    INNER JOIN ROUTETABLE AS r
                        ON  r.ENVIRONMENT  = m.ENVIRONMENT
                        AND r.SOURCESYSTEM = m.SOURCESYSTEM
                        AND r.SOURCEENTITY = m.SOURCEENTITY
                    WHERE m.STATUS = @RETRYSCHEDULED
                      AND m.NEXTATTEMPTDATETIME <= @DUE
                      AND r.ENABLED = 1
                    ORDER BY m.NEXTATTEMPTDATETIME, m.ID
                )
                UPDATE due
                SET STATUS = @PROCESSING, MESSAGELOG = @LOG
                OUTPUT inserted.ID INTO @Ids;

                SELECT ID FROM @Ids;
                """;

            var ids = new List<string>();
            new SQLServerHelper(_connectionString).ExecuteReader(
                sql,
                r => ids.Add(r["ID"].ToString() ?? ""),
                CommandType.Text,
                new SqlParameter("@BATCH", batchSize),
                new SqlParameter("@RETRYSCHEDULED", MessageStatus.RetryScheduled),
                new SqlParameter("@DUE", dueUtc),
                new SqlParameter("@PROCESSING", MessageStatus.Processing),
                new SqlParameter("@LOG", claimLog));

            return ids;
        }

        /// <summary>
        /// Retrieves a message from MESSAGEQUEUETABLE by ID.
        /// </summary>
        public QueueMessageContract GetMessage(string messageId)        {
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

        private async Task<OrchestrationResult> DispatchOrchestrationAsync(
            QueueMessageContract message, string messageId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(message.Type))
                throw new ApplicationException("Message Type is missing.");

            var stepLog = new OrchestrationLogger(_connectionString, long.Parse(messageId), _logger);

            // Host-supplied registrations are the only dispatch mechanism; the engine holds no
            // reference to any concrete orchestration and does not resolve types by name.
            if (!_orchestrations.TryGetValue(message.Type, out var orchestration))
            {
                throw new ApplicationException(
                    $"No orchestration is registered for type '{message.Type}'. " +
                    $"Registered types: {string.Join(", ", _orchestrations.Keys.OrderBy(k => k))}.");
            }

            return await orchestration.ProcessAsync(message, _connectionString, stepLog, ct);
        }

        private void UpdateMessageStatus(string messageId, string message, int status, string? destinationId = null)
        {
            var sql = destinationId is null
                ? @"UPDATE MESSAGEQUEUETABLE 
                SET STATUS = @STATUS, MESSAGELOG = @MESSAGELOG, MODIFIEDDATETIME = @MODIFIEDDATETIME 
                WHERE ID = @ID"
                : @"UPDATE MESSAGEQUEUETABLE 
                SET STATUS = @STATUS, MESSAGELOG = @MESSAGELOG, MODIFIEDDATETIME = @MODIFIEDDATETIME, DESTINATIONID = @DESTINATIONID 
                WHERE ID = @ID";

            var parameters = new List<SqlParameter>
            {
                new("@STATUS", status),
                new("@MESSAGELOG", message),
                new("@MODIFIEDDATETIME", DateTime.UtcNow),
                new("@ID", messageId),
            };
            if (destinationId is not null)
                parameters.Add(new SqlParameter("@DESTINATIONID", destinationId));

            new SQLServerHelper(_connectionString).ExecuteNonQuery(sql, CommandType.Text, [.. parameters]);
        }

        /// <summary>
        /// Applies a business-event reconciliation outcome to the row correlated by
        /// <paramref name="destinationId"/>. The transition is guarded in SQL rather than by a
        /// read-then-write, because this can run concurrently with the stuck-message sweep and with
        /// a second event for the same transaction id.
        /// </summary>
        /// <param name="responseKey">
        /// Optional D365 business key (e.g. "SalesId=200007") appended to the message log so an
        /// operator can find the record in D365 without decoding the correlation id.
        /// </param>
        /// <returns>Rows affected. Zero means the row was already terminal, and is not an error.</returns>
        public int UpdateStatusByDestinationId(
            string destinationId, int status, string message, string? responseKey = null)
        {
            if (string.IsNullOrWhiteSpace(destinationId))
                throw new ArgumentException("DestinationId is required.", nameof(destinationId));

            // Reconciliation is monotonic: Completed and Failed are terminal and a later or
            // duplicate event must never rewrite them.
            var sql = $@"UPDATE MESSAGEQUEUETABLE 
                SET STATUS = @STATUS, MESSAGELOG = @MESSAGELOG, MODIFIEDDATETIME = @MODIFIEDDATETIME 
                WHERE DESTINATIONID = @DESTINATIONID 
                  AND STATUS NOT IN ({MessageStatus.Completed}, {MessageStatus.Failed})";

            var log = string.IsNullOrWhiteSpace(responseKey) ? message : $"{message} {responseKey}";

            return new SQLServerHelper(_connectionString).ExecuteNonQuery(sql, CommandType.Text,
                new SqlParameter("@STATUS", status),
                new SqlParameter("@MESSAGELOG", log),
                new SqlParameter("@MODIFIEDDATETIME", DateTime.UtcNow),
                new SqlParameter("@DESTINATIONID", destinationId));
        }

        /// <summary>
        /// Resolves the most recent MESSAGEQUEUETABLE row ID for a given DESTINATIONID (correlation id).
        /// Used by asynchronous reconciliation to attach processing steps to the original transaction row.
        /// </summary>
        /// <returns>The message ID as a string, or <c>null</c> if no row matches.</returns>
        public string? GetMessageIdByDestinationId(string destinationId)
        {
            if (string.IsNullOrWhiteSpace(destinationId)) return null;

            const string sql = @"SELECT TOP 1 ID FROM MESSAGEQUEUETABLE 
                WHERE DESTINATIONID = @DESTINATIONID ORDER BY ID DESC";

            string? id = null;
            new SQLServerHelper(_connectionString).ExecuteReader(
                sql,
                r => { id = r["ID"].ToString(); },
                CommandType.Text,
                new SqlParameter("@DESTINATIONID", destinationId));
            return id;
        }

        /// <summary>
        /// Escalates messages stuck at <paramref name="fromStatus"/> whose last modification is older
        /// than <paramref name="olderThanUtc"/> to <paramref name="toStatus"/>. Used by the safety-net
        /// sweeper for asynchronous messages awaiting D365 confirmation that never arrived.
        /// </summary>
        /// <returns>The IDs of the escalated message rows (for alerting).</returns>
        public IReadOnlyList<string> EscalateStuckMessages(
            int fromStatus, int toStatus, DateTime olderThanUtc, string message)
        {
            const string sql = @"DECLARE @Ids TABLE (ID BIGINT);
                UPDATE MESSAGEQUEUETABLE 
                SET STATUS = @TO, MESSAGELOG = @MESSAGELOG, MODIFIEDDATETIME = @MODIFIEDDATETIME 
                OUTPUT inserted.ID INTO @Ids 
                WHERE STATUS = @FROM AND MODIFIEDDATETIME < @CUTOFF;
                SELECT ID FROM @Ids;";

            var ids = new List<string>();
            new SQLServerHelper(_connectionString).ExecuteReader(
                sql,
                r => ids.Add(r["ID"].ToString() ?? ""),
                CommandType.Text,
                new SqlParameter("@TO", toStatus),
                new SqlParameter("@MESSAGELOG", message),
                new SqlParameter("@MODIFIEDDATETIME", DateTime.UtcNow),
                new SqlParameter("@FROM", fromStatus),
                new SqlParameter("@CUTOFF", olderThanUtc));

            return ids;
        }
    }

    /// <summary>
    /// Immutable request object for inserting a new message into the queue.
    /// Replaces the old mutable public property pattern on EIMController.
    /// </summary>
    public sealed record MessageRequest
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

        /// <summary>Insert status. Defaults to New; the hold gate inserts Held instead.</summary>
        public int Status { get; init; } = MessageStatus.New;

        /// <summary>
        /// Identifies this *delivery* for duplicate detection - the source operation id, or the
        /// transport message id. Not the business record key: every change to one record shares a
        /// SourceId, so keying on that would reject an update as a duplicate of its own create.
        /// Null disables the check for this message.
        /// </summary>
        public string? DedupeKey { get; init; }
    }
}
