using System.Net;
using ARM.Integration.Dashboard.API.Services;
using Xunit;

namespace ARM.Integration.D365FSC.Gateway.Tests;

public class GatewayKeyVerifierTests
{
    private const string BaseUrl = "https://azf-d365fsc-gateway-wtc-nonprod.azurewebsites.net";

    /// <summary>
    /// 401 is the platform refusing a key it never issued, and is the only response that means the
    /// key is wrong. Everything else means the key got past the platform.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest, GatewayKeyVerdict.Accepted)]
    [InlineData(HttpStatusCode.OK, GatewayKeyVerdict.Accepted)]
    [InlineData(HttpStatusCode.Forbidden, GatewayKeyVerdict.Accepted)]
    [InlineData(HttpStatusCode.InternalServerError, GatewayKeyVerdict.Accepted)]
    [InlineData(HttpStatusCode.Unauthorized, GatewayKeyVerdict.Rejected)]
    public async Task Maps_status_to_verdict(HttpStatusCode status, GatewayKeyVerdict expected)
    {
        var verifier = Build(_ => new HttpResponseMessage(status));

        var result = await verifier.VerifyAsync("some-key");

        Assert.Equal(expected, result.Verdict);
    }

    [Fact]
    public async Task Transport_failure_is_unreachable_not_rejected()
    {
        var verifier = Build(_ => throw new HttpRequestException("no such host"));

        var result = await verifier.VerifyAsync("some-key");

        Assert.Equal(GatewayKeyVerdict.Unreachable, result.Verdict);
    }

    [Fact]
    public async Task Timeout_is_unreachable_not_rejected()
    {
        var verifier = BuildAsync(async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, timeoutSeconds: 1);

        var result = await verifier.VerifyAsync("some-key");

        Assert.Equal(GatewayKeyVerdict.Unreachable, result.Verdict);
    }

    [Fact]
    public async Task Records_the_application_that_accepted_the_key()
    {
        var verifier = Build(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));

        var result = await verifier.VerifyAsync("some-key");

        Assert.Equal(GatewayKeyVerdict.Accepted, result.Verdict);
        Assert.Equal("azf-d365fsc-gateway-wtc-nonprod", result.GatewayApp);
    }

    /// <summary>
    /// A local Functions host does not enforce keys, so probing one would report every string as
    /// verified - the defect this class exists to prevent, in the environment where it would be
    /// trusted most readily.
    /// </summary>
    [Theory]
    [InlineData("http://localhost:7072")]
    [InlineData("http://127.0.0.1:7072")]
    public async Task Loopback_is_recorded_unchecked_without_probing(string baseUrl)
    {
        var called = false;
        var verifier = Build(_ => { called = true; return new HttpResponseMessage(HttpStatusCode.OK); }, baseUrl: baseUrl);

        var result = await verifier.VerifyAsync("anything-at-all");

        Assert.Equal(GatewayKeyVerdict.Unchecked, result.Verdict);
        Assert.False(called);
    }

    [Fact]
    public async Task Missing_configuration_is_unreachable_not_accepted()
    {
        var verifier = Build(_ => new HttpResponseMessage(HttpStatusCode.OK), baseUrl: "");

        var result = await verifier.VerifyAsync("some-key");

        Assert.Equal(GatewayKeyVerdict.Unreachable, result.Verdict);
    }

    [Fact]
    public void Resolves_the_deployment_gateway_and_any_extras_without_duplicates()
    {
        var options = new GatewayKeyVerifierOptions
        {
            BaseUrl = "https://primary.example.com",
            AppName = "primary",
            Applications =
            [
                new("primary", "https://primary.example.com"),
                new("secondary", "https://secondary.example.com"),
                new("blank", ""),
            ],
        };

        var targets = options.ResolveTargets();

        Assert.Equal(["primary", "secondary"], targets.Select(t => t.Name));
    }

    /// <summary>
    /// Guessing would record a verification against an application the administrator never chose,
    /// which is the same class of error as reporting an unchecked key as verified.
    /// </summary>
    [Fact]
    public async Task Refuses_to_guess_when_several_gateways_are_configured()
    {
        var verifier = BuildMulti(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));

        var result = await verifier.VerifyAsync("some-key");

        Assert.Equal(GatewayKeyVerdict.Unreachable, result.Verdict);
        Assert.Contains("must be chosen", result.Detail);
    }

    [Fact]
    public async Task Verifies_against_the_chosen_gateway()
    {
        Uri? requested = null;
        var verifier = BuildMulti(
            _ => new HttpResponseMessage(HttpStatusCode.BadRequest),
            onRequest: r => requested = r.RequestUri);

        var result = await verifier.VerifyAsync("some-key", "secondary");

        Assert.Equal(GatewayKeyVerdict.Accepted, result.Verdict);
        Assert.Equal("secondary", result.GatewayApp);
        Assert.StartsWith("https://secondary.example.com/", requested!.ToString());
    }

    [Fact]
    public async Task Unknown_gateway_name_is_refused_rather_than_defaulted()
    {
        var called = false;
        var verifier = BuildMulti(_ => { called = true; return new HttpResponseMessage(HttpStatusCode.OK); });

        var result = await verifier.VerifyAsync("some-key", "not-configured");

        Assert.Equal(GatewayKeyVerdict.Unreachable, result.Verdict);
        Assert.False(called);
    }

    private static GatewayKeyVerifier BuildMulti(
        Func<CancellationToken, HttpResponseMessage> respond,
        Action<HttpRequestMessage>? onRequest = null)
        => new(
            new HttpClient(new StubHandler(ct => Task.FromResult(respond(ct)), onRequest)),
            new GatewayKeyVerifierOptions
            {
                BaseUrl = "https://primary.example.com",
                AppName = "primary",
                Applications = [new("secondary", "https://secondary.example.com")],
            });

    /// <summary>
    /// The dashboard server issues the probe itself, so an unrestricted address would let this
    /// screen reach anything the server can, including hosts only routable from inside Azure.
    /// </summary>
    [Theory]
    [InlineData("http://elsewhere.azurewebsites.net", "https")]
    [InlineData("https://169.254.169.254", "hosted under")]
    [InlineData("https://internal.contoso.local", "hosted under")]
    [InlineData("not a url", "valid URL")]
    [InlineData("https://user:pass@gw.azurewebsites.net", "no credentials")]
    [InlineData("https://gw.azurewebsites.net/?x=1", "no credentials")]
    public void Refuses_an_address_outside_policy(string address, string expectedInError)
    {
        var check = new GatewayKeyVerifierOptions().CheckAddress(address);

        Assert.Null(check.Target);
        Assert.Contains(expectedInError, check.Error);
    }

    [Fact]
    public void Accepts_an_allowed_host_and_names_it_after_the_function_app()
    {
        var check = new GatewayKeyVerifierOptions().CheckAddress(" https://azf-gw-other.azurewebsites.net ");

        Assert.Equal("azf-gw-other", check.Target!.Name);

        // The trailing slash matters: without it the probe path would replace the last segment.
        Assert.EndsWith("/", check.Target.BaseUrl);
    }

    [Fact]
    public async Task Verifies_against_an_address_that_is_not_in_the_configured_list()
    {
        Uri? requested = null;
        var verifier = Build(
            _ => new HttpResponseMessage(HttpStatusCode.BadRequest),
            onRequest: r => requested = r.RequestUri);

        var result = await verifier.VerifyAtAsync("some-key", "https://azf-gw-other.azurewebsites.net");

        Assert.Equal(GatewayKeyVerdict.Accepted, result.Verdict);
        Assert.Equal("azf-gw-other", result.GatewayApp);
        Assert.StartsWith("https://azf-gw-other.azurewebsites.net/api/", requested!.ToString());
    }

    [Fact]
    public async Task A_refused_address_is_unreachable_and_never_probed()
    {
        var called = false;
        var verifier = Build(_ => { called = true; return new HttpResponseMessage(HttpStatusCode.OK); });

        var result = await verifier.VerifyAtAsync("some-key", "https://attacker.example.com");

        Assert.Equal(GatewayKeyVerdict.Unreachable, result.Verdict);
        Assert.False(called);
    }

    [Fact]
    public async Task Sends_the_key_as_a_header_and_omits_the_consumer_id()
    {
        HttpRequestMessage? captured = null;
        var verifier = Build(_ => new HttpResponseMessage(HttpStatusCode.BadRequest), onRequest: r => captured = r);

        await verifier.VerifyAsync("the-key");

        Assert.NotNull(captured);
        Assert.Equal("the-key", captured!.Headers.GetValues("x-functions-key").Single());

        // Omitting the consumer id is what keeps the probe free of side effects: the gateway refuses
        // it before resolving credentials or calling D365.
        Assert.False(captured.Headers.Contains("X-Consumer-Id"));
        Assert.DoesNotContain("?", captured.RequestUri!.ToString());
    }

    private static GatewayKeyVerifier Build(
        Func<CancellationToken, HttpResponseMessage> respond,
        string baseUrl = BaseUrl,
        int timeoutSeconds = 30,
        Action<HttpRequestMessage>? onRequest = null)
        => BuildAsync(ct => Task.FromResult(respond(ct)), baseUrl, timeoutSeconds, onRequest);

    private static GatewayKeyVerifier BuildAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> respond,
        string baseUrl = BaseUrl,
        int timeoutSeconds = 30,
        Action<HttpRequestMessage>? onRequest = null)
        => new(
            new HttpClient(new StubHandler(respond, onRequest)),
            new GatewayKeyVerifierOptions
            {
                BaseUrl = baseUrl,
                AppName = "azf-d365fsc-gateway-wtc-nonprod",
                TimeoutSeconds = timeoutSeconds,
            });

    private sealed class StubHandler(
        Func<CancellationToken, Task<HttpResponseMessage>> respond,
        Action<HttpRequestMessage>? onRequest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onRequest?.Invoke(request);
            return respond(cancellationToken);
        }
    }
}
