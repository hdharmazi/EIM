using ARM.Integration.EIM.Orchestration;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Wilson.Integration.Automation.Inbound;

namespace Wilson.Integration.Automation.Functions;

/// <summary>
/// Consumes D365 F&amp;SC business events from the generic business-event queue and hands each one
/// to <see cref="BusinessEventReconciler"/>, which reconciles the status of the previously
/// submitted asynchronous message.
/// </summary>
public class QueueMessageQueueBusinessEventHandler(
    ILoggerFactory loggerFactory,
    IReadOnlyDictionary<string, IOrchestration> orchestrations)
{
    private readonly BusinessEventReconciler reconciler = new(
        loggerFactory.CreateLogger<QueueMessageQueueBusinessEventHandler>(), orchestrations);

    [Function(nameof(QueueMessageQueueBusinessEventHandler))]
    public async Task Run(
        [ServiceBusTrigger("%D365BusinessEventQueue%", Connection = "ServiceBus")]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions)
    {
        await reconciler.ProcessAsync(message, messageActions);
    }
}
