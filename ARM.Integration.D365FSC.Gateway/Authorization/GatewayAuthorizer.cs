using System.Security.Cryptography;
using System.Text;
using ARM.Integration.D365FSC.Gateway.Routing;
using Microsoft.Extensions.Logging;

namespace ARM.Integration.D365FSC.Gateway.Authorization;

public sealed record GatewayAuthorizationRequest(
    string? ConsumerId,
    string? FunctionKey,
    bool KeySuppliedInQueryString,
    string Environment,
    string Verb,
    GatewayResource Resource);

public sealed record GatewayAuthorizationResult(
    int StatusCode,
    string? Denial = null,
    GatewayConsumer? Consumer = null,
    string? CanonicalResource = null,
    bool IdentityVerified = false)
{
    public bool IsAllowed => StatusCode == 200;

    public static GatewayAuthorizationResult Allow(GatewayConsumer consumer, string canonicalResource, bool verified)
        => new(200, null, consumer, canonicalResource, verified);

    public static GatewayAuthorizationResult Deny(int statusCode, string denial) => new(statusCode, denial);
}

/// <summary>
/// Decides whether a request may proceed. Deny by default: only an exact
/// <c>GATEWAYPERMISSION</c> row on (consumer, environment, resource type, resource, verb) allows
/// anything, and there are no wildcards.
/// </summary>
/// <remarks>
/// Authorization runs before the environment's D365 credentials are resolved, so a request for an
/// environment the consumer does not hold grants in never causes that environment's secret to be
/// read, let alone used.
/// </remarks>
public sealed class GatewayAuthorizer(
    IGatewayConsumerRepository consumers,
    IGatewayPermissionRepository permissions,
    ILogger<GatewayAuthorizer>? logger = null,
    TimeProvider? timeProvider = null)
{
    private static readonly string[] GrantableVerbs = ["GET", "POST", "PATCH"];

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<GatewayAuthorizationResult> AuthorizeAsync(
        GatewayAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        // Query strings are recorded in request telemetry, App Service logs and any proxy in
        // between, so a key presented there must be treated as already disclosed.
        if (request.KeySuppliedInQueryString)
        {
            return GatewayAuthorizationResult.Deny(400, "key supplied in query string");
        }

        if (string.IsNullOrWhiteSpace(request.ConsumerId))
        {
            return GatewayAuthorizationResult.Deny(400, "missing consumer id");
        }

        var verb = request.Verb.ToUpperInvariant();
        if (!GrantableVerbs.Contains(verb))
        {
            return Denied(request, 403, $"verb {verb} is not grantable");
        }

        var consumer = await consumers.FindAsync(request.ConsumerId.Trim(), cancellationToken);
        if (consumer is null) return Denied(request, 403, "unknown consumer");
        if (!consumer.Enabled) return Denied(request, 403, "consumer disabled");
        if (consumer.ExpiresOn is { } expiry && expiry <= _time.GetUtcNow())
        {
            return Denied(request, 403, "consumer expired");
        }

        var verified = false;
        if (consumer.KeyHash is not null)
        {
            if (!KeyMatches(request.FunctionKey, consumer.KeyHash))
            {
                return Denied(request, 403, "key does not match consumer");
            }

            verified = true;
        }

        var environment = Normalise(request.Environment);

        var canonicalResource = await permissions.FindCanonicalResourceAsync(
            consumer.ConsumerId, environment, request.Resource.Type, request.Resource.Name, verb, cancellationToken);

        if (canonicalResource is null)
        {
            return Denied(request, 403, "no matching grant");
        }

        if (!verified)
        {
            logger?.LogWarning(
                "Request authorized with UNVERIFIED identity for consumer {ConsumerId}; a second consumer must not be enabled until key verification is in place.",
                consumer.ConsumerId);
        }

        return GatewayAuthorizationResult.Allow(consumer, canonicalResource, verified);
    }

    public static string Normalise(string value) => value.Trim().ToUpperInvariant();

    public static string HashKey(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private static bool KeyMatches(string? presentedKey, string expectedHash)
    {
        if (string.IsNullOrEmpty(presentedKey)) return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(HashKey(presentedKey)),
            Encoding.UTF8.GetBytes(expectedHash.ToLowerInvariant()));
    }

    private GatewayAuthorizationResult Denied(GatewayAuthorizationRequest request, int statusCode, string denial)
    {
        // The attempted five-tuple is what turns "but I granted it" into a two-second diagnosis,
        // and the denial rate is the closest thing to probe detection this design has.
        logger?.LogWarning(
            "Gateway denied: consumer={ConsumerId} environment={Environment} resourceType={ResourceType} resource={Resource} verb={Verb} reason={Reason}",
            request.ConsumerId, Normalise(request.Environment), request.Resource.Type, request.Resource.Name,
            request.Verb.ToUpperInvariant(), denial);

        return GatewayAuthorizationResult.Deny(statusCode, denial);
    }
}
