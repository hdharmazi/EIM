using ARM.Integration.EIM.Routing;
using Xunit;

namespace ARM.Integration.EIM.Tests;

public class ReleaseSweepTests
{
    private static readonly RouteKey Address = new("TEST", "D365FSC", "mserp_wtc_custpostaladdressv2entity");
    private static readonly RouteKey Contact = new("TEST", "D365FSC", "mserp_smmcontactpersonv2entity");

    private sealed class FakeStore : IReleaseSweepStore
    {
        private readonly Dictionary<RouteKey, Queue<ClaimedMessage[]>> _batches = new();

        public List<RouteKey> Routes { get; } = [];
        public List<(RouteKey Key, int BatchSize)> Claims { get; } = [];

        public void Add(RouteKey key, params ClaimedMessage[] batch)
        {
            if (!Routes.Contains(key)) Routes.Add(key);
            if (!_batches.TryGetValue(key, out var q)) _batches[key] = q = new Queue<ClaimedMessage[]>();
            q.Enqueue(batch);
        }

        public Task<IReadOnlyList<RouteKey>> GetDrainableRoutesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RouteKey>>(Routes);

        public Task<IReadOnlyList<ClaimedMessage>> ClaimHeldAsync(
            RouteKey key, int batchSize, string claimLog, CancellationToken ct = default)
        {
            Claims.Add((key, batchSize));
            var batch = _batches.TryGetValue(key, out var q) && q.Count > 0 ? q.Dequeue() : [];
            return Task.FromResult<IReadOnlyList<ClaimedMessage>>(batch);
        }
    }

    private sealed class RecordingProcessor(params string[] failIds) : IMessageProcessor
    {
        public List<string> Processed { get; } = [];

        public Task ProcessAsync(string messageId, CancellationToken ct = default)
        {
            Processed.Add(messageId);
            return failIds.Contains(messageId)
                ? Task.FromException(new InvalidOperationException($"boom {messageId}"))
                : Task.CompletedTask;
        }
    }

    private static ClaimedMessage M(string id) => new(id, $"src-{id}");

    [Fact]
    public async Task Messages_are_processed_in_claim_order()
    {
        // Ordering is the entire reason held messages exist rather than being dropped.
        var store = new FakeStore();
        store.Add(Address, M("1"), M("2"), M("3"), M("4"));
        var processor = new RecordingProcessor();

        await new ReleaseSweep(store, processor).RunAsync(10);

        Assert.Equal(["1", "2", "3", "4"], processor.Processed);
    }

    [Fact]
    public async Task A_failure_does_not_stop_the_drain()
    {
        var store = new FakeStore();
        store.Add(Address, M("1"), M("2"), M("3"));
        var processor = new RecordingProcessor("2");

        var result = await new ReleaseSweep(store, processor).RunAsync(10);

        Assert.Equal(["1", "2", "3"], processor.Processed);
        Assert.Equal(2, result.Released);
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public async Task Counts_are_reported_per_run()
    {
        var store = new FakeStore();
        store.Add(Address, M("1"), M("2"));
        store.Add(Contact, M("3"));

        var result = await new ReleaseSweep(store, new RecordingProcessor("3")).RunAsync(10);

        Assert.Equal(3, result.Claimed);
        Assert.Equal(2, result.Released);
        Assert.Equal(1, result.Failed);
        Assert.Equal(2, result.RoutesDrained);
    }

    [Fact]
    public async Task Each_route_gets_its_own_batch()
    {
        // A global claim in id order would let one busy route monopolise every batch.
        var store = new FakeStore();
        store.Add(Address, M("1"));
        store.Add(Contact, M("2"));
        var processor = new RecordingProcessor();

        await new ReleaseSweep(store, processor).RunAsync(50);

        Assert.Equal(2, store.Claims.Count);
        Assert.All(store.Claims, c => Assert.Equal(50, c.BatchSize));
        Assert.Contains(store.Claims, c => c.Key.Equals(Address));
        Assert.Contains(store.Claims, c => c.Key.Equals(Contact));
    }

    [Fact]
    public async Task A_route_that_claims_nothing_is_not_counted_as_drained()
    {
        var store = new FakeStore();
        store.Add(Address);

        var result = await new ReleaseSweep(store, new RecordingProcessor()).RunAsync(10);

        Assert.Equal(0, result.Claimed);
        Assert.Equal(0, result.RoutesDrained);
    }

    [Fact]
    public async Task Nothing_to_drain_is_not_an_error()
    {
        var result = await new ReleaseSweep(new FakeStore(), new RecordingProcessor()).RunAsync(10);

        Assert.Equal(0, result.Claimed);
        Assert.Equal(0, result.Released);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task Every_message_is_replayed_individually()
    {
        // The spec forbids collapsing several held messages for one record into one operation:
        // a create followed by updates must replay in full or the create is lost.
        var store = new FakeStore();
        store.Add(Address,
            new ClaimedMessage("1", "cust-1"),
            new ClaimedMessage("2", "cust-1"),
            new ClaimedMessage("3", "cust-1"));
        var processor = new RecordingProcessor();

        await new ReleaseSweep(store, processor).RunAsync(10);

        Assert.Equal(3, processor.Processed.Count);
    }

    [Fact]
    public async Task A_non_positive_batch_size_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new ReleaseSweep(new FakeStore(), new RecordingProcessor()).RunAsync(0));
    }
}
