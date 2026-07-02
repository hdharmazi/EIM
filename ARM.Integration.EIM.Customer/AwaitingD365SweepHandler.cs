using ARM.Integration.EIM;
using ARM.Integration.EIM.Orchestration;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace ARM.Integration.EIM.Customer;

/// <summary>
/// Safety-net sweeper for asynchronous sales-order messages awaiting D365 confirmation.
///
/// A D365 message-processing business event normally reconciles a submitted message
/// (status 5 → 2/3). If that event never arrives (D365 outage, lost subscription, dropped message),
/// this timer escalates rows stuck at status 5 beyond a configurable timeout to status 4 (Dead Letter)
/// and logs an alert. Escalation is non-terminal: a late business event can still correct the row.
///
/// Configuration:
///   "AwaitingD365SweepSchedule": "0 */15 * * * *"   ← CRON: every 15 minutes
///   "AwaitingD365StuckTimeoutHours": "4"             ← escalate after 4 hours at status 5
/// </summary>
public class AwaitingD365SweepHandler(ILoggerFactory loggerFactory)
{
    private readonly ILogger logger = loggerFactory.CreateLogger<AwaitingD365SweepHandler>();

    private const int AwaitingStatus = 5;
    private const int DeadLetterStatus = 4;

    [Function(nameof(AwaitingD365SweepHandler))]
    public void Run([TimerTrigger("%AwaitingD365SweepSchedule%")] TimerInfo timer)
    {
        var connectionString = Environment.GetEnvironmentVariable("EIMDB")
            ?? throw new InvalidOperationException("EIMDB environment variable is not set.");

        var timeoutHours = double.TryParse(
            Environment.GetEnvironmentVariable("AwaitingD365StuckTimeoutHours"), out var h) ? h : 4;

        var cutoff = DateTime.UtcNow.AddHours(-timeoutHours);

        var controller = new EIMController(connectionString, logger, new Dictionary<string, IOrchestration>());

        var log = $"Escalated to Dead Letter: no D365 confirmation within {timeoutHours}h.";
        var escalatedIds = controller.EscalateStuckMessages(AwaitingStatus, DeadLetterStatus, cutoff, log);

        if (escalatedIds.Count == 0)
        {
            logger.LogInformation("AwaitingD365 sweep: no messages stuck beyond {hours}h.", timeoutHours);
            return;
        }

        // Alert: rows escalated because D365 never confirmed.
        logger.LogWarning(
            "AwaitingD365 sweep: escalated {count} message(s) to Dead Letter (no D365 confirmation within {hours}h). IDs: {ids}",
            escalatedIds.Count, timeoutHours, string.Join(", ", escalatedIds));
    }
}
