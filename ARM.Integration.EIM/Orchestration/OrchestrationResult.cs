namespace ARM.Integration.EIM.Orchestration;

/// <summary>
/// Result of an orchestration run. Lets an orchestration report the resulting
/// message status, a log message, and an optional destination correlation id
/// (e.g., a D365 CreatedTransactionId for asynchronous submits).
///
/// Status values follow the MESSAGEQUEUETABLE convention:
/// 2 = Completed, 3 = Failed, 5 = Submitted / Awaiting D365.
/// </summary>
public sealed record OrchestrationResult
{
    /// <summary>The resulting message status. Defaults to 2 (Completed) for synchronous orchestrations.</summary>
    public int Status { get; init; } = StatusCompleted;

    /// <summary>The log/response message to persist against the message row.</summary>
    public string Message { get; init; } = "";

    /// <summary>Optional destination correlation id, persisted into MESSAGEQUEUETABLE.DESTINATIONID.</summary>
    public string? DestinationId { get; init; }

    public const int StatusCompleted = 2;
    public const int StatusFailed = 3;
    public const int StatusSubmitted = 5;

    /// <summary>Creates a Completed (2) result. Used by synchronous orchestrations.</summary>
    public static OrchestrationResult Completed(string message, string? destinationId = null)
        => new() { Status = StatusCompleted, Message = message, DestinationId = destinationId };

    /// <summary>Creates a Submitted / Awaiting D365 (5) result carrying the destination correlation id.</summary>
    public static OrchestrationResult Submitted(string message, string? destinationId)
        => new() { Status = StatusSubmitted, Message = message, DestinationId = destinationId };
}
