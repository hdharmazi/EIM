using ARM.Integration.EIM.Orchestration;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace ARM.Integration.EIM.Customer;

public class QueueCaptureCustomer(
    ILoggerFactory loggerFactory,
    IReadOnlyDictionary<string, IOrchestration> orchestrations)
{
    private readonly ILogger logger = loggerFactory.CreateLogger<QueueCaptureCustomer>();

    [Function(nameof(QueueCaptureCustomer))]
    public async Task Run(
        [ServiceBusTrigger("%CaptureToD365Queue%", Connection = "ServiceBus")]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions)
    {
        await QueueCaptureHandler.ProcessAsync(
            message, messageActions, logger, orchestrations,
            activeFlag: "CaptureToD365QueueActive",
            responseQueueSetting: "CaptureToD365QueueResponse");
    }
}