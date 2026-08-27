namespace ARM.Integration.EIM.Orchestration;

/// <summary>
/// Result of an orchestration run. Lets an orchestration report the resulting
/// message status, a log message, and an optional destination correlation id
/// (e.g., a D365 CreatedTransactionId for asynchronous submits).
/// </summary>
public sealed record OrchestrationResult
{
    /// <summary>The resulting message status. Defaults to Completed for synchronous orchestrations.</summary>
    public int Status { get; init; } = MessageStatus.Completed;

    /// <summary>The log/response message to persist against the message row.</summary>
    public string Message { get; init; } = "";

    /// <summary>Optional destination correlation id, persisted into MESSAGEQUEUETABLE.DESTINATIONID.</summary>
    public string? DestinationId { get; init; }

    /// <summary>Creates a Completed result. Used by synchronous orchestrations.</summary>
    public static OrchestrationResult Completed(string message, string? destinationId = null)
        => new() { Status = MessageStatus.Completed, Message = message, DestinationId = destinationId };

    /// <summary>Creates a Submitted / Awaiting D365 result carrying the destination correlation id.</summary>
    public static OrchestrationResult Submitted(string message, string? destinationId)
        => new() { Status = MessageStatus.AwaitingD365, Message = message, DestinationId = destinationId };
}
