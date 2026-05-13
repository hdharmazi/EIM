using ARM.Integration.Core.Helpers;
using ARM.Integration.Core.Model;
using ARM.Integration.EIM.Utilities;
using System.Text.Json;

namespace ARM.Integration.EIM.Orchestration
{
    /// <summary>
    /// Orchestrates the processing of Capture customer messages and performs Create, Update, or Delete
    /// operations against D365 Finance &amp; Supply Chain via the CustomersV3 entity.
    /// Sends a response message back to the source system's Service Bus queue upon completion.
    /// </summary>
    public class CustomerAddress_To_D365FSC
    {
        /// <summary>
        /// Processes an EIM queue message by deserializing the payload into a Capture customer context,
        /// performing the requested action (New, Update, or Delete) against D365 Finance &amp; Supply Chain
        /// via the CustomersV3 entity, and sending a response message back to the source system's Service Bus queue.
        /// </summary>
        /// <param name="eimMessages">The serialized <see cref="QueueMessageContract"/> JSON string from the queue.</param>
        /// <param name="logger">The logger instance used for diagnostic logging.</param>
        /// <returns>
        /// A JSON string representing the <see cref="CaptureCustomerMessageContract"/> response payload on success,
        /// or <c>"unsupported action"</c> if the action type is not recognized.
        /// </returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="eimMessages"/> is null or whitespace.</exception>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="logger"/> is null.</exception>
        /// <exception cref="JsonException">Thrown when deserialization of the message or payload fails.</exception>
        public async Task<string> ProcessAsync(string eimMessages, string connectionString, IOrchestrationLogger stepLog)
        {
            if (string.IsNullOrWhiteSpace(eimMessages))
                throw new ArgumentException("Input message cannot be null or empty.", nameof(eimMessages));
            ArgumentNullException.ThrowIfNull(stepLog);

            stepLog?.Log("Processing", "Processing Messages");

            QueueMessageContract data;

            try
            {
                stepLog?.Log("DeserializeMessage");
                data = JsonSerializer.Deserialize<QueueMessageContract>(eimMessages)
                    ?? throw new JsonException("Deserialized data is null.");
            }
            catch (JsonException ex)
            {
                stepLog?.LogError("DeserializeMessage", ex.Message);
                throw;
            }

            string result = string.Empty;
/*
            try
            {
                stepLog?.Log("ResolveEnvironments", $"Source={data.SourceSystem}, Dest={data.DestinationSystem}, Env={data.Environment}");
                var sourceEnvironmentConnection = EIMUtilities.GetEnvironment(
                 data.SourceSystem, data.Environment, connectionString);

                var destinationEnvironmentConnection = EIMUtilities.GetEnvironment(
                    data.DestinationSystem, data.Environment, connectionString);

                // Deserialize the payload to get the Capture customer context
                var context = JsonSerializer.Deserialize<CaptureCustomerMessageContract>(data.Payload)
                    ?? throw new JsonException("context is null.");

                stepLog?.Log("DeserializePayload", $"Action={context.Action}, Account={context.CustomerAccount}, DataArea={context.DataAreaId}");

                // Map from CaptureCustomerMessageContract to D365FSC_CustomersV3
                var messageContract = new CustomersEntity
                {
                    DataAreaId = context.DataAreaId,
                    CustomerAccount = context.CustomerAccount,
                    OrganizationName = context.OrganizationName,
                    NameAlias = context.NameAlias,
                    CustomerGroupId = context.CustomerGroupId,
                    EmployeeResponsibleNumber = context.EmployeeResponsibleNumber,
                    Blocked = context.Blocked,
                    DepositRequired = context.DepositRequired,
                    DotNumber = context.DotNumber,
                    TaxStatus = context.TaxStatus,
                    PartyNumber = context.PartyNumber,
                    SalesCurrencyCode = "USD" // Defaulting, to USD, adjust as necessary"
                };

                // Call D365 client to perform the requested action
                var d365Client = D365FSCServiceCollectionExtensions.CreateStandaloneClient(destinationEnvironmentConnection);
                string actionString = context.Action;
                var responseMessageContract = new CaptureCustomerMessageContract();

                if (actionString.Equals("New", StringComparison.OrdinalIgnoreCase))
                {
                    stepLog?.Log("CreateCustomer", $"DataAreaId={messageContract.DataAreaId}, Account={messageContract.CustomerAccount}");
                    result = await d365Client.CreateRawAsync("CustomersV3", JsonSerializer.Serialize(messageContract));
                    stepLog?.Log("CreateCustomer", "Succeeded");

                    var resultJson = JsonSerializer.Deserialize<CustomersEntity>(result)
                   ?? throw new JsonException("context is null.");

                    responseMessageContract.SourceId    = context.SourceId;
                    responseMessageContract.Action = actionString;
                    responseMessageContract.Status = "Success";
                    responseMessageContract.PartyNumber = resultJson.PartyNumber;
                    responseMessageContract.CustomerAccount = resultJson.CustomerAccount;
                    responseMessageContract.DataAreaId = resultJson.DataAreaId;
                    responseMessageContract.OrganizationName = resultJson.OrganizationName;
                    responseMessageContract.NameAlias = resultJson.NameAlias;
                    responseMessageContract.CustomerGroupId = resultJson.CustomerGroupId;
                    responseMessageContract.EmployeeResponsibleNumber = resultJson.EmployeeResponsibleNumber;
                    responseMessageContract.Blocked = resultJson.Blocked;
                    responseMessageContract.DepositRequired = resultJson.DepositRequired;
                    //responseMessageContract.DotNumber = resultJson.DotNumber;
                    //responseMessageContract.TaxStatus = resultJson.TaxStatus;
                }
                else if (actionString.Equals("Update", StringComparison.OrdinalIgnoreCase))
                {


                    var updateKey = EntityKey.Create("dataAreaId", messageContract.DataAreaId)
                        .Add("CustomerAccount", messageContract.CustomerAccount);
                    stepLog?.Log("UpdateCustomer", $"Keys: {updateKey}");
                    await d365Client.UpdateRawAsync("CustomersV3", updateKey, JsonSerializer.Serialize(messageContract));
                    stepLog?.Log("UpdateCustomer", "Succeeded");

                    responseMessageContract.SourceId = context.SourceId;
                    responseMessageContract.Action = actionString;
                    responseMessageContract.Status = "Success";
                    responseMessageContract.PartyNumber = context.PartyNumber;
                    responseMessageContract.CustomerAccount = context.CustomerAccount;
                }
                else if (actionString.Equals("Delete", StringComparison.OrdinalIgnoreCase))
                {
                    var deleteKey = EntityKey.Create("dataAreaId", messageContract.DataAreaId)
                        .Add("CustomerAccount", messageContract.CustomerAccount);
                    stepLog?.Log("DeleteCustomer", $"Keys: {deleteKey}");
                    await d365Client.DeleteAsync("CustomersV3", deleteKey);
                    stepLog?.Log("DeleteCustomer", "Succeeded");

                    responseMessageContract.SourceId = context.SourceId;
                    responseMessageContract.Action = actionString;
                    responseMessageContract.Status = "Success";
                    responseMessageContract.PartyNumber = context.PartyNumber;
                    responseMessageContract.CustomerAccount = context.CustomerAccount;
                }
                else
                {
                    stepLog?.LogWarning("UnsupportedAction", context.Action);
                    return "unsupported action";
                }

                var payload = JsonSerializer.Serialize(responseMessageContract);
                stepLog?.Log("SendResponse", $"Queue={data.ResponseQueue}");
                await using var sb = new ServiceBusHelper(sourceEnvironmentConnection);

                var responsequeue = data.ResponseQueue;
                await sb.SendAsync(responsequeue,payload);
                stepLog?.Log("SendResponse", "Succeeded");
                result = payload;
                stepLog?.Log("Result", result);
            }

            catch (Exception ex)
            {
                var message = ExceptionHelper.GetMessages(ex);
                stepLog?.LogError("Error", message);
                throw;
            }
*/
            return result;
        }
    }
}
