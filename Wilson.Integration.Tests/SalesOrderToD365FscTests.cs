using ARM.Integration.Core.Helpers;
using ARM.Integration.Core.Model;
using ARM.Integration.EIM;
using ARM.Integration.EIM.Orchestration;
using ARM.Integration.EIM.Retry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wilson.Integration.Orchestrations;
using Xunit;

namespace Wilson.Integration.Tests;

/// <summary>
/// Spec capture-salesorder-submit: the synchronous submit to
/// <c>WTC_CaptureSalesOrderService/CreateSalesOrder</c>, its result mapping and the Capture reply.
/// </summary>
public class SalesOrderToD365FscTests
{
    private const string CreateSalesOrder = "api/services/WTC_Integration/WTC_CaptureSalesOrderService/CreateSalesOrder";
    private const string SourceConnection = "CAPTURE-connection";
    private const string DestinationConnection = "D365FSC-connection";
    private const string ResponseQueue = "salesorder-response";

    private const string CreatedResponse =
        """{"$id":"1","Success":true,"PBLId":"PBL-1","SalesId":"200007","SalesIds":["200007"],"AlreadyProcessed":false,"ErrorMessage":""}""";

    // Tests never read RetryableODataErrorCodes from the process environment: the helper takes the set
    // and the orchestration double overrides the loader.
    private static readonly IReadOnlySet<string> NoRetryableCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static string SampleEnvelope() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "SalesOrderCapture.json"));

    private static string SmallEnvelope(Action<JsonObject>? alterData = null)
    {
        var data = new JsonObject
        {
            ["dataAreaId"] = "WTC",
            ["PBLId"] = "PBL-1",
            ["CustAccount"] = "119637",
            ["Line"] = new JsonArray(
                new JsonObject { ["LineNum"] = 1, ["Pick"] = "G050175010102-", ["Qty"] = 1 },
                new JsonObject { ["LineNum"] = 2, ["Pick"] = "G050316-41FT", ["Qty"] = 1, ["Price"] = 55900 }),
            ["TradeAllowance"] = new JsonArray(),
        };
        alterData?.Invoke(data);

        return new JsonObject
        {
            ["SourceId"] = "CAP-1",
            ["Action"] = "New",
            ["Entity"] = "SalesOrderQueue",
            ["Data"] = data,
        }.ToJsonString();
    }

    private static QueueMessageContract Message(string? payload = null, string? responseQueue = ResponseQueue) => new()
    {
        Id = "1",
        Environment = "BAT",
        SourceId = "CAP-1",
        SourceSystem = "CAPTURE",
        SourceEntity = "SalesOrderQueue",
        DestinationSystem = "D365FSC",
        DestinationEntity = "SalesOrder",
        Type = "SalesOrder_To_D365FSC",
        ResponseQueue = responseQueue!,
        Payload = payload ?? SmallEnvelope(),
    };

    private static JsonObject Body(string requestJson) => JsonNode.Parse(requestJson)!.AsObject();

    private static JsonObject SalesOrderOf(string requestJson) =>
        Assert.IsType<JsonObject>(Body(requestJson)["SalesOrder"]);

    // ── Request build ────────────────────────────────────────────────────────

    // Scenario: Direct service request is constructed
    [Fact]
    public void Direct_service_request_is_constructed()
    {
        var (sourceId, pblId, companyId, requestJson) = CaptureSalesOrderService.BuildRequest(SmallEnvelope());

        Assert.Equal(CreateSalesOrder, CaptureSalesOrderService.ServiceMethod);
        Assert.Equal("CAP-1", sourceId);
        Assert.Equal("PBL-1", pblId);
        Assert.Equal("WTC", companyId);

        var body = Body(requestJson);
        Assert.Equal(new[] { "CompanyId", "SalesOrder" }, body.Select(p => p.Key).ToArray());
        Assert.Equal("WTC", (string?)body["CompanyId"]);

        // An object, not a JSON string: D365 binds it to WTC_CapturePBLTableContract.
        var order = SalesOrderOf(requestJson);
        Assert.Equal("PBL-1", (string?)order["PBLId"]);
        Assert.Equal(2, Assert.IsType<JsonArray>(order["Line"]).Count);
    }

    // Scenario: dataAreaId becomes company id and is removed from the order
    [Fact]
    public void DataAreaId_becomes_the_company_id_and_is_removed_from_the_order()
    {
        var (_, pblId, companyId, requestJson) = CaptureSalesOrderService.BuildRequest(SampleEnvelope());

        Assert.Equal("WTC", companyId);
        Assert.Equal("WT-2060037068", pblId);
        Assert.Equal("WTC", (string?)Body(requestJson)["CompanyId"]);
        Assert.False(SalesOrderOf(requestJson).ContainsKey("dataAreaId"));
    }

    // Scenario: Missing dataAreaId is terminal
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_dataAreaId_is_terminal(string? dataAreaId)
    {
        var payload = SmallEnvelope(data =>
        {
            if (dataAreaId is null) data.Remove("dataAreaId");
            else data["dataAreaId"] = dataAreaId;
        });

        var ex = Assert.Throws<JsonException>(() => CaptureSalesOrderService.BuildRequest(payload));

        Assert.Equal("Envelope Data.dataAreaId is missing.", ex.Message);
        Assert.True(CaptureSalesOrderService.IsDefiniteTerminal(ex, NoRetryableCodes));
        Assert.Equal(FailureDisposition.Terminal, new DefaultFailureClassifier(new RetryOptions()).Classify(ex));
    }

    [Fact]
    public void Missing_data_is_terminal()
    {
        var payload = """{"SourceId":"CAP-1","Action":"New","Entity":"SalesOrderQueue"}""";

        var ex = Assert.Throws<JsonException>(() => CaptureSalesOrderService.BuildRequest(payload));

        Assert.True(CaptureSalesOrderService.IsDefiniteTerminal(ex, NoRetryableCodes));
    }

    // Scenario: Unknown order fields are preserved
    [Fact]
    public void Unknown_order_fields_are_preserved()
    {
        var payload = SmallEnvelope(data =>
        {
            data["FutureField"] = "kept";
            data["FutureBlock"] = new JsonObject { ["Nested"] = 5637147904, ["Flag"] = "Yes" };
        });

        var order = SalesOrderOf(CaptureSalesOrderService.BuildRequest(payload).RequestJson);

        Assert.Equal("kept", (string?)order["FutureField"]);
        Assert.Equal("""{"Nested":5637147904,"Flag":"Yes"}""", order["FutureBlock"]!.ToJsonString());
    }

    // Scenarios: Nested collections are preserved; the order is forwarded with no field mapping
    [Fact]
    public void Nested_collections_and_every_field_are_preserved()
    {
        var source = JsonNode.Parse(SampleEnvelope())!["Data"]!.AsObject();

        var order = SalesOrderOf(CaptureSalesOrderService.BuildRequest(SampleEnvelope()).RequestJson);

        var lines = Assert.IsType<JsonArray>(order["Line"]);
        var tradeAllowances = Assert.IsType<JsonArray>(order["TradeAllowance"]);
        Assert.Equal(56, lines.Count);
        Assert.Single(tradeAllowances);
        Assert.True(JsonNode.DeepEquals(source["Line"], lines));
        Assert.True(JsonNode.DeepEquals(source["TradeAllowance"], tradeAllowances));

        // Pass-through: the order is exactly Data without dataAreaId.
        source.Remove("dataAreaId");
        Assert.True(JsonNode.DeepEquals(source, order));
    }

    // ── Result parsing ───────────────────────────────────────────────────────

    // Scenario: Created order completes the message (parse half); $id member ignored
    [Fact]
    public void Created_order_result_is_parsed_and_the_id_member_ignored()
    {
        var result = CaptureSalesOrderService.ParseResult(CreatedResponse);

        Assert.True(result.Success);
        Assert.Equal("PBL-1", result.PBLId);
        Assert.Equal("200007", result.SalesId);
        Assert.Equal(new[] { "200007" }, result.SalesIds);
        Assert.False(result.AlreadyProcessed);
    }

    // Scenario: Already-processed order completes the message (parse half)
    [Fact]
    public void Already_processed_result_is_parsed()
    {
        var result = CaptureSalesOrderService.ParseResult(
            """{"Success":true,"SalesId":"200007","AlreadyProcessed":true}""");

        Assert.True(result.Success);
        Assert.True(result.AlreadyProcessed);
        Assert.Equal("200007", result.SalesId);
        Assert.Empty(result.SalesIds);
    }

    [Fact]
    public void Multiple_sales_ids_are_kept_in_order()
    {
        var result = CaptureSalesOrderService.ParseResult(
            """{"Success":true,"PBLId":"PBL-1","SalesId":"200009","SalesIds":["200007","200008","200009"],"AlreadyProcessed":false}""");

        Assert.Equal(new[] { "200007", "200008", "200009" }, result.SalesIds);
        Assert.Equal("200009", result.SalesId);
    }

    [Fact]
    public void Member_names_match_case_insensitively()
    {
        var result = CaptureSalesOrderService.ParseResult(
            """{"success":true,"salesId":"200007","SALESIDS":["200007"],"alreadyprocessed":true,"pblid":"PBL-1"}""");

        Assert.True(result.Success);
        Assert.Equal("200007", result.SalesId);
        Assert.Equal(new[] { "200007" }, result.SalesIds);
        Assert.True(result.AlreadyProcessed);
        Assert.Equal("PBL-1", result.PBLId);
    }

    // Scenario: Rejected order fails as terminal (parse half)
    [Fact]
    public void Rejected_result_is_parsed()
    {
        var result = CaptureSalesOrderService.ParseResult(
            """{"Success":false,"PBLId":"PBL-1","SalesId":"","SalesIds":[],"AlreadyProcessed":false,"ErrorMessage":"Customer account C999 does not exist."}""");

        Assert.False(result.Success);
        Assert.Equal("Customer account C999 does not exist.", result.ErrorMessage);
        Assert.Equal("PBL-1", result.PBLId);
    }

    // Scenario: Unrecognised response fails as terminal
    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("<html>Bad gateway</html>")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"SalesId":"200007"}""")]
    [InlineData("""{"Success":"maybe"}""")]
    public void Unrecognised_response_fails_with_a_json_exception(string response)
    {
        var ex = Assert.Throws<JsonException>(() => CaptureSalesOrderService.ParseResult(response));

        Assert.True(CaptureSalesOrderService.IsDefiniteTerminal(ex, NoRetryableCodes));
    }

    [Theory]
    [InlineData("""{"Success":true,"SalesIds":[]}""")]
    [InlineData("""{"Success":true,"SalesId":"  "}""")]
    public void Success_without_a_sales_id_fails_with_a_json_exception(string response)
    {
        var ex = Assert.Throws<JsonException>(() => CaptureSalesOrderService.ParseResult(response));

        Assert.Equal("D365 reported success without a SalesId.", ex.Message);
    }

    // ── Failure triage for the Capture reply ─────────────────────────────────

    public static TheoryData<Exception> TerminalFailures() =>
    [
        new D365ServiceRejectedException(CreateSalesOrder, "PBL-1", "Route R1 is not valid."),
        new JsonException("bad"),
        new D365FSCException("bad request", HttpStatusCode.BadRequest),
        new D365FSCException("conflict", HttpStatusCode.Conflict),
        new D365FSCException("unprocessable", HttpStatusCode.UnprocessableEntity),
        new D365FSCException("not implemented", HttpStatusCode.NotImplemented),
        new D365FSCException("bad request", HttpStatusCode.BadRequest, "SomeUnlistedCode"),
        new InvalidOperationException("wrapped", new D365ServiceRejectedException(CreateSalesOrder, null, null)),
        new MissingEntityKeyException("PBLId"),
    ];

    // D8: integration setup problems. Terminal for the classifier (status 3), but no Capture reply.
    // The second value is the HTTP status the SetupFailure step reports.
    public static TheoryData<Exception, int> SetupFailures() => new()
    {
        { new D365FSCException("unauthorized", HttpStatusCode.Unauthorized), 401 },
        { new D365FSCException("forbidden", HttpStatusCode.Forbidden), 403 },
        { new D365FSCException("Operation not found", HttpStatusCode.NotFound), 404 },
        { new InvalidOperationException("wrapped", new D365FSCException("unauthorized", HttpStatusCode.Unauthorized)), 401 },
        { new AggregateException(new D365FSCException("forbidden", HttpStatusCode.Forbidden)), 403 },
        // First decisive link wins: the 401 decides, whatever it wraps.
        { new D365FSCException("unauthorized", HttpStatusCode.Unauthorized, inner: new JsonException("bad body")), 401 },
    };

    public static TheoryData<Exception> TransientFailures() =>
    [
        new D365FSCException("unavailable", HttpStatusCode.ServiceUnavailable),
        new D365FSCException("server error", HttpStatusCode.InternalServerError),
        new D365FSCException("timeout", HttpStatusCode.RequestTimeout),
        new D365FSCException("throttled", HttpStatusCode.TooManyRequests),
        new TimeoutException("timed out"),
        new TaskCanceledException("HttpClient.Timeout", new TimeoutException()),
        new HttpRequestException("connection reset"),
    ];

    [Theory]
    [MemberData(nameof(TerminalFailures))]
    public void Definite_terminal_failures_earn_a_failed_reply(Exception exception)
    {
        Assert.True(CaptureSalesOrderService.IsDefiniteTerminal(exception, NoRetryableCodes));
        Assert.Equal(FailureDisposition.Terminal, new DefaultFailureClassifier(new RetryOptions()).Classify(exception));
    }

    [Theory]
    [MemberData(nameof(TransientFailures))]
    public void Transient_failures_earn_no_reply(Exception exception)
    {
        Assert.False(CaptureSalesOrderService.IsDefiniteTerminal(exception, NoRetryableCodes));
        Assert.Equal(FailureDisposition.Retryable, new DefaultFailureClassifier(new RetryOptions()).Classify(exception));
    }

    // Scenario: Setup failure sends no reply (triage half)
    [Theory]
    [MemberData(nameof(SetupFailures))]
    public void Setup_failures_earn_no_reply_but_stay_terminal(Exception exception, int status)
    {
        Assert.False(CaptureSalesOrderService.IsDefiniteTerminal(exception, NoRetryableCodes));
        Assert.True(CaptureSalesOrderService.TryGetSetupFailure(exception, NoRetryableCodes, out var setupStatus));
        Assert.Equal(status, (int)setupStatus);
        Assert.Equal(FailureDisposition.Terminal, new DefaultFailureClassifier(new RetryOptions()).Classify(exception));
    }

    // Scenario: Setup failure wrapping another error still sends no reply (triage half). The classifier
    // decides on the first decisive link, and so does the reply triage, in both directions.
    [Fact]
    public void First_decisive_link_decides_between_setup_failure_and_failed_reply()
    {
        var classifier = new DefaultFailureClassifier(new RetryOptions());

        var setupOverJson = new D365FSCException("unauthorized", HttpStatusCode.Unauthorized, inner: new JsonException("bad body"));
        Assert.False(CaptureSalesOrderService.IsDefiniteTerminal(setupOverJson, NoRetryableCodes));
        Assert.True(CaptureSalesOrderService.TryGetSetupFailure(setupOverJson, NoRetryableCodes, out _));
        Assert.Equal(FailureDisposition.Terminal, classifier.Classify(setupOverJson));

        var jsonOverSetup = new JsonException("bad body", new D365FSCException("unauthorized", HttpStatusCode.Unauthorized));
        Assert.True(CaptureSalesOrderService.IsDefiniteTerminal(jsonOverSetup, NoRetryableCodes));
        Assert.False(CaptureSalesOrderService.TryGetSetupFailure(jsonOverSetup, NoRetryableCodes, out _));
        Assert.Equal(FailureDisposition.Terminal, classifier.Classify(jsonOverSetup));

        // A transient link is not decisive: the setup failure beneath it still decides.
        var transientOverSetup = new D365FSCException("unavailable", HttpStatusCode.ServiceUnavailable,
            inner: new D365FSCException("forbidden", HttpStatusCode.Forbidden));
        Assert.False(CaptureSalesOrderService.IsDefiniteTerminal(transientOverSetup, NoRetryableCodes));
        Assert.True(CaptureSalesOrderService.TryGetSetupFailure(transientOverSetup, NoRetryableCodes, out var status));
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal(FailureDisposition.Terminal, classifier.Classify(transientOverSetup));

        // A missing entity key reached before a 401 decides: Failed reply, no SetupFailure step.
        // MissingEntityKeyException takes no inner exception, so it sits above the 401 in walk order:
        // both walks visit an AggregateException's inner exceptions last-first.
        var missingKeyOverSetup = new AggregateException(
            new D365FSCException("unauthorized", HttpStatusCode.Unauthorized),
            new MissingEntityKeyException("PBLId"));
        Assert.True(CaptureSalesOrderService.IsDefiniteTerminal(missingKeyOverSetup, NoRetryableCodes));
        Assert.False(CaptureSalesOrderService.TryGetSetupFailure(missingKeyOverSetup, NoRetryableCodes, out _));
        Assert.Equal(FailureDisposition.Terminal, classifier.Classify(missingKeyOverSetup));
    }

    // Scenario: Other failures do not log a setup failure (triage half)
    [Theory]
    [MemberData(nameof(TerminalFailures))]
    [MemberData(nameof(TransientFailures))]
    public void Other_failures_are_not_setup_failures(Exception exception)
        => Assert.False(CaptureSalesOrderService.TryGetSetupFailure(exception, NoRetryableCodes, out _));

    [Fact]
    public void Setup_status_with_a_configured_retryable_odata_code_is_retried_not_a_setup_failure()
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DatabaseBusy" };
        var exception = new D365FSCException("busy", HttpStatusCode.Forbidden, "DatabaseBusy");

        Assert.False(CaptureSalesOrderService.TryGetSetupFailure(exception, codes, out _));
        Assert.False(CaptureSalesOrderService.IsDefiniteTerminal(exception, codes));
        Assert.Equal(FailureDisposition.Retryable,
            new DefaultFailureClassifier(new RetryOptions { RetryableODataErrorCodes = { "DatabaseBusy" } }).Classify(exception));
    }

    // Scenario: Configured retryable OData code sends no reply (triage half)
    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "DatabaseBusy")]
    [InlineData(HttpStatusCode.Conflict, "databasebusy")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "DATABASEBUSY")]
    public void Configured_retryable_odata_code_earns_no_reply(HttpStatusCode status, string odataErrorCode)
    {
        // Same source the host uses, fed from a dictionary instead of the process environment.
        var options = RetryOptionsFactory.Create(name =>
            name == RetryOptionsFactory.RetryableODataErrorCodesSetting ? "OtherCode;DatabaseBusy" : null);
        var exception = new D365FSCException("busy", status, odataErrorCode);

        Assert.False(CaptureSalesOrderService.IsDefiniteTerminal(exception, options.RetryableODataErrorCodes));
        Assert.Equal(FailureDisposition.Retryable, new DefaultFailureClassifier(options).Classify(exception));

        // Without the configuration the same failure is a definite answer and earns a Failed reply.
        Assert.True(CaptureSalesOrderService.IsDefiniteTerminal(exception, NoRetryableCodes));
    }

    [Fact]
    public void Retryable_odata_code_does_not_hide_a_terminal_inner_failure()
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DatabaseBusy" };
        var exception = new D365FSCException("busy", HttpStatusCode.BadRequest, "DatabaseBusy", inner: new JsonException("bad"));

        Assert.True(CaptureSalesOrderService.IsDefiniteTerminal(exception, codes));
        Assert.Equal(FailureDisposition.Terminal, new DefaultFailureClassifier(new RetryOptions { RetryableODataErrorCodes = { "DatabaseBusy" } }).Classify(exception));
    }

    // ── Reply envelope ───────────────────────────────────────────────────────

    [Fact]
    public void Failed_reply_carries_only_the_pbl_id()
    {
        var reply = CaptureSalesOrderService.BuildReply(Message(), null, "Failed", "Route R1 is not valid.", null, "PBL-1");

        Assert.Equal("CAP-1", reply.SourceId);
        Assert.Equal("Reconcile", reply.Action);
        Assert.Equal("SalesOrderQueue", reply.Entity);
        Assert.Equal("Failed", reply.Status);
        Assert.Equal("Route R1 is not valid.", reply.StatusMessage);
        Assert.Equal("""{"PBLId":"PBL-1"}""", reply.Data!.ToJsonString());
    }

    [Fact]
    public void Reply_entity_falls_back_to_the_destination_entity()
    {
        var message = Message();
        message.SourceEntity = " ";

        var reply = CaptureSalesOrderService.BuildReply(message, "CAP-1", "Failed", "x", null, null);

        Assert.Equal("SalesOrder", reply.Entity);
    }

    // ── Orchestration ────────────────────────────────────────────────────────

    // Scenarios: Created order completes the message; Success reply carries the sales id;
    // Message-queue service is no longer used
    [Fact]
    public async Task Created_order_completes_and_replies_success()
    {
        var sut = new TestableSalesOrder { Response = CreatedResponse };

        var result = await sut.ProcessAsync(Message(), "eimdb", new RecordingLogger());

        Assert.Equal(MessageStatus.Completed, result.Status);
        Assert.Equal("200007", result.DestinationId);
        Assert.Equal(CreatedResponse, result.Message);

        var call = Assert.Single(sut.Calls);
        Assert.Equal(DestinationConnection, call.Connection);
        Assert.Equal(CreateSalesOrder, call.Method);
        Assert.DoesNotContain("SendMessageWithResponse", call.Method);
        Assert.Equal("WTC", (string?)Body(call.Body)["CompanyId"]);

        var reply = Assert.Single(sut.Replies);
        Assert.Equal(SourceConnection, reply.Connection);
        Assert.Equal(ResponseQueue, reply.Queue);

        var envelope = JsonNode.Parse(reply.Payload)!.AsObject();
        Assert.Equal("CAP-1", (string?)envelope["SourceId"]);
        Assert.Equal("Reconcile", (string?)envelope["Action"]);
        Assert.Equal("SalesOrderQueue", (string?)envelope["Entity"]);
        Assert.Equal("Success", (string?)envelope["Status"]);
        Assert.False(envelope.ContainsKey("StatusMessage"));
        Assert.Equal(
            """{"SalesId":"200007","SalesIds":["200007"],"PBLId":"PBL-1","AlreadyProcessed":false}""",
            envelope["Data"]!.ToJsonString());
    }

    // Scenario: Already-processed order completes the message
    [Fact]
    public async Task Already_processed_order_completes_and_says_so_in_the_reply()
    {
        var sut = new TestableSalesOrder { Response = """{"Success":true,"SalesId":"200007","AlreadyProcessed":true}""" };

        var result = await sut.ProcessAsync(Message(), "eimdb", new RecordingLogger());

        Assert.Equal(MessageStatus.Completed, result.Status);
        Assert.Equal("200007", result.DestinationId);

        var data = JsonNode.Parse(Assert.Single(sut.Replies).Payload)!["Data"]!;
        Assert.True((bool)data["AlreadyProcessed"]!);
        Assert.Equal("PBL-1", (string?)data["PBLId"]);
    }

    // Scenarios: Rejected order fails as terminal; Rejection reply carries the D365 error
    [Fact]
    public async Task Rejected_order_throws_and_replies_failed_with_the_d365_error()
    {
        var sut = new TestableSalesOrder
        {
            Response = """{"Success":false,"PBLId":"PBL-1","SalesIds":[],"AlreadyProcessed":false,"ErrorMessage":"Route R1 is not valid."}""",
        };
        var log = new RecordingLogger();

        var ex = await Assert.ThrowsAsync<D365ServiceRejectedException>(
            () => sut.ProcessAsync(Message(), "eimdb", log));

        Assert.Contains("Route R1 is not valid.", ex.Message);
        Assert.Equal(CreateSalesOrder, ex.ServiceMethod);
        Assert.Equal("PBL-1", ex.BusinessKey);
        Assert.Contains(log.Errors, e => e.Detail!.Contains("Route R1 is not valid."));

        var envelope = JsonNode.Parse(Assert.Single(sut.Replies).Payload)!.AsObject();
        Assert.Equal("Failed", (string?)envelope["Status"]);
        Assert.Contains("Route R1 is not valid.", (string?)envelope["StatusMessage"]);
        Assert.Equal("Reconcile", (string?)envelope["Action"]);
        Assert.Equal("CAP-1", (string?)envelope["SourceId"]);
        Assert.Equal("""{"PBLId":"PBL-1"}""", envelope["Data"]!.ToJsonString());
    }

    [Fact]
    public async Task Rejection_with_customer_error_carries_the_text()
    {
        var sut = new TestableSalesOrder
        {
            Response = """{"Success":false,"PBLId":"PBL-1","ErrorMessage":"Customer account C999 does not exist."}""",
        };

        var ex = await Assert.ThrowsAsync<D365ServiceRejectedException>(
            () => sut.ProcessAsync(Message(), "eimdb", new RecordingLogger()));

        Assert.Contains("Customer account C999 does not exist.", ex.Message);
    }

    // Scenario: Unrecognised response fails as terminal
    [Fact]
    public async Task Unrecognised_response_throws_and_replies_failed()
    {
        var sut = new TestableSalesOrder { Response = "<html>oops</html>" };

        await Assert.ThrowsAsync<JsonException>(() => sut.ProcessAsync(Message(), "eimdb", new RecordingLogger()));

        Assert.Equal("Failed", (string?)JsonNode.Parse(Assert.Single(sut.Replies).Payload)!["Status"]);
    }

    // Scenario: Missing dataAreaId is terminal (no D365 call is made)
    [Fact]
    public async Task Missing_dataAreaId_makes_no_call_and_replies_failed()
    {
        var sut = new TestableSalesOrder { Response = CreatedResponse };
        var payload = SmallEnvelope(data => data.Remove("dataAreaId"));

        await Assert.ThrowsAsync<JsonException>(() => sut.ProcessAsync(Message(payload), "eimdb", new RecordingLogger()));

        Assert.Empty(sut.Calls);
        var envelope = JsonNode.Parse(Assert.Single(sut.Replies).Payload)!.AsObject();
        Assert.Equal("Failed", (string?)envelope["Status"]);
        Assert.Equal("CAP-1", (string?)envelope["SourceId"]);
        Assert.Contains("dataAreaId", (string?)envelope["StatusMessage"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task Definite_d365_http_rejection_replies_failed(HttpStatusCode status)
    {
        var failure = new D365FSCException("Invalid order", status);
        var sut = new TestableSalesOrder { Failure = failure };

        var thrown = await Assert.ThrowsAsync<D365FSCException>(() => sut.ProcessAsync(Message(), "eimdb", new RecordingLogger()));

        Assert.Same(failure, thrown);
        var envelope = JsonNode.Parse(Assert.Single(sut.Replies).Payload)!.AsObject();
        Assert.Equal("Failed", (string?)envelope["Status"]);
        Assert.Contains("Invalid order", (string?)envelope["StatusMessage"]);
    }

    // Scenarios: Setup failure sends no reply; Setup failure wrapping another error still sends no reply
    [Theory]
    [MemberData(nameof(SetupFailures))]
    public async Task Setup_failure_sends_no_reply_and_is_rethrown_as_terminal(Exception failure, int status)
    {
        var sut = new TestableSalesOrder { Failure = failure };
        var log = new RecordingLogger();

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => sut.ProcessAsync(Message(), "eimdb", log));

        Assert.Same(failure, thrown);
        Assert.Single(sut.Calls);
        Assert.Empty(sut.Replies);
        Assert.Contains(log.Errors, e => e.Name == "CallService");
        Assert.Equal(FailureDisposition.Terminal, new DefaultFailureClassifier(new RetryOptions()).Classify(thrown));

        // The dedicated error step the go-live alert watches: HTTP status and PBLId, nothing else.
        var setup = Assert.Single(log.Errors, e => e.Name == "SetupFailure");
        Assert.Equal($"D365 returned {status} for PBLId PBL-1; reply held until reprocess", setup.Detail);
        Assert.DoesNotContain(log.Entries, e => e.Name == "SetupFailure");
        Assert.DoesNotContain(log.Warnings, e => e.Name == "SetupFailure");
        Assert.Contains(log.Entries, e => e.Name == "SendResponse" && e.Detail!.StartsWith("Skipped: no reply for this failure"));
    }

    // Scenario: Other failures do not log a setup failure
    [Theory]
    [InlineData("503")]
    [InlineData("timeout")]
    [InlineData("rejection")]
    [InlineData("400")]
    public async Task Other_failures_do_not_log_a_setup_failure(string kind)
    {
        var sut = kind switch
        {
            "503" => new TestableSalesOrder { Failure = new D365FSCException("unavailable", HttpStatusCode.ServiceUnavailable) },
            "timeout" => new TestableSalesOrder { Failure = new TimeoutException("timed out") },
            "rejection" => new TestableSalesOrder { Response = """{"Success":false,"PBLId":"PBL-1","ErrorMessage":"Route R1 is not valid."}""" },
            "400" => new TestableSalesOrder { Failure = new D365FSCException("Invalid order", HttpStatusCode.BadRequest) },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var log = new RecordingLogger();

        await Assert.ThrowsAnyAsync<Exception>(() => sut.ProcessAsync(Message(), "eimdb", log));

        Assert.DoesNotContain(log.Errors, e => e.Name == "SetupFailure");
        Assert.DoesNotContain(log.Entries, e => e.Name == "SetupFailure");
        Assert.DoesNotContain(log.Warnings, e => e.Name == "SetupFailure");
    }

    // Scenario: Reprocess after a setup failure sends one reply
    [Fact]
    public async Task Reprocess_after_a_setup_failure_sends_one_success_reply()
    {
        // One instance, as the registry shares it between the first run and the Dashboard reprocess.
        var sut = new TestableSalesOrder { Failure = new D365FSCException("unauthorized", HttpStatusCode.Unauthorized) };
        var message = Message();

        await Assert.ThrowsAsync<D365FSCException>(() => sut.ProcessAsync(message, "eimdb", new RecordingLogger()));
        Assert.Empty(sut.Replies);

        // Setup fixed; the reprocess calls D365 again.
        sut.Failure = null;
        var result = await sut.ProcessAsync(message, "eimdb", new RecordingLogger());

        Assert.Equal(MessageStatus.Completed, result.Status);
        Assert.Equal("200007", result.DestinationId);
        Assert.Equal(2, sut.Calls.Count);

        var envelope = JsonNode.Parse(Assert.Single(sut.Replies).Payload)!.AsObject();
        Assert.Equal("Success", (string?)envelope["Status"]);
        Assert.Equal("200007", (string?)envelope["Data"]!["SalesId"]);
    }

    // Scenario: Configured retryable OData code sends no reply
    [Fact]
    public async Task Configured_retryable_odata_code_sends_no_reply_and_is_rethrown()
    {
        var failure = new D365FSCException("busy", HttpStatusCode.BadRequest, "DatabaseBusy");
        var sut = new TestableSalesOrder
        {
            Failure = failure,
            RetryableCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "databasebusy" },
        };

        var thrown = await Assert.ThrowsAsync<D365FSCException>(() => sut.ProcessAsync(Message(), "eimdb", new RecordingLogger()));

        Assert.Same(failure, thrown);
        Assert.Single(sut.Calls);
        Assert.Empty(sut.Replies);
    }

    [Fact]
    public async Task Unreadable_retry_settings_hold_the_reply_back_and_keep_the_original_failure()
    {
        var failure = new D365FSCException("Invalid order", HttpStatusCode.BadRequest);
        var sut = new TestableSalesOrder
        {
            Failure = failure,
            RetryCodesFailure = new ArgumentOutOfRangeException("RetryMaxAttempts"),
        };
        var log = new RecordingLogger();

        var thrown = await Assert.ThrowsAsync<D365FSCException>(() => sut.ProcessAsync(Message(), "eimdb", log));

        Assert.Same(failure, thrown);
        Assert.Empty(sut.Replies);
        Assert.Contains(log.Errors, e => e.Name == "SendResponse" && e.Detail!.Contains("retry settings"));
        Assert.DoesNotContain(log.Errors, e => e.Name == "SetupFailure");
    }

    [Fact]
    public async Task Unreadable_retry_settings_still_log_a_setup_failure()
    {
        var sut = new TestableSalesOrder
        {
            Failure = new D365FSCException("unauthorized", HttpStatusCode.Unauthorized),
            RetryCodesFailure = new ArgumentOutOfRangeException("RetryMaxAttempts"),
        };
        var log = new RecordingLogger();

        await Assert.ThrowsAsync<D365FSCException>(() => sut.ProcessAsync(Message(), "eimdb", log));

        Assert.Empty(sut.Replies);
        Assert.Contains(log.Errors, e => e.Name == "SetupFailure" && e.Detail!.Contains("401"));
    }

    // Scenario: Setup failure wrapping another error still sends no reply
    [Fact]
    public async Task Setup_failure_wrapping_another_error_still_sends_no_reply()
    {
        var failure = new D365FSCException("unauthorized", HttpStatusCode.Unauthorized, inner: new JsonException("bad body"));
        var sut = new TestableSalesOrder { Failure = failure };
        var log = new RecordingLogger();

        var thrown = await Assert.ThrowsAsync<D365FSCException>(() => sut.ProcessAsync(Message(), "eimdb", log));

        Assert.Same(failure, thrown);
        Assert.Empty(sut.Replies);
        Assert.Equal(FailureDisposition.Terminal, new DefaultFailureClassifier(new RetryOptions()).Classify(thrown));
        Assert.Contains(log.Errors, e => e.Name == "SetupFailure" && e.Detail!.Contains("401"));
    }

    // Scenario: Transient failure sends no reply
    [Theory]
    [MemberData(nameof(TransientFailures))]
    public async Task Transient_failure_sends_no_reply_and_is_rethrown(Exception failure)
    {
        var sut = new TestableSalesOrder { Failure = failure };
        var log = new RecordingLogger();

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => sut.ProcessAsync(Message(), "eimdb", log));

        Assert.Same(failure, thrown);
        Assert.Single(sut.Calls);
        Assert.Empty(sut.Replies);
        Assert.Contains(log.Errors, e => e.Name == "CallService");
    }

    // Scenario: Reply failure does not fail a created order
    [Fact]
    public async Task Reply_failure_does_not_fail_a_created_order()
    {
        var sut = new TestableSalesOrder { Response = CreatedResponse, ReplyFailure = new InvalidOperationException("Service Bus down") };
        var log = new RecordingLogger();

        var result = await sut.ProcessAsync(Message(), "eimdb", log);

        Assert.Equal(MessageStatus.Completed, result.Status);
        Assert.Equal("200007", result.DestinationId);
        Assert.Single(sut.Replies);
        Assert.Contains(log.Errors, e => e.Name == "SendResponse" && e.Detail!.Contains("Service Bus down"));
    }

    [Fact]
    public async Task Reply_failure_does_not_mask_a_rejection()
    {
        var sut = new TestableSalesOrder
        {
            Response = """{"Success":false,"ErrorMessage":"Route R1 is not valid."}""",
            ReplyFailure = new InvalidOperationException("Service Bus down"),
        };

        var ex = await Assert.ThrowsAsync<D365ServiceRejectedException>(
            () => sut.ProcessAsync(Message(), "eimdb", new RecordingLogger()));

        Assert.Equal("Route R1 is not valid.", ex.Message);
    }

    [Fact]
    public async Task No_reply_is_sent_without_a_response_queue()
    {
        var sut = new TestableSalesOrder { Response = CreatedResponse };

        var result = await sut.ProcessAsync(Message(responseQueue: " "), "eimdb", new RecordingLogger());

        Assert.Equal(MessageStatus.Completed, result.Status);
        Assert.Empty(sut.Replies);
    }

    [Fact]
    public void Registry_still_dispatches_sales_orders_to_this_orchestration()
        => Assert.IsType<SalesOrder_To_D365FSC>(OrchestrationRegistry.CreateDefault()["SalesOrder_To_D365FSC"]);

    // ── Test doubles ─────────────────────────────────────────────────────────

    private sealed class TestableSalesOrder : SalesOrder_To_D365FSC
    {
        public string Response { get; init; } = CreatedResponse;
        public Exception? Failure { get; set; }
        public Exception? ReplyFailure { get; init; }
        public IReadOnlySet<string> RetryableCodes { get; init; } = NoRetryableCodes;
        public Exception? RetryCodesFailure { get; init; }

        public List<(string Connection, string Method, string Body)> Calls { get; } = [];
        public List<(string Connection, string Queue, string Payload)> Replies { get; } = [];

        protected override string ResolveConnection(string system, string environment, string connectionString)
            => $"{system}-connection";

        protected override Task<string> CallServiceAsync(
            string destinationConnection, string method, string body, CancellationToken ct)
        {
            Calls.Add((destinationConnection, method, body));
            return Failure is null ? Task.FromResult(Response) : Task.FromException<string>(Failure);
        }

        protected override IReadOnlySet<string> GetRetryableODataErrorCodes()
            => RetryCodesFailure is null ? RetryableCodes : throw RetryCodesFailure;

        protected override Task SendReplyAsync(string sourceConnection, string queue, string payload, CancellationToken ct)
        {
            Replies.Add((sourceConnection, queue, payload));
            return ReplyFailure is null ? Task.CompletedTask : Task.FromException(ReplyFailure);
        }
    }

    private sealed class RecordingLogger : IOrchestrationLogger
    {
        // One list per level, as OrchestrationLogger forwards Log/LogWarning/LogError to ILogger.
        public List<(string Name, string? Detail)> Entries { get; } = [];
        public List<(string Name, string? Detail)> Warnings { get; } = [];
        public List<(string Name, string? Detail)> Errors { get; } = [];

        public void Log(string name, string? detail = null) => Entries.Add((name, detail));
        public void LogWarning(string name, string? detail = null) => Warnings.Add((name, detail));
        public void LogError(string name, string? detail = null) => Errors.Add((name, detail));
        public ILogger Logger => NullLogger.Instance;
    }
}
