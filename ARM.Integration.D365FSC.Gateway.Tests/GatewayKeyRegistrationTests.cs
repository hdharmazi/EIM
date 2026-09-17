using System.Net;
using ARM.Integration.Dashboard.API.Controllers;
using ARM.Integration.Dashboard.API.Models;
using ARM.Integration.Dashboard.API.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ARM.Integration.D365FSC.Gateway.Tests;

/// <summary>
/// The guards around key registration: a digest must never be stored in place of a key, and a key
/// the gateway will not vouch for must not displace one that already works.
/// </summary>
public class GatewayKeyRegistrationTests
{
    private const string Consumer = "acme-erp";
    private const string ExistingHash = "1111111111111111111111111111111111111111111111111111111111111111";

    [Fact]
    public async Task A_rejected_key_leaves_the_existing_hash_in_place()
    {
        // 401 is the gateway refusing a key it never issued.
        var (controller, repo, _) = Build(HttpStatusCode.Unauthorized);

        var result = await controller.SetKey(Consumer, new GatewayConsumerKeyInput { Key = "not-the-right-key" });

        Assert.IsType<BadRequestObjectResult>(result);

        // Nothing was written, so whatever hash the consumer already had is still the one in force.
        Assert.Null(repo.StoredHash);
    }

    [Fact]
    public async Task An_unreachable_gateway_leaves_the_existing_hash_in_place()
    {
        var (controller, repo, _) = Build(_ => throw new HttpRequestException("no such host"));

        var result = await controller.SetKey(Consumer, new GatewayConsumerKeyInput { Key = "a-perfectly-good-key" });

        // 503, not 400: the key was never shown to be wrong, only unverifiable.
        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status.StatusCode);
        Assert.Null(repo.StoredHash);
    }

    /// <summary>
    /// A 64-character hex string is a digest. Storing it would hash the hash, which fails closed but
    /// only at the consumer's next real request, as a 403 that names nothing useful.
    /// </summary>
    [Fact]
    public async Task A_hex_digest_is_refused_before_any_network_call()
    {
        var (controller, repo, probe) = Build(HttpStatusCode.BadRequest);

        var result = await controller.SetKey(Consumer, new GatewayConsumerKeyInput { Key = ExistingHash });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(0, probe.Calls);
        Assert.Null(repo.StoredHash);
    }

    [Fact]
    public async Task An_accepted_key_is_stored_with_what_it_was_verified_against()
    {
        var (controller, repo, _) = Build(HttpStatusCode.BadRequest, gateway: true);

        var result = await controller.SetKey(Consumer, new GatewayConsumerKeyInput { Key = "a-real-key" });

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(repo.StoredHash);
        Assert.NotEqual(ExistingHash, repo.StoredHash);
        Assert.Equal("azf-d365fsc-gateway-wtc-nonprod", repo.StoredApp);
        Assert.NotNull(repo.StoredVerifiedOn);
    }

    private static (GatewayConsumerController, FakeRepo, ProbeCounter) Build(
        HttpStatusCode status, bool gateway = false)
        => Build(_ =>
        {
            var response = new HttpResponseMessage(status);
            if (gateway) response.Headers.Add("X-Correlation-Id", "abc");
            return response;
        });

    private static (GatewayConsumerController, FakeRepo, ProbeCounter) Build(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var probe = new ProbeCounter(respond);
        var repo = new FakeRepo();

        var verifier = new GatewayKeyVerifier(
            new HttpClient(probe),
            new GatewayKeyVerifierOptions
            {
                BaseUrl = "https://azf-d365fsc-gateway-wtc-nonprod.azurewebsites.net",
                AppName = "azf-d365fsc-gateway-wtc-nonprod",
            });

        var controller = new GatewayConsumerController(repo, verifier, new NullLogger())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        return (controller, repo, probe);
    }

    private sealed class ProbeCounter(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }

    private sealed class FakeRepo : IGatewayConsumerRepository
    {
        public GatewayConsumerRecord Consumer { get; } = new()
        {
            ConsumerId = GatewayKeyRegistrationTests.Consumer,
            SystemProfile = "D365FSC-GW",
            Enabled = true,
            IdentityVerified = true,
            GatewayUrl = null,
        };

        public string? StoredHash { get; private set; }
        public string? StoredApp { get; private set; }
        public DateTime? StoredVerifiedOn { get; private set; }

        public Task<GatewayConsumerRecord?> GetAsync(string consumerId) =>
            Task.FromResult<GatewayConsumerRecord?>(consumerId == Consumer.ConsumerId ? Consumer : null);

        public Task<bool> SetKeyHashAsync(
            string consumerId, string keyHash, string modifiedBy, string? verifiedApp, DateTime? verifiedOn)
        {
            StoredHash = keyHash;
            StoredApp = verifiedApp;
            StoredVerifiedOn = verifiedOn;
            return Task.FromResult(true);
        }

        public Task<List<GatewayConsumerRecord>> GetAllAsync() => Task.FromResult(new List<GatewayConsumerRecord> { Consumer });
        public Task<int> CountEnabledUnverifiedAsync(string? excludingConsumerId = null) => Task.FromResult(0);
        public Task CreateAsync(GatewayConsumerInput input, string createdBy) => Task.CompletedTask;
        public Task<bool> UpdateAsync(string consumerId, GatewayConsumerInput input, string modifiedBy) => Task.FromResult(true);
        public Task<bool> DeleteAsync(string consumerId) => Task.FromResult(true);
    }

    private sealed class NullLogger : ILogger<GatewayConsumerController>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) { }
    }
}
