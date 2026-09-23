using ARM.Integration.Dashboard.API.Services;
using ARM.Integration.EIM;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ARM.Integration.D365FSC.Gateway.Tests;

/// <summary>Runs only when EIM_TEST_DB names a database; never hardcodes a connection string.</summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(CleanupIntegrationTests.ConnectionString))
            Skip = "Set EIM_TEST_DB to a disposable database to run cleanup integration tests.";
    }
}

/// <summary>
/// Exercises the real batched SQL against a real database.
/// </summary>
/// <remarks>
/// CleanupAsync scopes only by status and age, so it would happily delete live rows. Every seeded
/// row is dated <see cref="PlantedOn"/> and every run uses <see cref="AncientDays"/>, putting the
/// cutoff around 2001 — no real row is that old, so a run can only match rows this fixture created.
/// Never point EIM_TEST_DB at an environment whose data matters.
/// </remarks>
[Collection("cleanup-integration")]
public sealed class CleanupIntegrationTests : IAsyncLifetime
{
    internal static readonly string? ConnectionString = Environment.GetEnvironmentVariable("EIM_TEST_DB");

    private static readonly DateTime PlantedOn = new(2000, 1, 1);
    private const int AncientDays = 9000;

    /// <summary>Written to ENVIRONMENT so teardown can find every planted row.</summary>
    private readonly string _marker = $"IT-{Guid.NewGuid():N}"[..10];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;

        // Log rows are matched on DETAIL, not by joining parents: an orphan has no parent left.
        await ExecuteAsync("""
            DELETE FROM dbo.MESSAGELOGDETAIL  WHERE DETAIL = @MARKER;
            DELETE FROM dbo.MESSAGEQUEUETABLE WHERE ENVIRONMENT = @MARKER;
            """);
    }

    private MessageQueueRepository Repository(int batchSize = 2000, int budgetSeconds = 60)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:EIMDB"] = ConnectionString,
            })
            .Build();

        return new MessageQueueRepository(config, NullLoggerFactory.Instance, batchSize, budgetSeconds);
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        cmd.Parameters.AddWithValue("@MARKER", _marker);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await conn.OpenAsync();
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        cmd.Parameters.AddWithValue("@MARKER", _marker);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await conn.OpenAsync();
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    /// <summary>Plants rows at the given status, each with three log detail rows.</summary>
    private Task SeedAsync(int status, int count) => ExecuteAsync("""
        INSERT INTO dbo.MESSAGEQUEUETABLE (ENVIRONMENT, STATUS, CREATEDDATETIME, SOURCESYSTEM)
        SELECT TOP (@COUNT) @MARKER, @STATUS, @CREATED, 'IntegrationTest'
        FROM sys.all_objects a CROSS JOIN sys.all_objects b;

        INSERT INTO dbo.MESSAGELOGDETAIL (MESSAGEID, NAME, DETAIL)
        SELECT m.ID, s.NAME, @MARKER
        FROM dbo.MESSAGEQUEUETABLE m
        CROSS JOIN (VALUES ('Processing'), ('MappedPayload'), ('Result')) AS s(NAME)
        WHERE m.ENVIRONMENT = @MARKER AND m.STATUS = @STATUS;
        """,
        ("@COUNT", count), ("@STATUS", status), ("@CREATED", PlantedOn));

    private Task<int> RowsAsync(int status) => ScalarAsync(
        "SELECT COUNT(*) FROM dbo.MESSAGEQUEUETABLE WHERE ENVIRONMENT = @MARKER AND STATUS = @STATUS",
        ("@STATUS", status));

    private Task<int> OrphanedLogRowsAsync() => ScalarAsync("""
        SELECT COUNT(*)
        FROM dbo.MESSAGELOGDETAIL d
        WHERE d.DETAIL = @MARKER
          AND NOT EXISTS (SELECT 1 FROM dbo.MESSAGEQUEUETABLE m WHERE m.ID = d.MESSAGEID)
        """);

    [IntegrationFact]
    public async Task Age_only_cleanup_deletes_terminal_rows()
    {
        await SeedAsync(MessageStatus.Completed, 5);
        await SeedAsync(MessageStatus.Failed, 4);

        var scope = CleanupEligibility.Resolve(status: null, acknowledged: false);
        var result = await Repository().CleanupAsync(scope.Statuses, AncientDays);

        Assert.Equal(0, await RowsAsync(MessageStatus.Completed));
        Assert.Equal(0, await RowsAsync(MessageStatus.Failed));
        Assert.Equal(0, result.Remaining);
        Assert.True(result.Deleted >= 9);
    }

    [IntegrationFact]
    public async Task Age_only_cleanup_spares_every_protected_status()
    {
        int[] protectedStatuses =
        [
            MessageStatus.New, MessageStatus.Processing, MessageStatus.DeadLetter,
            MessageStatus.AwaitingD365, MessageStatus.Held, MessageStatus.RetryScheduled,
        ];

        foreach (var status in protectedStatuses) await SeedAsync(status, 3);

        var scope = CleanupEligibility.Resolve(status: null, acknowledged: false);
        await Repository().CleanupAsync(scope.Statuses, AncientDays);

        foreach (var status in protectedStatuses)
            Assert.Equal(3, await RowsAsync(status));
    }

    /// <summary>The direct regression test for the OR to AND reversal.</summary>
    [IntegrationFact]
    public async Task Status_and_age_delete_the_intersection_not_the_union()
    {
        await SeedAsync(MessageStatus.Completed, 3);
        await SeedAsync(MessageStatus.Failed, 3);

        var scope = CleanupEligibility.Resolve(MessageStatus.Completed, acknowledged: false);
        await Repository().CleanupAsync(scope.Statuses, AncientDays);

        Assert.Equal(0, await RowsAsync(MessageStatus.Completed));
        // Under OR these would also have gone, because they are older than the cutoff.
        Assert.Equal(3, await RowsAsync(MessageStatus.Failed));
    }

    [IntegrationFact]
    public async Task A_completed_run_leaves_no_orphaned_log_details()
    {
        await SeedAsync(MessageStatus.Completed, 25);
        await SeedAsync(MessageStatus.Held, 2);

        var scope = CleanupEligibility.Resolve(status: null, acknowledged: false);
        await Repository(batchSize: 10).CleanupAsync(scope.Statuses, AncientDays);

        Assert.Equal(0, await OrphanedLogRowsAsync());
    }

    [IntegrationFact]
    public async Task An_interrupted_run_leaves_no_orphaned_log_details_and_resumes()
    {
        await SeedAsync(MessageStatus.Completed, 30);

        var scope = CleanupEligibility.Resolve(status: null, acknowledged: false);

        // A zero budget stops after one batch, which is what a real interruption looks like.
        var first = await Repository(batchSize: 10, budgetSeconds: 0).CleanupAsync(scope.Statuses, AncientDays);

        Assert.True(first.Remaining > 0);
        Assert.Equal(0, await OrphanedLogRowsAsync());

        var second = await Repository(batchSize: 10).CleanupAsync(scope.Statuses, AncientDays);

        Assert.Equal(0, second.Remaining);
        Assert.Equal(0, await RowsAsync(MessageStatus.Completed));
        Assert.Equal(0, await OrphanedLogRowsAsync());
    }

    [IntegrationFact]
    public async Task An_acknowledged_protected_status_does_not_touch_other_statuses()
    {
        await SeedAsync(MessageStatus.Held, 4);
        await SeedAsync(MessageStatus.RetryScheduled, 4);
        await SeedAsync(MessageStatus.Completed, 4);

        var scope = CleanupEligibility.Resolve(MessageStatus.Held, acknowledged: true);
        await Repository().CleanupAsync(scope.Statuses, AncientDays);

        Assert.Equal(0, await RowsAsync(MessageStatus.Held));
        Assert.Equal(4, await RowsAsync(MessageStatus.RetryScheduled));
        Assert.Equal(4, await RowsAsync(MessageStatus.Completed));
    }

    [IntegrationFact]
    public async Task A_filter_matching_nothing_deletes_nothing()
    {
        // Nothing is seeded at these statuses, so the run is a genuine zero-match rather than a
        // no-op produced by a cutoff that happens to exclude everything.
        await SeedAsync(MessageStatus.Held, 3);

        var scope = CleanupEligibility.Resolve(MessageStatus.Completed, acknowledged: false);
        var result = await Repository().CleanupAsync(scope.Statuses, AncientDays);

        Assert.Equal(0, result.Deleted);
        Assert.Equal(3, await RowsAsync(MessageStatus.Held));
    }
}
