using System.Net;
using System.Text;
using ARM.Integration.Core.Helpers;
using ARM.Integration.D365FSC.Gateway.Configuration;
using ARM.Integration.D365FSC.Gateway.Connections;
using ARM.Integration.D365FSC.Gateway.Forwarding;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace ARM.Integration.D365FSC.Gateway.Tests;

public class GatewayForwarderTests
{
    private const string D365Base = "https://wtc-dev.operations.dynamics.com";
    private const string GatewayBase = "https://gw.example.net";

    private sealed class CapturingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? Captured { get; private set; }
        public string? CapturedBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Captured = request;
            if (request.Content is not null)
            {
                CapturedBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return response;
        }
    }

    private static D365FSCOptions D365Options() => new()
    {
        Tenant = "t",
        ClientId = "c",
        Secret = "s",
        URL = D365Base,
        AuthURL = "https://login.microsoftonline.com"
    };

    private static (GatewayForwarder Forwarder, D365Connection Connection, CapturingHandler Handler) Create(
        HttpResponseMessage? response = null, int maxBytes = 1024 * 1024)
    {
        var handler = new CapturingHandler(response ?? new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        });

        var options = D365Options();
        var http = new HttpClient(handler) { BaseAddress = new Uri(D365Base + "/") };

        var gatewayOptions = new GatewayOptions
        {
            EimDbConnectionString = "unused",
            BaseUrl = GatewayBase,
            MaxRequestBytes = maxBytes
        };

        return (new GatewayForwarder(gatewayOptions), new D365Connection(http, options, "hash"), handler);
    }

    private static HttpRequest Request(string method = "GET", string queryString = "", string? body = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.QueryString = new QueryString(queryString);

        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
            context.Request.ContentType = "application/json";
        }

        return context.Request;
    }

    [Fact]
    public async Task Skiptoken_survives_the_round_trip_unmangled()
    {
        var (forwarder, connection, handler) = Create();

        // Already percent-encoded and full of quotes and commas. Parsing the query into a
        // dictionary and re-serialising would re-encode it and D365 would reject the cursor.
        const string query = "?$skiptoken=dataAreaId%3D%27usmf%27%2CCustomerAccount%3D%27US-042%27&cross-company=true";

        await forwarder.ForwardAsync(
            connection, Request(queryString: query), "data/CustomersV3", "DEV", "cid", default);

        Assert.EndsWith(query, handler.Captured!.RequestUri!.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cross_company_is_not_injected_when_the_caller_omits_it()
    {
        var (forwarder, connection, handler) = Create();

        await forwarder.ForwardAsync(
            connection, Request(), "data/CustomersV3", "DEV", "cid", default);

        Assert.DoesNotContain("cross-company", handler.Captured!.RequestUri!.OriginalString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Caller_credentials_are_never_forwarded()
    {
        var (forwarder, connection, handler) = Create();
        var request = Request();
        request.Headers["Authorization"] = "Bearer caller-token";
        request.Headers["x-functions-key"] = "the-key";
        request.Headers["X-Consumer-Id"] = "acme";
        request.Headers["Cookie"] = "session=1";

        await forwarder.ForwardAsync(connection, request, "data/CustomersV3", "DEV", "cid", default);

        Assert.False(handler.Captured!.Headers.Contains("Authorization"));
        Assert.False(handler.Captured.Headers.Contains("x-functions-key"));
        Assert.False(handler.Captured.Headers.Contains("X-Consumer-Id"));
        Assert.False(handler.Captured.Headers.Contains("Cookie"));
    }

    [Fact]
    public async Task Conditional_and_paging_headers_are_forwarded()
    {
        var (forwarder, connection, handler) = Create();
        var request = Request("PATCH", body: "{}");
        request.Headers["If-Match"] = "*";
        request.Headers["Prefer"] = "odata.maxpagesize=500";

        await forwarder.ForwardAsync(connection, request, "data/CustomersV3", "DEV", "cid", default);

        Assert.True(handler.Captured!.Headers.Contains("If-Match"));
        Assert.True(handler.Captured.Headers.Contains("Prefer"));
    }

    [Fact]
    public async Task Next_link_is_rewritten_to_the_gateway()
    {
        var body = $$"""
            {"@odata.context":"{{D365Base}}/data/$metadata#CustomersV3",
             "value":[],
             "@odata.nextLink":"{{D365Base}}/data/CustomersV3?$skiptoken=abc"}
            """;

        var (forwarder, connection, _) = Create(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });

        var outcome = await forwarder.ForwardAsync(
            connection, Request(), "data/CustomersV3", "DEV", "cid", default);

        Assert.Contains($"{GatewayBase}/api/v1/d365fsc/DEV/data/CustomersV3?$skiptoken=abc", outcome.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(D365Base, outcome.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Location_header_is_rewritten_on_create()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        response.Headers.Location = new Uri($"{D365Base}/data/CustomersV3('US-999')");

        var (forwarder, connection, _) = Create(response);

        var outcome = await forwarder.ForwardAsync(
            connection, Request("POST", body: "{}"), "data/CustomersV3", "DEV", "cid", default);

        Assert.Equal(201, outcome.StatusCode);
        Assert.Equal($"{GatewayBase}/api/v1/d365fsc/DEV/data/CustomersV3('US-999')", outcome.Headers["Location"]);
    }

    [Fact]
    public async Task Forged_forwarded_host_does_not_influence_rewritten_links()
    {
        var body = $$"""{"@odata.nextLink":"{{D365Base}}/data/CustomersV3?$skiptoken=abc"}""";

        var (forwarder, connection, _) = Create(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });

        var request = Request();
        request.Headers["X-Forwarded-Host"] = "attacker.example.com";
        request.Headers["Host"] = "attacker.example.com";

        var outcome = await forwarder.ForwardAsync(connection, request, "data/CustomersV3", "DEV", "cid", default);

        Assert.Contains(GatewayBase, outcome.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("attacker.example.com", outcome.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upstream_response_headers_outside_the_allow_list_are_dropped()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        response.Headers.TryAddWithoutValidation("Server", "Microsoft-IIS/10.0");
        response.Headers.TryAddWithoutValidation("X-AspNet-Version", "4.0.30319");

        var (forwarder, connection, _) = Create(response);

        var outcome = await forwarder.ForwardAsync(connection, Request(), "data/CustomersV3", "DEV", "cid", default);

        Assert.False(outcome.Headers.ContainsKey("Server"));
        Assert.False(outcome.Headers.ContainsKey("X-AspNet-Version"));
    }

    [Fact]
    public async Task Oversized_declared_body_is_rejected_without_calling_d365()
    {
        var (forwarder, connection, handler) = Create(maxBytes: 16);

        var outcome = await forwarder.ForwardAsync(
            connection, Request("POST", body: new string('x', 256)), "data/CustomersV3", "DEV", "cid", default);

        Assert.Equal(413, outcome.StatusCode);
        Assert.Null(handler.Captured);
    }

    [Fact]
    public async Task Oversized_undeclared_body_is_rejected_while_reading()
    {
        var (forwarder, connection, handler) = Create(maxBytes: 16);

        var request = Request("POST", body: new string('x', 256));
        request.ContentLength = null; // chunked: the declared length cannot be trusted

        var outcome = await forwarder.ForwardAsync(
            connection, request, "data/CustomersV3", "DEV", "cid", default);

        Assert.Equal(413, outcome.StatusCode);
        Assert.Null(handler.Captured);
    }

    [Fact]
    public async Task Upstream_status_is_preserved()
    {
        var (forwarder, connection, _) = Create(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        });

        var outcome = await forwarder.ForwardAsync(connection, Request(), "data/CustomersV3", "DEV", "cid", default);

        Assert.Equal(429, outcome.StatusCode);
    }
}
