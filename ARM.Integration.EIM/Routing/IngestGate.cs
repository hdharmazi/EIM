namespace ARM.Integration.EIM.Routing;

/// <summary>An already-ingested message for the same source id.</summary>
public readonly record struct ExistingMessage(string MessageId, int Status);

/// <summary>
/// Finds a message already ingested for the same delivery, scoped by source system and
/// environment. The key identifies the delivery - a source operation id or transport message id -
/// not the business record, since every change to one record shares a business key.
/// </summary>
public interface IDuplicateProbe
{
    Task<ExistingMessage?> FindAsync(RouteKey key, string dedupeKey, CancellationToken cancellationToken = default);
}

public enum IngestAction
{
    /// <summary>No prior message for this source id.</summary>
    InsertNew,

    /// <summary>A row exists but never progressed past ingest, so it is driven rather than duplicated.</summary>
    RedriveExisting,

    /// <summary>A prior message already progressed beyond ingest.</summary>
    RejectDuplicate
}

/// <param name="ExistingMessageId">Set only for <see cref="IngestAction.RedriveExisting"/>.</param>
public readonly record struct IngestDecision(
    IngestAction Action,
    string? ExistingMessageId,
    int Status,
    bool Process,
    string OrchestrationType,
    string Reason);

/// <summary>
/// Decides what to do with an arriving message: insert it, drive an existing unprocessed row, or
/// reject it as a duplicate.
/// </summary>
/// <remarks>
/// Dedupe is a prerequisite for release rather than a follow-up. A pause window is exactly when a
/// re-sending source piles up duplicates, and release is when they would all fire at once.
/// </remarks>
public sealed class IngestGate(HoldGate holdGate, IDuplicateProbe duplicates)
{
    private readonly HoldGate _holdGate = holdGate ?? throw new ArgumentNullException(nameof(holdGate));
    private readonly IDuplicateProbe _duplicates = duplicates ?? throw new ArgumentNullException(nameof(duplicates));

    /// <param name="dedupeKey">
    /// Identifies this delivery. Null or blank skips the duplicate check entirely, which is the
    /// correct behaviour for a source that offers nothing stable to key on: inventing a key from
    /// the business record would reject legitimate updates.
    /// </param>
    /// <exception cref="RouteNotFoundException">The route is not registered.</exception>
    public async Task<IngestDecision> DecideAsync(
        RouteKey key, string? dedupeKey, CancellationToken cancellationToken = default)
    {
        var existing = string.IsNullOrWhiteSpace(dedupeKey)
            ? null
            : await _duplicates.FindAsync(key, dedupeKey, cancellationToken);

        // Checked before the route is resolved: rejecting a duplicate needs neither the
        // orchestration type nor a backlog probe.
        if (existing is { } prior && prior.Status != MessageStatus.New)
        {
            return new IngestDecision(IngestAction.RejectDuplicate, prior.MessageId,
                prior.Status, Process: false, OrchestrationType: "",
                $"duplicate of message {prior.MessageId} already at status {prior.Status}");
        }

        var gate = await _holdGate.DecideAsync(key, cancellationToken);

        if (existing is { } unprocessed)
        {
            // Ingested but never processed - a crash between insert and dispatch. Driving the
            // existing row is what stops that crash stranding the message permanently, and stops
            // redelivery inserting a second copy of it.
            return new IngestDecision(IngestAction.RedriveExisting, unprocessed.MessageId,
                gate.Status, gate.Process, gate.OrchestrationType,
                $"re-driving unprocessed message {unprocessed.MessageId}, {gate.Reason}");
        }

        return new IngestDecision(IngestAction.InsertNew, null,
            gate.Status, gate.Process, gate.OrchestrationType, gate.Reason);
    }
}
