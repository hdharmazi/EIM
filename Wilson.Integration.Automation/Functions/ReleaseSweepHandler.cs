using ARM.Integration.EIM;
using ARM.Integration.EIM.Orchestration;
using ARM.Integration.EIM.Retry;
using ARM.Integration.EIM.Routing;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Wilson.Integration.Automation.Functions;

/// <summary>
/// Drains held messages for routes that have been resumed.
///
/// A paused route captures messages at status 6 (Held). This timer releases them oldest-first,
/// a bounded batch per route per run, and stops holding new traffic once a route's backlog clears.
///
/// Configuration:
///   "ReleaseSweepSchedule":  "0 */1 * * * *"   ← CRON: every minute
///   "ReleaseSweepBatchSize": "100"
/// </summary>
public class ReleaseSweepHandler(
    ILoggerFactory loggerFactory,
    IReadOnlyDictionary<string, IOrchestration> orchestrations,
    IFailureClassifier failureClassifier,
    RetryScheduleCalculator retrySchedule)
{
    private readonly ILogger logger = loggerFactory.CreateLogger<ReleaseSweepHandler>();

    [Function(nameof(ReleaseSweepHandler))]
    public async Task Run([TimerTrigger("%ReleaseSweepSchedule%")] TimerInfo timer)
    {
        var connectionString = Environment.GetEnvironmentVariable("EIMDB")
            ?? throw new InvalidOperationException("EIMDB environment variable is not set.");

        var batchSize = int.TryParse(
            Environment.GetEnvironmentVariable("ReleaseSweepBatchSize"), out var configured) && configured > 0
            ? configured
            : 100;

        var controller = new EIMController(
            connectionString, logger, orchestrations, failureClassifier, retrySchedule);

        var sweep = new ReleaseSweep(
            new SqlReleaseSweepStore(connectionString),
            new ControllerMessageProcessor(controller),
            logger);

        await sweep.RunAsync(batchSize);
    }

    private sealed class ControllerMessageProcessor(EIMController controller) : IMessageProcessor
    {
        public Task ProcessAsync(string messageId, CancellationToken cancellationToken = default)
            => controller.ProcessMessageAsync(messageId, cancellationToken);
    }
}
