using Microsoft.Extensions.Logging;

namespace ARM.Integration.EIM.Routing;

/// <param name="MessageId">The queue row id.</param>
/// <param name="SourceId">Carried so a release can be reported per business record.</param>
public readonly record struct ClaimedMessage(string MessageId, string SourceId);

public interface IReleaseSweepStore
{
    /// <summary>Routes that are enabled and still have held messages.</summary>
    Task<IReadOnlyList<RouteKey>> GetDrainableRoutesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims the oldest held messages for one route, stopping short of any message
    /// on that route still awaiting retry so an older change cannot be applied after a newer one.
    /// </summary>
    Task<IReadOnlyList<ClaimedMessage>> ClaimHeldAsync(
        RouteKey key, int batchSize, string claimLog, CancellationToken cancellationToken = default);
}

public interface IMessageProcessor
{
    Task ProcessAsync(string messageId, CancellationToken cancellationToken = default);
}

public sealed record ReleaseSweepResult(int Claimed, int Released, int Failed, int RoutesDrained);

/// <summary>
/// Drains held messages for routes that have been resumed.
/// </summary>
/// <remarks>
/// Claims are per route rather than across all of them. A global claim in id order would let one
/// busy route monopolise every batch — with tens of thousands of held address messages, a route
/// resumed at the same time would wait hours for its first message.
/// </remarks>
public sealed class ReleaseSweep(
    IReleaseSweepStore store,
    IMessageProcessor processor,
    ILogger? logger = null)
{
    private readonly IReleaseSweepStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IMessageProcessor _processor = processor ?? throw new ArgumentNullException(nameof(processor));

    public async Task<ReleaseSweepResult> RunAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        if (batchSize < 1)
            throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "Batch size must be at least 1.");

        var routes = await _store.GetDrainableRoutesAsync(cancellationToken);

        var claimed = 0;
        var released = 0;
        var failed = 0;
        var routesDrained = 0;

        foreach (var route in routes)
        {
            var batch = await _store.ClaimHeldAsync(route, batchSize, "Claimed by release sweep.", cancellationToken);

            if (batch.Count == 0)
            {
                continue;
            }

            routesDrained++;
            claimed += batch.Count;

            // Serially, in claim order. Parallelism here would reorder writes to the destination,
            // which is the one thing holding was meant to preserve.
            foreach (var message in batch)
            {
                try
                {
                    await _processor.ProcessAsync(message.MessageId, cancellationToken);
                    released++;

                    // One line per released record: the only trace of what a release overwrote,
                    // given Decision 23 replays without consulting the destination.
                    logger?.LogInformation(
                        "Released {MessageId} for {RouteKey}, source id {SourceId}.",
                        message.MessageId, route, message.SourceId);
                }
                catch (Exception ex)
                {
                    // One poison message must not block the backlog. It has already been recorded
                    // at its own failure status by the processor.
                    failed++;
                    logger?.LogError(ex,
                        "Release of {MessageId} for {RouteKey} failed; continuing with the batch.",
                        message.MessageId, route);
                }
            }
        }

        var result = new ReleaseSweepResult(claimed, released, failed, routesDrained);

        logger?.LogInformation(
            "Release sweep: {Routes} route(s), claimed {Claimed}, released {Released}, failed {Failed}.",
            result.RoutesDrained, result.Claimed, result.Released, result.Failed);

        return result;
    }
}
