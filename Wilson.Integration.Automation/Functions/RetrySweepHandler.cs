using ARM.Integration.EIM;
using ARM.Integration.EIM.Orchestration;
using ARM.Integration.EIM.Retry;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Wilson.Integration.Automation.Functions;

/// <summary>
/// Retries messages that failed for a transient reason.
///
/// A retryable failure leaves a message at status 7 (Retry Scheduled) with an attempt count and a
/// next-attempt time. This timer claims a bounded batch whose time has passed and drives them.
///
/// Configuration:
///   "RetrySweepSchedule": "0 */1 * * * *"   ← CRON: every minute
///   "RetrySweepBatchSize": "100"
///   "RetryMaxAttempts", "RetryBaseDelaySeconds", "RetryMaxDelaySeconds", "RetryJitterFactor"
/// </summary>
public class RetrySweepHandler(
    ILoggerFactory loggerFactory,
    IReadOnlyDictionary<string, IOrchestration> orchestrations,
    IFailureClassifier failureClassifier,
    RetryScheduleCalculator retrySchedule,
    RetryOptions retryOptions)
{
    private readonly ILogger logger = loggerFactory.CreateLogger<RetrySweepHandler>();

    [Function(nameof(RetrySweepHandler))]
    public async Task Run([TimerTrigger("%RetrySweepSchedule%")] TimerInfo timer)
    {
        var connectionString = Environment.GetEnvironmentVariable("EIMDB")
            ?? throw new InvalidOperationException("EIMDB environment variable is not set.");

        var controller = new EIMController(
            connectionString, logger, orchestrations, failureClassifier, retrySchedule);

        var claimed = controller.ClaimDueRetries(
            retryOptions.SweepBatchSize, DateTime.UtcNow, "Claimed by retry sweep.");

        if (claimed.Count == 0)
        {
            logger.LogInformation("Retry sweep: nothing due.");
            return;
        }

        var succeeded = 0;
        var failed = 0;

        foreach (var messageId in claimed)
        {
            try
            {
                // ProcessMessageAsync records its own outcome, including scheduling a further
                // attempt or escalating once attempts are exhausted.
                await controller.ProcessMessageAsync(messageId);
                succeeded++;
            }
            catch (Exception ex)
            {
                // One poison message must not strand the rest of the claimed batch.
                failed++;
                logger.LogError(ex, "Retry sweep: message {MessageId} threw outside normal handling.", messageId);
            }
        }

        logger.LogInformation(
            "Retry sweep: claimed {Claimed}, completed {Succeeded}, errored {Failed}.",
            claimed.Count, succeeded, failed);
    }
}
