using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ARM.Integration.Dashboard.API.Models;
using ARM.Integration.Dashboard.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ARM.Integration.Dashboard.API.Controllers;

[ApiController]
[Route("api/gateway/consumers")]
[Authorize(Roles = "Admin")]
public class GatewayConsumerController(
    GatewayConsumerRepository repo,
    GatewayKeyVerifier verifier,
    ILogger<GatewayConsumerController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await repo.GetAllAsync());

    /// <summary>
    /// The gateways a key may be verified against, so the dashboard can show where a key will be
    /// checked and offer a choice when more than one exists. Addresses only; no key material.
    /// </summary>
    /// <remarks>
    /// Absolute route rather than "gateways" relative to this controller, which would sit under the
    /// same path as <c>{consumerId}</c> and make a consumer named "gateways" unreachable.
    /// </remarks>
    [HttpGet("/api/gateway/gateways")]
    public IActionResult GetGateways() =>
        Ok(verifier.Targets.Select(t => new { name = t.Name, baseUrl = t.BaseUrl }));

    [HttpGet("{consumerId}")]
    public async Task<IActionResult> Get(string consumerId)
    {
        var record = await repo.GetAsync(consumerId);
        return record is null ? NotFound() : Ok(record);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] GatewayConsumerInput input)
    {
        if (string.IsNullOrWhiteSpace(input.ConsumerId) || string.IsNullOrWhiteSpace(input.SystemProfile))
        {
            return BadRequest("ConsumerId and SystemProfile are required.");
        }

        if (await repo.GetAsync(input.ConsumerId) is not null)
        {
            return Conflict("A consumer with this id already exists.");
        }

        var (checkedInput, addressError) = WithCheckedAddress(input);
        if (addressError is not null) return BadRequest(addressError);

        if (input.Enabled && await BlockedByUnverifiedIdentity(input.ConsumerId))
        {
            return Conflict(UnverifiedIdentityMessage);
        }

        await repo.CreateAsync(checkedInput, CallerIdentity());
        return CreatedAtAction(nameof(Get), new { consumerId = input.ConsumerId }, await repo.GetAsync(input.ConsumerId));
    }

    [HttpPut("{consumerId}")]
    public async Task<IActionResult> Update(string consumerId, [FromBody] GatewayConsumerInput input)
    {
        var (checkedInput, addressError) = WithCheckedAddress(input);
        if (addressError is not null) return BadRequest(addressError);

        if (input.Enabled && await BlockedByUnverifiedIdentity(consumerId))
        {
            return Conflict(UnverifiedIdentityMessage);
        }

        return await repo.UpdateAsync(consumerId, checkedInput, CallerIdentity()) ? NoContent() : NotFound();
    }

    /// <summary>
    /// Applies the gateway address policy and normalises the address, so what is stored is what will
    /// later be probed. Rejecting at save time means a bad address is reported while the
    /// administrator is still looking at the field, rather than as a failed key registration later.
    /// </summary>
    private (GatewayConsumerInput Input, string? Error) WithCheckedAddress(GatewayConsumerInput input)
    {
        if (string.IsNullOrWhiteSpace(input.GatewayUrl))
        {
            return (input with { GatewayUrl = null }, null);
        }

        var check = verifier.CheckAddress(input.GatewayUrl);

        return check.Target is null
            ? (input, check.Error)
            : (input with { GatewayUrl = check.Target.BaseUrl }, null);
    }

    [HttpDelete("{consumerId}")]
    public async Task<IActionResult> Delete(string consumerId)
    {
        var deleted = await repo.DeleteAsync(consumerId);

        if (deleted)
        {
            logger.LogWarning(
                "GatewayConsumerDeleted: consumer={ConsumerId} by={Actor}", consumerId, CallerIdentity());
        }

        return deleted ? NoContent() : NotFound();
    }

    /// <summary>
    /// Registers a consumer's function key by storing its hash.
    /// </summary>
    /// <remarks>
    /// Without this, enabling identity verification needs a SHA-256 computed by hand and direct SQL,
    /// which defeats the point of administering consumers from the dashboard. The key is hashed here
    /// and the raw value is never persisted, echoed or written to a log; the only observable effect
    /// is that the consumer starts reporting as verified.
    /// </remarks>
    [HttpPost("{consumerId}/key")]
    public async Task<IActionResult> SetKey(string consumerId, [FromBody] GatewayConsumerKeyInput input)
    {
        var key = input.Key?.Trim();

        if (string.IsNullOrEmpty(key)) return BadRequest("A function key is required.");

        // A 64-character hex string is a digest, not a key. Accepting it would store the hash of a
        // hash, which fails closed but only at the first real request, with a misleading 403.
        if (key.Length == 64 && key.All(Uri.IsHexDigit))
        {
            return BadRequest("That looks like a hash rather than a function key. Paste the key itself.");
        }

        // Verified before hashing. Storing first and checking later would leave a consumer holding a
        // hash that matches nothing, which presents as a 403 on their next real request.
        var consumer = await repo.GetAsync(consumerId);
        if (consumer is null) return NotFound();

        // The consumer's own address wins over the caller's choice from the configured list. It was
        // validated when it was saved, and it is the address that consumer's traffic actually uses,
        // so verifying anywhere else would prove the key against a gateway they never call.
        var verification = string.IsNullOrWhiteSpace(consumer.GatewayUrl)
            ? await verifier.VerifyAsync(key, input.GatewayApp, HttpContext.RequestAborted)
            : await verifier.VerifyAtAsync(key, consumer.GatewayUrl, HttpContext.RequestAborted);

        switch (verification.Verdict)
        {
            case GatewayKeyVerdict.Rejected:
                logger.LogWarning(
                    "GatewayConsumerKeyRejected: consumer={ConsumerId} app={App} by={Actor}",
                    consumerId, verification.GatewayApp, CallerIdentity());
                return BadRequest(verification.Detail);

            case GatewayKeyVerdict.Unreachable:
                // Deliberately not a rejection. Telling an administrator their good key is bad
                // during a gateway outage sends them looking for a fault in the key.
                logger.LogWarning(
                    "GatewayConsumerKeyUnchecked: consumer={ConsumerId} app={App} by={Actor}",
                    consumerId, verification.GatewayApp, CallerIdentity());
                return StatusCode(StatusCodes.Status503ServiceUnavailable, verification.Detail);
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var verified = verification.Verdict == GatewayKeyVerdict.Accepted;

        var stored = await repo.SetKeyHashAsync(
            consumerId,
            hash,
            CallerIdentity(),
            verified ? verification.GatewayApp : null,
            verified ? DateTime.UtcNow : null);

        if (!stored) return NotFound();

        logger.LogInformation(
            "GatewayConsumerKeyRegistered: consumer={ConsumerId} verdict={Verdict} app={App} by={Actor}",
            consumerId, verification.Verdict, verification.GatewayApp, CallerIdentity());

        return Ok(new { verified, detail = verification.Detail, gatewayApp = verification.GatewayApp });
    }

    private const string UnverifiedIdentityMessage =
        "Another enabled consumer has no key hash recorded, so consumer identity is unverified. " +
        "Enable key verification before operating more than one consumer.";

    /// <summary>
    /// While any enabled consumer's identity is unverified, a second one must not be enabled:
    /// <c>X-Consumer-Id</c> is a plain header, so with two identities in play it would select which
    /// set of grants to apply without anything proving the choice.
    /// </summary>
    private async Task<bool> BlockedByUnverifiedIdentity(string consumerId) =>
        await repo.CountEnabledUnverifiedAsync(excludingConsumerId: consumerId) > 0;

    private string CallerIdentity() =>
        User.FindFirst(ClaimTypes.Upn)?.Value
        ?? User.FindFirst("preferred_username")?.Value
        ?? User.Identity?.Name
        ?? "unknown";
}

[ApiController]
[Route("api/gateway/consumers/{consumerId}/permissions")]
[Authorize(Roles = "Admin")]
public class GatewayPermissionController(
    GatewayPermissionRepository repo,
    GatewayConsumerRepository consumers,
    IConfiguration configuration,
    ILogger<GatewayPermissionController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(string consumerId, [FromQuery] string? environment)
        => Ok(await repo.GetAsync(consumerId, environment?.Trim().ToUpperInvariant()));

    [HttpPost]
    public async Task<IActionResult> Grant(string consumerId, [FromBody] GatewayPermissionInput input)
    {
        if (await consumers.GetAsync(consumerId) is null) return NotFound("Unknown consumer.");

        var validation = Validate(input);
        if (validation is not null) return BadRequest(validation);

        await repo.CreateAsync(consumerId, input, CallerIdentity());
        NotifyIfProduction("GatewayGrantCreated", consumerId, input);

        return NoContent();
    }

    [HttpDelete]
    public async Task<IActionResult> Revoke(string consumerId, [FromBody] GatewayPermissionInput input)
    {
        var removed = await repo.DeleteAsync(consumerId, input);
        if (removed) NotifyIfProduction("GatewayGrantDeleted", consumerId, input);

        return removed ? NoContent() : NotFound();
    }

    /// <summary>
    /// Mirrors the database constraints so the UI gets a usable message rather than a SQL error.
    /// The constraints remain the enforcement point.
    /// </summary>
    private static string? Validate(GatewayPermissionInput input)
    {
        var type = input.ResourceType?.Trim().ToUpperInvariant();
        var verb = input.Verb?.Trim().ToUpperInvariant();

        if (string.IsNullOrWhiteSpace(input.Resource)) return "Resource is required.";
        if (string.IsNullOrWhiteSpace(input.Environment)) return "Environment is required.";
        if (type is not ("DATA" or "ACTION" or "SERVICE")) return "ResourceType must be DATA, ACTION or SERVICE.";
        if (verb == "DELETE") return "DELETE is not grantable in this release.";
        if (verb is not ("GET" or "POST" or "PATCH")) return "Verb must be GET, POST or PATCH.";
        if (type != "DATA" && verb != "POST") return "Actions and services are invoked by POST only.";

        return null;
    }

    /// <summary>
    /// Production grants are not gated by a second approval, by decision. Raising an event makes the
    /// change something that is noticed the same day rather than discovered during an investigation.
    /// </summary>
    private void NotifyIfProduction(string eventName, string consumerId, GatewayPermissionInput input)
    {
        var productionEnvironments = (configuration["Gateway:ProductionEnvironments"] ?? "CDB-PROD,PROD")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var environment = input.Environment.Trim();
        if (!productionEnvironments.Contains(environment, StringComparer.OrdinalIgnoreCase)) return;

        logger.LogWarning(
            "{EventName}: consumer={ConsumerId} environment={Environment} resourceType={ResourceType} resource={Resource} verb={Verb} by={Actor}",
            eventName, consumerId, environment.ToUpperInvariant(), input.ResourceType?.ToUpperInvariant(),
            input.Resource, input.Verb?.ToUpperInvariant(), CallerIdentity());
    }

    private string CallerIdentity() =>
        User.FindFirst(ClaimTypes.Upn)?.Value
        ?? User.FindFirst("preferred_username")?.Value
        ?? User.Identity?.Name
        ?? "unknown";
}

[ApiController]
[Route("api/gateway/catalog")]
[Authorize(Roles = "Admin")]
public class GatewayCatalogController(D365EntityCatalog catalog) : ControllerBase
{
    [HttpGet("{systemProfile}/{environment}/entities")]
    public async Task<IActionResult> GetEntities(string systemProfile, string environment)
    {
        try
        {
            var entities = await catalog.GetEntitiesAsync(systemProfile, environment);
            return entities is null ? NotFound("Environment is not configured.") : Ok(entities);
        }
        catch (Exception)
        {
            return StatusCode(StatusCodes.Status502BadGateway,
                "Could not read the entity list from D365 for this environment.");
        }
    }
}
