using ARM.Integration.EIM.Orchestration;
using ARM.Integration.EIM.Retry;
using ARM.Integration.EIM.Routing;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Wilson.Integration.Automation.Outbound;

namespace Wilson.Integration.Automation.Functions;

/// <summary>
/// Receives D365 FSC data events from Service Bus and hands each one to
/// <see cref="DataEventProcessor"/>, which routes, filters and settles it.
/// </summary>
public class QueueDataEventsHandler(
    ILoggerFactory loggerFactory,
    IReadOnlyDictionary<string, IOrchestration> orchestrations,
    IngestGate gate,
    IFailureClassifier failureClassifier,
    RetryScheduleCalculator retrySchedule)
{
    private readonly DataEventProcessor processor = new(
        loggerFactory.CreateLogger<QueueDataEventsHandler>(), orchestrations, gate, failureClassifier, retrySchedule);

    [Function(nameof(QueueDataEventsHandler))]
    public async Task Run(
        [ServiceBusTrigger("%D365FscDataEventQueue%", Connection = "ServiceBus")]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions)
    {
        await processor.ProcessAsync(message, messageActions);
    }
}
