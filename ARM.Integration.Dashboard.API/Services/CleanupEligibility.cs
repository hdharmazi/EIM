using ARM.Integration.EIM;

namespace ARM.Integration.Dashboard.API.Services;

/// <summary>The statuses a cleanup request may delete, or the reason it was refused.</summary>
public sealed record CleanupScope(IReadOnlyList<int> Statuses, string? Refusal)
{
    public bool IsRefused => Refusal is not null;
}

/// <summary>
/// Decides which statuses a cleanup request may delete.
/// </summary>
/// <remarks>
/// MESSAGEQUEUETABLE is the system of record for hold/release, retry and reconciliation rather than
/// a log, so an age-only delete that ignored status would destroy in-flight work.
/// </remarks>
public static class CleanupEligibility
{
    /// <summary>Deleted when no status is named: terminal, with nothing else waiting on them.</summary>
    public static readonly int[] DefaultDeletable = [MessageStatus.Completed, MessageStatus.Failed];

    // Phrased for an operator: what depends on the rows, not just that they are protected.
    private static readonly Dictionary<int, string> Protections = new()
    {
        [MessageStatus.New] = "NEW is protected: these messages have not been processed yet.",
        [MessageStatus.Processing] = "PROCESSING is protected: these messages are mid-flight.",
        [MessageStatus.DeadLetter] = "DEAD is protected: these messages are reprocessable and still correctable by a late business event.",
        [MessageStatus.AwaitingD365] = "AWAITING D365 is protected: these messages are waiting for asynchronous confirmation.",
        [MessageStatus.Held] = "HELD is protected: these messages are a paused route's backlog and drain when the route resumes.",
        [MessageStatus.RetryScheduled] = "RETRY SCHEDULED is protected: these messages are queued for another attempt.",
    };

    /// <summary>
    /// Resolves a request into the statuses it may delete. A selected status also acts as the status
    /// filter, so the caller does not add a second one.
    /// </summary>
    public static CleanupScope Resolve(int? status, bool acknowledged)
    {
        if (status is not { } selected)
            return new CleanupScope(DefaultDeletable, null);

        if (DefaultDeletable.Contains(selected) || acknowledged)
            return new CleanupScope([selected], null);

        var reason = Protections.TryGetValue(selected, out var known)
            ? known
            : $"Status {selected} is not a recognised status and is protected.";

        return new CleanupScope([], $"{reason} Confirm explicitly to delete it.");
    }
}
