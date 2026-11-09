using ARM.Integration.Core.Helpers;
using ARM.Integration.Core.Model;
using ARM.Integration.EIM.Orchestration;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Wilson.Integration.Orchestrations;

/// <summary>
/// The outcome D365 reports from <c>WTC_CaptureSalesOrderService/CreateSalesOrder</c>.
/// </summary>
/// <param name="Success">Whether the order was created, or had already been created for this PBLId.</param>
/// <param name="PBLId">The PBL id D365 processed.</param>
/// <param name="SalesId">The sales order id (the last one when the order produced several).</param>
/// <param name="SalesIds">Every sales order id created for the order; empty on failure.</param>
/// <param name="AlreadyProcessed">True when D365 found the PBLId already processed and returned its existing sales orders.</param>
/// <param name="ErrorMessage">The D365 error text when <paramref name="Success"/> is false.</param>
public sealed record CaptureSalesOrderServiceResult(
    bool Success,
    string? PBLId,
    string? SalesId,
    IReadOnlyList<string> SalesIds,
    bool AlreadyProcessed,
    string? ErrorMessage);

/// <summary>
/// Pure helpers for the Capture sales-order submit to the D365 custom service
/// <c>WTC_CaptureSalesOrderService/CreateSalesOrder</c>: request build, result parsing, the reply
/// envelope for Capture, and which failures earn a Failed reply. Kept free of I/O so they can be
/// tested without D365 or Service Bus.
/// </summary>
public static class CaptureSalesOrderService
{
    /// <summary>The D365 service operation that creates a sales order synchronously and idempotently on PBLId.</summary>
    public const string ServiceMethod =
        "api/services/WTC_Integration/WTC_CaptureSalesOrderService/CreateSalesOrder";

    private const string DataAreaIdField = "dataAreaId";

    private static readonly JsonSerializerOptions ResultOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Builds the <c>CreateSalesOrder</c> request body from a Capture envelope:
    /// <c>{ "CompanyId": Data.dataAreaId, "SalesOrder": Data without dataAreaId }</c>.
    /// The order is forwarded verbatim, with no field mapping.
    /// </summary>
    /// <exception cref="JsonException">
    /// The payload is not a JSON object, <c>Data</c> is missing, or <c>Data.dataAreaId</c> is missing or blank.
    /// </exception>
    public static (string? SourceId, string? PBLId, string CompanyId, string RequestJson) BuildRequest(string payload)
    {
        var root = JsonNode.Parse(payload) as JsonObject
            ?? throw new JsonException("Payload is not a JSON object.");

        var order = root["Data"] as JsonObject
            ?? throw new JsonException("Envelope 'Data' is missing.");

        var companyId = AsString(order[DataAreaIdField]);
        if (string.IsNullOrWhiteSpace(companyId))
            throw new JsonException("Envelope Data.dataAreaId is missing.");

        // dataAreaId travels as CompanyId; D365 runs the service under changecompany(CompanyId).
        order.Remove(DataAreaIdField);

        // Detach Data from the envelope so it can be re-parented under SalesOrder unchanged.
        root.Remove("Data");

        var request = new JsonObject
        {
            ["CompanyId"] = companyId,
            ["SalesOrder"] = order,
        };

        return (AsString(root["SourceId"]), AsString(order["PBLId"]), companyId, request.ToJsonString());
    }

    /// <summary>
    /// Parses the <c>CreateSalesOrder</c> response. Member names match case-insensitively; unknown
    /// members (such as <c>$id</c>) are ignored.
    /// </summary>
    /// <exception cref="JsonException">
    /// The body is not a JSON object, has no <c>Success</c> member, or reports success without a <c>SalesId</c>.
    /// </exception>
    public static CaptureSalesOrderServiceResult ParseResult(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            throw new JsonException("Unrecognised D365 response: the body is empty.");

        ServiceResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ServiceResponse>(response, ResultOptions);
        }
        catch (JsonException ex)
        {
            throw new JsonException($"Unrecognised D365 response: {ex.Message}", ex);
        }

        if (parsed?.Success is not { } success)
            throw new JsonException("Unrecognised D365 response: no Success member.");

        if (success && string.IsNullOrWhiteSpace(parsed.SalesId))
            throw new JsonException("D365 reported success without a SalesId.");

        return new CaptureSalesOrderServiceResult(
            success,
            parsed.PBLId,
            parsed.SalesId,
            parsed.SalesIds ?? [],
            parsed.AlreadyProcessed ?? false,
            parsed.ErrorMessage);
    }

    /// <summary>
    /// Builds the reply sent to Capture on the message's response queue. Same envelope shape Capture
    /// already receives from the business-event reconcile, with <c>Action = "Reconcile"</c>.
    /// </summary>
    /// <param name="message">The queue message being processed.</param>
    /// <param name="sourceId">The envelope SourceId; falls back to the queue message's SourceId.</param>
    /// <param name="status">"Success" or "Failed".</param>
    /// <param name="statusMessage">The status text (the D365 error text on failure).</param>
    /// <param name="result">The parsed D365 result; when it is a success, Data carries the sales ids.</param>
    /// <param name="pblId">The PBLId from the request, used when the result does not carry one.</param>
    public static OutboundMessageEnvelope<JsonObject> BuildReply(
        QueueMessageContract message,
        string? sourceId,
        string status,
        string? statusMessage,
        CaptureSalesOrderServiceResult? result,
        string? pblId)
    {
        ArgumentNullException.ThrowIfNull(message);

        var replyPblId = string.IsNullOrWhiteSpace(result?.PBLId) ? pblId : result.PBLId;

        var data = result is { Success: true }
            ? new JsonObject
            {
                ["SalesId"] = result.SalesId,
                ["SalesIds"] = new JsonArray(result.SalesIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                ["PBLId"] = replyPblId,
                ["AlreadyProcessed"] = result.AlreadyProcessed,
            }
            : new JsonObject { ["PBLId"] = replyPblId };

        return new OutboundMessageEnvelope<JsonObject>
        {
            SourceId = string.IsNullOrWhiteSpace(sourceId) ? message.SourceId : sourceId,
            Action = "Reconcile",
            Entity = string.IsNullOrWhiteSpace(message.SourceEntity)
                ? message.DestinationEntity
                : message.SourceEntity,
            Status = status,
            StatusMessage = statusMessage,
            Data = data,
        };
    }

    /// <summary>
    /// Whether a failure is a definite terminal answer about the order that earns a Failed reply: a
    /// D365 rejection, an unparsable request or response, or a D365 HTTP status that retrying cannot
    /// fix. Walks the inner-exception chain like <c>DefaultFailureClassifier</c>: the first decisive
    /// link wins, so a setup failure (401/403/404) wrapping, say, a <see cref="JsonException"/> still
    /// earns no reply.
    /// </summary>
    /// <remarks>
    /// Returns false (no reply) for:
    /// <list type="bullet">
    /// <item>Transient failures (408, 429, 5xx except 501/505, timeouts, connection errors): they are
    /// retried, and the retry sends the reply.</item>
    /// <item>A <see cref="D365FSCException"/> whose <see cref="D365FSCException.ODataErrorCode"/> is in
    /// <paramref name="retryableODataErrorCodes"/>: the classifier retries it too.</item>
    /// <item>D365 setup failures, a <see cref="D365FSCException"/> with 401, 403 or 404 (credentials,
    /// privilege, service not deployed). The message still ends at status 3 through
    /// <c>DefaultFailureClassifier</c>; once the setup is fixed, the reprocess sends the only reply, so
    /// Capture never sees "Failed" followed by "Success" for one order.</item>
    /// </list>
    /// </remarks>
    /// <param name="exception">The failure raised while building the request, calling D365 or reading its result.</param>
    /// <param name="retryableODataErrorCodes">
    /// The configured <c>RetryableODataErrorCodes</c> (see <c>RetryOptionsFactory</c>); matched with the
    /// set's own comparer, which is case-insensitive for <c>RetryOptions</c>.
    /// </param>
    public static bool IsDefiniteTerminal(Exception exception, IReadOnlySet<string> retryableODataErrorCodes)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(retryableODataErrorCodes);

        return FindDecisiveLink(exception, retryableODataErrorCodes) is { } link
            && !(link is D365FSCException d365 && IsSetupFailureStatus(d365.StatusCode));
    }

    /// <summary>
    /// Whether a failure is a D365 setup failure (design D8): the first decisive link in the chain is
    /// a <see cref="D365FSCException"/> with 401, 403 or 404 (credentials, privilege, service not
    /// deployed). These end at status 3 with no Capture reply until the setup is fixed and the message
    /// is reprocessed, so the orchestration logs a dedicated <c>SetupFailure</c> step for alerting.
    /// </summary>
    /// <param name="exception">The failure raised while building the request, calling D365 or reading its result.</param>
    /// <param name="retryableODataErrorCodes">The configured <c>RetryableODataErrorCodes</c>, as for <see cref="IsDefiniteTerminal"/>.</param>
    /// <param name="status">The HTTP status of the deciding link when this returns true.</param>
    public static bool TryGetSetupFailure(
        Exception exception, IReadOnlySet<string> retryableODataErrorCodes, out HttpStatusCode status)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(retryableODataErrorCodes);

        if (FindDecisiveLink(exception, retryableODataErrorCodes) is D365FSCException d365
            && IsSetupFailureStatus(d365.StatusCode))
        {
            status = d365.StatusCode;
            return true;
        }

        status = default;
        return false;
    }

    /// <summary>
    /// The first link in the chain that gives a definite terminal answer, in the order
    /// <c>DefaultFailureClassifier</c> walks it: a rejection, a <see cref="JsonException"/>, a
    /// <see cref="MissingEntityKeyException"/>, or a <see cref="D365FSCException"/> with a
    /// non-retryable status (the same links <c>DefaultFailureClassifier.IsPositivelyTerminal</c> accepts). A <see cref="D365FSCException"/>
    /// with a retryable status or a configured retryable OData code is not decisive, and the walk
    /// goes on beneath it. Null when nothing is decisive (a transient failure).
    /// </summary>
    private static Exception? FindDecisiveLink(Exception exception, IReadOnlySet<string> retryableODataErrorCodes)
    {
        var pending = new Stack<Exception>();
        pending.Push(exception);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            if (current is D365ServiceRejectedException or JsonException or MissingEntityKeyException
                || current is D365FSCException d365
                    && !IsRetryableStatus(d365.StatusCode)
                    && !IsRetryableODataErrorCode(d365, retryableODataErrorCodes))
            {
                return current;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }

        return null;
    }

    // Integration setup problems, not order problems: no Failed reply (design D8).
    private static bool IsSetupFailureStatus(HttpStatusCode status) => status is
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound;

    // DefaultFailureClassifier retries these codes whatever the HTTP status.
    private static bool IsRetryableODataErrorCode(D365FSCException exception, IReadOnlySet<string> retryableCodes) =>
        exception.ODataErrorCode is not null && retryableCodes.Contains(exception.ODataErrorCode);

    // Same statuses DefaultFailureClassifier retries: 408, 429 and 5xx except 501/505.
    private static bool IsRetryableStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.RequestTimeout => true,
        HttpStatusCode.TooManyRequests => true,
        HttpStatusCode.NotImplemented => false,
        HttpStatusCode.HttpVersionNotSupported => false,
        _ => (int)status >= 500
    };

    private static string? AsString(JsonNode? node) =>
        node is JsonValue value
            ? value.TryGetValue<string>(out var text) ? text : value.ToJsonString()
            : null;

    /// <summary>The D365 result contract as received; nullable so a missing member can be detected.</summary>
    private sealed class ServiceResponse
    {
        [JsonPropertyName("Success")] public bool? Success { get; set; }
        [JsonPropertyName("PBLId")] public string? PBLId { get; set; }
        [JsonPropertyName("SalesId")] public string? SalesId { get; set; }
        [JsonPropertyName("SalesIds")] public List<string>? SalesIds { get; set; }
        [JsonPropertyName("AlreadyProcessed")] public bool? AlreadyProcessed { get; set; }
        [JsonPropertyName("ErrorMessage")] public string? ErrorMessage { get; set; }
    }
}
