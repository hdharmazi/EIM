using ARM.Integration.Core.Helpers;
using ARM.Integration.Core.Model;
using ARM.Integration.EIM.Orchestration;
using ARM.Integration.EIM.Retry;
using ARM.Integration.EIM.Utilities;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wilson.Integration.Orchestrations;

/// <summary>
/// Capture → D365 FSC: creates a sales order synchronously through the custom service
/// <c>WTC_CaptureSalesOrderService/CreateSalesOrder</c>.
///
/// The order body is forwarded as pass-through JSON (no field mapping): <c>dataAreaId</c> becomes
/// <c>CompanyId</c> and the rest of <c>Data</c> is sent as <c>SalesOrder</c>. D365 writes the PBL
/// records and the sales order(s) in one call and is idempotent on <c>PBLId</c>, so retries and
/// Dashboard reprocessing return the existing sales order(s) with <c>AlreadyProcessed = true</c>.
///
/// The message completes (status 2) with <c>DESTINATIONID = SalesId</c>, or fails as terminal with
/// <see cref="D365ServiceRejectedException"/> when D365 rejects the order. The reply to Capture is
/// sent from here, straight after the call: Success on completion, Failed only for definite terminal
/// errors about the order or the response, and none for transient errors (including the configured
/// <c>RetryableODataErrorCodes</c>), which are retried. D365 setup failures (HTTP 401, 403, 404:
/// credentials, privilege, service not deployed) also send no reply: the message still ends at
/// status 3, and the reprocess after the setup fix sends the only reply. Because Capture just waits
/// meanwhile, a setup failure also logs a dedicated <c>SetupFailure</c> error step (HTTP status and
/// PBLId) that the production alert watches. Sending the reply is best-effort and never changes the
/// outcome.
/// </summary>
public class SalesOrder_To_D365FSC : IOrchestration
{
    // Fallback for the setup-failure check only, when the retry settings cannot be read.
    private static readonly IReadOnlySet<string> NoRetryableCodes = new HashSet<string>();

    public async Task<OrchestrationResult> ProcessAsync(
        QueueMessageContract data,
        string connectionString,
        IOrchestrationLogger stepLog,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(stepLog);

        stepLog.Log("Processing", "Processing Messages");

        stepLog.Log("ResolveEnvironments", $"Source={data.SourceSystem}, Dest={data.DestinationSystem}, Env={data.Environment}");
        var sourceEnvironmentConnection = ResolveConnection(data.SourceSystem, data.Environment, connectionString);
        var destinationEnvironmentConnection = ResolveConnection(data.DestinationSystem, data.Environment, connectionString);

        const string method = CaptureSalesOrderService.ServiceMethod;
        string? sourceId = null;
        string? pblId = null;
        var step = "BuildRequest";
        string response;
        CaptureSalesOrderServiceResult result;

        try
        {
            var request = CaptureSalesOrderService.BuildRequest(data.Payload);
            sourceId = request.SourceId;
            pblId = request.PBLId;
            stepLog.Log("BuildRequest", $"CompanyId={request.CompanyId}, PBLId={pblId}");

            step = "CallService";
            stepLog.Log("CallService", method);
            response = await CallServiceAsync(destinationEnvironmentConnection, method, request.RequestJson, ct);
            stepLog.Log("CallService", "Succeeded");

            step = "Result";
            result = CaptureSalesOrderService.ParseResult(response);
            if (!result.Success)
                throw new D365ServiceRejectedException(method, result.PBLId ?? pblId, result.ErrorMessage);
        }
        catch (Exception ex)
        {
            var errorMessage = ExceptionHelper.GetMessages(ex);
            stepLog.LogError(step, errorMessage);

            var retryableCodes = TryGetRetryableODataErrorCodes(stepLog);

            // D8: a setup failure holds the reply back, so Capture just waits. This dedicated step is
            // what the go-live alert watches; transient retries never write it.
            if (CaptureSalesOrderService.TryGetSetupFailure(ex, retryableCodes ?? NoRetryableCodes, out var setupStatus))
            {
                stepLog.LogError("SetupFailure",
                    $"D365 returned {(int)setupStatus} for PBLId {pblId}; reply held until reprocess");
            }

            // Only a definite answer about the order earns a Failed reply. A transient failure is
            // retried and a setup failure (401/403/404) is reprocessed once fixed; that later run
            // replies, so Capture never sees "Failed" followed by "Success" for one order.
            if (retryableCodes is not null && CaptureSalesOrderService.IsDefiniteTerminal(ex, retryableCodes))
            {
                await TrySendReplyAsync(
                    data, sourceEnvironmentConnection,
                    CaptureSalesOrderService.BuildReply(data, sourceId, "Failed", errorMessage, null, pblId),
                    stepLog, ct);
            }
            else
            {
                stepLog.Log("SendResponse", "Skipped: no reply for this failure; a later run (retry or reprocess) sends it");
            }

            throw;
        }

        stepLog.Log("Result",
            $"SalesId={result.SalesId}, SalesIds={string.Join(",", result.SalesIds)}, AlreadyProcessed={result.AlreadyProcessed}");

        // The order exists in D365 now: a reply failure must not turn it into a failed message.
        await TrySendReplyAsync(
            data, sourceEnvironmentConnection,
            CaptureSalesOrderService.BuildReply(data, sourceId, "Success", null, result, pblId),
            stepLog, ct);

        return OrchestrationResult.Completed(response, result.SalesId);
    }

    /// <summary>Resolves a system's environment configuration JSON from the EIM database.</summary>
    protected virtual string ResolveConnection(string system, string environment, string connectionString)
        => EIMUtilities.GetEnvironment(system, environment, connectionString);

    /// <summary>POSTs the request body to the D365 service operation and returns the response body.</summary>
    protected virtual Task<string> CallServiceAsync(
        string destinationConnection, string method, string body, CancellationToken ct)
        => D365FSCServiceCollectionExtensions.CreateStandaloneClient(destinationConnection)
            .CallServiceAsync(method, body, ct);

    /// <summary>Sends the reply payload to the source system's response queue.</summary>
    protected virtual async Task SendReplyAsync(
        string sourceConnection, string queue, string payload, CancellationToken ct)
    {
        await using var sb = new ServiceBusHelper(sourceConnection);
        await sb.SendAsync(queue, payload, cancellationToken: ct);
    }

    /// <summary>
    /// The configured <c>RetryableODataErrorCodes</c>, read from the same settings the Functions host
    /// builds its <see cref="RetryOptions"/> and <c>DefaultFailureClassifier</c> from.
    /// </summary>
    protected virtual IReadOnlySet<string> GetRetryableODataErrorCodes()
        => RetryOptionsFactory.FromEnvironment().RetryableODataErrorCodes;

    /// <summary>The configured retryable OData codes, or null (logged) when the settings cannot be read.</summary>
    private IReadOnlySet<string>? TryGetRetryableODataErrorCodes(IOrchestrationLogger stepLog)
    {
        try
        {
            return GetRetryableODataErrorCodes();
        }
        catch (Exception settingsEx)
        {
            // Never let a settings fault mask the original failure. Holding the reply back is the safe
            // side: the retry or the reprocess replies.
            stepLog.LogError("SendResponse", $"Reply held back: the retry settings cannot be read: {ExceptionHelper.GetMessages(settingsEx)}");
            return null;
        }
    }

    private async Task TrySendReplyAsync(
        QueueMessageContract data,
        string sourceConnection,
        OutboundMessageEnvelope<JsonObject> reply,
        IOrchestrationLogger stepLog,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(data.ResponseQueue))
        {
            stepLog.Log("SendResponse", "Skipped: no response queue");
            return;
        }

        try
        {
            stepLog.Log("SendResponse", $"Queue={data.ResponseQueue}, Status={reply.Status}");
            await SendReplyAsync(sourceConnection, data.ResponseQueue, JsonSerializer.Serialize(reply), ct);
            stepLog.Log("SendResponse", "Succeeded");
        }
        catch (Exception ex)
        {
            stepLog.LogError("SendResponse", $"Failed to send the {reply.Status} reply: {ExceptionHelper.GetMessages(ex)}");
        }
    }
}
