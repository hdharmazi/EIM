using System.Security.Claims;
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
    ILogger<GatewayConsumerController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await repo.GetAllAsync());

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

        if (input.Enabled && await BlockedByUnverifiedIdentity(input.ConsumerId))
        {
            return Conflict(UnverifiedIdentityMessage);
        }

        await repo.CreateAsync(input, CallerIdentity());
        return CreatedAtAction(nameof(Get), new { consumerId = input.ConsumerId }, await repo.GetAsync(input.ConsumerId));
    }

    [HttpPut("{consumerId}")]
    public async Task<IActionResult> Update(string consumerId, [FromBody] GatewayConsumerInput input)
    {
        if (input.Enabled && await BlockedByUnverifiedIdentity(consumerId))
        {
            return Conflict(UnverifiedIdentityMessage);
        }

        return await repo.UpdateAsync(consumerId, input, CallerIdentity()) ? NoContent() : NotFound();
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
