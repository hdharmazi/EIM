using ARM.Integration.EIM.Routing;
using Xunit;

namespace ARM.Integration.EIM.Tests;

public class IngestGateTests
{
    private static readonly RouteKey Address =
        new("TEST", "D365FSC", "mserp_wtc_custpostaladdressv2entity");

    private sealed class StubResolver(bool enabled) : IRouteResolver
    {
        public Task<RouteDefinition> ResolveAsync(RouteKey key, CancellationToken ct = default)
            => Task.FromResult(new RouteDefinition("CustomerAddress_To_Capture", enabled, PausePolicy.Hold));

        public void Invalidate() { }
    }

    private sealed class StubBacklog(bool hasBacklog) : IRouteBacklogProbe
    {
        public int Calls { get; private set; }

        public Task<bool> HasBacklogAsync(RouteKey key, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(hasBacklog);
        }
    }

    private sealed class StubDuplicates(ExistingMessage? existing) : IDuplicateProbe
    {
        public string? LastKey { get; private set; }
        public int Calls { get; private set; }

        public Task<ExistingMessage?> FindAsync(RouteKey key, string dedupeKey, CancellationToken ct = default)
        {
            Calls++;
            LastKey = dedupeKey;
            return Task.FromResult(existing);
        }
    }

    private static IngestGate Build(
        bool enabled = true, bool hasBacklog = false, ExistingMessage? existing = null,
        StubBacklog? backlog = null)
        => new(new HoldGate(new StubResolver(enabled), backlog ?? new StubBacklog(hasBacklog)),
               new StubDuplicates(existing));

    [Fact]
    public async Task A_new_source_id_on_a_live_route_is_inserted_and_processed()
    {
        var decision = await Build().DecideAsync(Address, "cust-1");

        Assert.Equal(IngestAction.InsertNew, decision.Action);
        Assert.True(decision.Process);
        Assert.Equal(MessageStatus.New, decision.Status);
    }

    [Fact]
    public async Task A_new_source_id_on_a_paused_route_is_inserted_and_held()
    {
        var decision = await Build(enabled: false).DecideAsync(Address, "cust-1");

        Assert.Equal(IngestAction.InsertNew, decision.Action);
        Assert.False(decision.Process);
        Assert.Equal(MessageStatus.Held, decision.Status);
    }

    [Theory]
    [InlineData(MessageStatus.Processing)]
    [InlineData(MessageStatus.Completed)]
    [InlineData(MessageStatus.Failed)]
    [InlineData(MessageStatus.DeadLetter)]
    [InlineData(MessageStatus.AwaitingD365)]
    [InlineData(MessageStatus.Held)]
    [InlineData(MessageStatus.RetryScheduled)]
    public async Task A_source_id_past_ingest_is_rejected_as_a_duplicate(int status)
    {
        var gate = Build(existing: new ExistingMessage("4242", status));

        var decision = await gate.DecideAsync(Address, "cust-1");

        Assert.Equal(IngestAction.RejectDuplicate, decision.Action);
        Assert.False(decision.Process);
        Assert.Contains("4242", decision.Reason);
    }

    [Fact]
    public async Task Duplicate_rejection_applies_while_a_route_is_held()
    {
        // A pause window is exactly when a re-sending source piles up duplicates.
        var gate = Build(enabled: false, existing: new ExistingMessage("4242", MessageStatus.Held));

        var decision = await gate.DecideAsync(Address, "cust-1");

        Assert.Equal(IngestAction.RejectDuplicate, decision.Action);
    }

    [Fact]
    public async Task A_duplicate_costs_no_backlog_probe()
    {
        // Rejecting needs neither the orchestration type nor the backlog, so it must not pay for them.
        var backlog = new StubBacklog(false);
        var gate = Build(existing: new ExistingMessage("4242", MessageStatus.Completed), backlog: backlog);

        await gate.DecideAsync(Address, "cust-1");

        Assert.Equal(0, backlog.Calls);
    }

    [Fact]
    public async Task An_unprocessed_row_is_re_driven_rather_than_duplicated()
    {
        // A crash between insert and dispatch must not strand the message, nor create a second copy.
        var gate = Build(existing: new ExistingMessage("77", MessageStatus.New));

        var decision = await gate.DecideAsync(Address, "cust-1");

        Assert.Equal(IngestAction.RedriveExisting, decision.Action);
        Assert.Equal("77", decision.ExistingMessageId);
        Assert.True(decision.Process);
    }

    [Fact]
    public async Task An_unprocessed_row_on_a_paused_route_becomes_held()
    {
        // Left at New it would sit outside the release sweep's predicate and never drain.
        var gate = Build(enabled: false, existing: new ExistingMessage("77", MessageStatus.New));

        var decision = await gate.DecideAsync(Address, "cust-1");

        Assert.Equal(IngestAction.RedriveExisting, decision.Action);
        Assert.False(decision.Process);
        Assert.Equal(MessageStatus.Held, decision.Status);
    }

    [Fact]
    public async Task An_unprocessed_row_on_a_draining_route_becomes_held()
    {
        var gate = Build(hasBacklog: true, existing: new ExistingMessage("77", MessageStatus.New));

        var decision = await gate.DecideAsync(Address, "cust-1");

        Assert.Equal(IngestAction.RedriveExisting, decision.Action);
        Assert.Equal(MessageStatus.Held, decision.Status);
    }

    [Fact]
    public async Task The_dedupe_key_is_what_is_looked_up()
    {
        var duplicates = new StubDuplicates(null);
        var gate = new IngestGate(new HoldGate(new StubResolver(true), new StubBacklog(false)), duplicates);

        await gate.DecideAsync(Address, "operation-99");

        Assert.Equal("operation-99", duplicates.LastKey);
    }

    [Fact]
    public async Task An_absent_dedupe_key_skips_the_check_entirely()
    {
        // A source with nothing stable to key on must not be deduplicated. Inventing a key from the
        // business record is exactly the bug this replaced.
        var duplicates = new StubDuplicates(new ExistingMessage("1", MessageStatus.Completed));
        var gate = new IngestGate(new HoldGate(new StubResolver(true), new StubBacklog(false)), duplicates);

        var decision = await gate.DecideAsync(Address, null);

        Assert.Equal(IngestAction.InsertNew, decision.Action);
        Assert.Equal(0, duplicates.Calls);
    }

    [Fact]
    public async Task A_second_change_to_the_same_record_is_not_a_duplicate()
    {
        // Regression: dedupe was keyed on SOURCEID, which holds the business record key, so an
        // update to an already-processed customer was rejected as a duplicate of its own create.
        // Deployed, every record would have been processed once and every later change discarded.
        var duplicates = new StubDuplicates(null);
        var gate = new IngestGate(new HoldGate(new StubResolver(true), new StubBacklog(false)), duplicates);

        var create = await gate.DecideAsync(Address, "operation-create");
        var update = await gate.DecideAsync(Address, "operation-update");

        Assert.Equal(IngestAction.InsertNew, create.Action);
        Assert.Equal(IngestAction.InsertNew, update.Action);
    }
}
