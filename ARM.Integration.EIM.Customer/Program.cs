using Wilson.Integration.Orchestrations;
using ARM.Integration.EIM.Orchestration;
using ARM.Integration.EIM.Retry;
using ARM.Integration.EIM.Routing;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = FunctionsApplication.CreateBuilder(args);

// Register all orchestrations as a named dictionary for DI-based dispatch
builder.Services.AddSingleton<IReadOnlyDictionary<string, IOrchestration>>(_ =>
    OrchestrationRegistry.CreateDefault());

var eimdb = Environment.GetEnvironmentVariable("EIMDB")
    ?? throw new InvalidOperationException("EIMDB environment variable is not set.");

builder.Services.AddSingleton<IRouteRepository>(_ => new SqlRouteRepository(eimdb));
builder.Services.AddSingleton<IRouteResolver>(sp => new CachingRouteResolver(
    sp.GetRequiredService<IRouteRepository>(),
    logger: sp.GetService<ILogger<CachingRouteResolver>>()));
builder.Services.AddSingleton<IRouteBacklogProbe>(_ => new SqlRouteBacklogProbe(eimdb));
builder.Services.AddSingleton<HoldGate>();
builder.Services.AddSingleton<IDuplicateProbe>(_ => new SqlDuplicateProbe(eimdb));
builder.Services.AddSingleton<IngestGate>();

// Retry tuning is per environment, read from app settings; Validate throws on a bad value rather
// than silently falling back to a default that hides the typo.
builder.Services.AddSingleton(_ => RetryOptionsFactory.FromEnvironment());
builder.Services.AddSingleton<IFailureClassifier>(sp => new DefaultFailureClassifier(
    sp.GetRequiredService<RetryOptions>(),
    sp.GetService<ILogger<DefaultFailureClassifier>>()));
builder.Services.AddSingleton(sp => new RetryScheduleCalculator(sp.GetRequiredService<RetryOptions>()));

var host = builder.Build();

// Routes are data and orchestration registrations are code, so they drift independently. Checking
// at startup turns that drift into one failure here rather than a runtime failure per message.
await ValidateRoutesAsync(host.Services);

host.Run();

static async Task ValidateRoutesAsync(IServiceProvider services)
{
    var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("RouteValidation");

    try
    {
        var routes = await services.GetRequiredService<IRouteRepository>().LoadAllAsync();
        RouteRegistryValidator.Validate(
            routes, services.GetRequiredService<IReadOnlyDictionary<string, IOrchestration>>());

        logger.LogInformation("Route validation passed: {Count} routes.", routes.Count);
    }
    catch (InvalidOperationException ex)
    {
        logger.LogCritical(ex, "Route validation failed.");
        throw;
    }
    catch (Exception ex)
    {
        // A database blip at startup must not stop the host. The triggers are what keep messages
        // durable, and the resolver reloads on first use regardless.
        logger.LogError(ex, "Could not validate routes at startup; continuing.");
    }
}
