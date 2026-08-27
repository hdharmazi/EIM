using ARM.Integration.EIM;
using ARM.Integration.EIM.Orchestration;
using ARM.Integration.Core.Helpers;
using ARM.Integration.Core.Model;
using ARM.Integration.EIM.Utilities;
using System.Text.Json;

namespace Wilson.Integration.Orchestrations
{
    /// <summary>
    /// A sample orchestration that forwards an EIM queue message payload to a D365 Finance &amp; Supply Chain
    /// custom service endpoint (ARM_SysMessageService/SendMessageWithResponse) and returns the service response.
    /// Use as a template for building new orchestrations.
    /// </summary>
    public class Sample : IOrchestration
    {
        public async Task<OrchestrationResult> ProcessAsync(
            QueueMessageContract data,
            string connectionString,
            IOrchestrationLogger stepLog,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(data);
            ArgumentNullException.ThrowIfNull(stepLog);

            stepLog.Log("Processing", "Processing Messages");

            var destinationEnvironmentConnection = EIMUtilities.GetEnvironment(
                data.DestinationSystem, data.Environment, connectionString);

            var d365Client = D365FSCServiceCollectionExtensions.CreateStandaloneClient(destinationEnvironmentConnection);

            const string messageApi = "api/services/ARM_Integration/ARM_SysMessageService/SendMessageWithResponse";

            string result;
            try
            {
                stepLog.Log("CallService", messageApi);
                result = await d365Client.CallServiceAsync(messageApi, data.Payload, ct);
                stepLog.Log("CallService", "Succeeded");
            }
            catch (Exception ex)
            {
                stepLog.LogError("CallService", ex.Message);
                throw;
            }

            return OrchestrationResult.Completed(result);
        }
    }
}
