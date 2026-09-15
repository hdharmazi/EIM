using System.Text;
using ARM.Integration.D365FSC.Gateway.Authorization;
using ARM.Integration.D365FSC.Gateway.Configuration;
using ARM.Integration.D365FSC.Gateway.Connections;
using ARM.Integration.D365FSC.Gateway.Forwarding;
using ARM.Integration.D365FSC.Gateway.Idempotency;
using ARM.Integration.D365FSC.Gateway.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace ARM.Integration.D365FSC.Gateway.Functions;

/// <summary>
/// The gateway's request pipeline, shared by the data and services routes.
/// </summary>
public sealed class GatewayPipeline(
    GatewayOptions options,
    GatewayAuthorizer authorizer,
    ID365ConnectionProvider connections,
    GatewayForwarder forwarder,
    IIdempotencyStore idempotency,
    ILogger<GatewayPipeline> logger)
{
    private const string ConsumerHeader = "X-Consumer-Id";
    private const string FunctionKeyHeader = "x-functions-key";
    private const string IdempotencyHeader = "Idempotency-Key";
    private const string CorrelationHeader = "X-Correlation-Id";

    public async Task<IActionResult> HandleAsync(
        HttpRequest request, string environment, GatewayPathResult parsed, CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid().ToString("n");
        request.HttpContext.Response.Headers[CorrelationHeader] = correlationId;

        if (!parsed.IsSuccess)
        {
            logger.LogWarning(
                "Rejected malformed path: {Reason} ({CorrelationId})", parsed.Failure, correlationId);
            return Error(StatusCodes.Status400BadRequest, "bad_request", correlationId);
        }

        var resource = parsed.Resource!;
        var verb = request.Method.ToUpperInvariant();

        var decision = await authorizer.AuthorizeAsync(
            new GatewayAuthorizationRequest(
                request.Headers[ConsumerHeader].FirstOrDefault(),
                request.Headers[FunctionKeyHeader].FirstOrDefault(),
                request.Query.ContainsKey("code"),
                environment,
                verb,
                resource),
            cancellationToken);

        if (!decision.IsAllowed)
        {
            // Deliberately uninformative: the caller learns nothing about which resources exist.
            return Error(
                decision.StatusCode,
                decision.StatusCode == StatusCodes.Status400BadRequest ? "bad_request" : "forbidden",
                correlationId);
        }

        var consumer = decision.Consumer!;
        var normalisedEnvironment = GatewayAuthorizer.Normalise(environment);

        string? idempotencyKey = null;
        if (HttpMethods.IsPost(request.Method))
        {
            idempotencyKey = request.Headers[IdempotencyHeader].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(idempotencyKey))
            {
                return Error(StatusCodes.Status400BadRequest, "idempotency_key_required", correlationId);
            }
        }

        var connection = await connections.GetAsync(consumer.SystemProfile, normalisedEnvironment, cancellationToken);
        if (connection is null)
        {
            logger.LogWarning(
                "Environment {Environment} has no {SystemProfile} configuration ({CorrelationId}).",
                normalisedEnvironment, consumer.SystemProfile, correlationId);
            return Error(StatusCodes.Status404NotFound, "environment_not_configured", correlationId);
        }

        var targetPath = BuildTargetPath(resource, decision.CanonicalResource!);

        if (idempotencyKey is null)
        {
            var direct = await forwarder.ForwardAsync(
                connection, request, targetPath, normalisedEnvironment, correlationId, cancellationToken);
            return Respond(request, direct, correlationId);
        }

        return await HandleIdempotentAsync(
            request, connection, consumer, idempotencyKey, targetPath, normalisedEnvironment, correlationId, cancellationToken);
    }

    private async Task<IActionResult> HandleIdempotentAsync(
        HttpRequest request,
        D365Connection connection,
        GatewayConsumer consumer,
        string idempotencyKey,
        string targetPath,
        string environment,
        string correlationId,
        CancellationToken cancellationToken)
    {
        request.EnableBuffering();
        using var bodyBuffer = new MemoryStream();
        await request.Body.CopyToAsync(bodyBuffer, cancellationToken);
        request.Body.Position = 0;

        var requestHash = SqlIdempotencyStore.ComputeRequestHash(
            request.Method, targetPath, request.QueryString.Value ?? string.Empty, bodyBuffer.ToArray());

        var decision = await idempotency.BeginAsync(
            consumer.ConsumerId, idempotencyKey, requestHash,
            DateTimeOffset.UtcNow.Add(options.IdempotencyRetention), cancellationToken);

        switch (decision.Outcome)
        {
            case IdempotencyOutcome.Replay:
                logger.LogInformation(
                    "Replaying stored response for {ConsumerId} key {Key} ({CorrelationId}).",
                    consumer.ConsumerId, idempotencyKey, correlationId);
                return new ContentResult
                {
                    StatusCode = decision.ResponseStatus ?? StatusCodes.Status200OK,
                    Content = decision.ResponseBody ?? string.Empty,
                    ContentType = "application/json"
                };

            case IdempotencyOutcome.Conflict:
                return Error(StatusCodes.Status409Conflict, "idempotency_key_reused", correlationId);

            case IdempotencyOutcome.InProgress:
                return Error(StatusCodes.Status409Conflict, "request_in_progress", correlationId);
        }

        try
        {
            var outcome = await forwarder.ForwardAsync(
                connection, request, targetPath, environment, correlationId, cancellationToken);

            await idempotency.CompleteAsync(
                consumer.ConsumerId, idempotencyKey, outcome.StatusCode, outcome.Body, cancellationToken);

            return Respond(request, outcome, correlationId);
        }
        catch
        {
            // D365 was never reached, or never answered. Holding the reservation would block the
            // client's retry for something that may not have happened.
            await idempotency.AbandonAsync(consumer.ConsumerId, idempotencyKey, CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Builds the D365-relative path, substituting the stored canonical resource spelling for
    /// whatever casing the caller used.
    /// </summary>
    private static string BuildTargetPath(GatewayResource resource, string canonicalResource) =>
        resource.Type switch
        {
            GatewayResourceType.Data => $"data/{canonicalResource}{resource.KeyPredicate}",

            GatewayResourceType.Action =>
                $"data/{canonicalResource.Split('/')[0]}{resource.KeyPredicate}/" +
                $"{resource.ActionNamespace}{canonicalResource.Split('/')[1]}",

            _ => $"api/services/{canonicalResource}"
        };

    private static IActionResult Respond(HttpRequest request, ForwardOutcome outcome, string correlationId)
    {
        foreach (var (name, value) in outcome.Headers)
        {
            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
            request.HttpContext.Response.Headers[name] = value;
        }

        return new ContentResult
        {
            StatusCode = outcome.StatusCode,
            Content = outcome.Body,
            ContentType = outcome.ContentType ?? "application/json"
        };
    }

    private static IActionResult Error(int statusCode, string error, string correlationId) =>
        new ContentResult
        {
            StatusCode = statusCode,
            Content = GatewayForwarder.BuildErrorBody(error, correlationId),
            ContentType = "application/json"
        };
}

public sealed class D365DataFunction(GatewayPipeline pipeline)
{
    [Function("D365Data")]
    public Task<IActionResult> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", "post", "patch", "delete",
            Route = "v1/d365fsc/{environment}/data/{*path}")] HttpRequest request,
        string environment,
        CancellationToken cancellationToken)
    {
        // Parsed from the raw target, not from the decoded route value: the authorization decision
        // has to be made against what was actually sent.
        var rawTail = GatewayForwarder.RawPathAfter(request, "/data/");

        return pipeline.HandleAsync(
            request, environment, GatewayPathParser.ParseDataPath(rawTail), cancellationToken);
    }
}

public sealed class D365ServiceFunction(GatewayPipeline pipeline)
{
    [Function("D365Service")]
    public Task<IActionResult> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post",
            Route = "v1/d365fsc/{environment}/services/{group}/{service}/{operation}")] HttpRequest request,
        string environment,
        string group,
        string service,
        string operation,
        CancellationToken cancellationToken)
        => pipeline.HandleAsync(
            request, environment, GatewayPathParser.ParseServicePath(group, service, operation), cancellationToken);
}

public sealed class IdempotencyPurgeFunction(IIdempotencyStore store, ILogger<IdempotencyPurgeFunction> logger)
{
    [Function("IdempotencyPurge")]
    public async Task RunAsync(
        [TimerTrigger("%Gateway:IdempotencyPurgeSchedule%")] TimerInfo timer,
        CancellationToken cancellationToken)
    {
        var removed = await store.PurgeExpiredAsync(cancellationToken);
        logger.LogInformation("Purged {Count} expired idempotency record(s).", removed);
    }
}
