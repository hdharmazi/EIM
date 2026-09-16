using ARM.Integration.Dashboard.API.Auth;
using ARM.Integration.Dashboard.API.Middleware;
using ARM.Integration.Dashboard.API.Services;
using ARM.Integration.EIM.Routing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Identity.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddScoped<MessageQueueRepository>();
builder.Services.AddScoped<EnvironmentRepository>();
builder.Services.AddScoped<ConfigurationRepository>();
builder.Services.AddScoped<UserRepository>();
builder.Services.AddScoped<SimulationService>();
builder.Services.AddScoped<SalesOrderExcelParser>();
builder.Services.AddScoped<RouteRepository>();
builder.Services.AddScoped<StatusRepository>();
builder.Services.AddScoped<GatewayConsumerRepository>();
builder.Services.AddScoped<GatewayPermissionRepository>();
builder.Services.AddScoped<D365EntityCatalog>();

// Named client so the handler is pooled rather than rebuilt per request, and so the probe cannot
// inherit a redirect that would send a function key to somewhere other than the gateway.
builder.Services.AddHttpClient(nameof(GatewayKeyVerifier))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    });

builder.Services.AddScoped(sp => new GatewayKeyVerifier(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(GatewayKeyVerifier)),
    builder.Configuration.GetSection("Gateway").Get<GatewayKeyVerifierOptions>() ?? new(),
    sp.GetService<ILogger<GatewayKeyVerifier>>()));

// Singleton so pause and resume can invalidate the cache the API itself reads through.
builder.Services.AddSingleton<IRouteRepository>(_ => new SqlRouteRepository(
    builder.Configuration.GetConnectionString("EIMDB")
    ?? throw new InvalidOperationException("ConnectionStrings:EIMDB is not configured.")));
builder.Services.AddSingleton<IRouteResolver>(sp => new CachingRouteResolver(
    sp.GetRequiredService<IRouteRepository>(),
    logger: sp.GetService<ILogger<CachingRouteResolver>>()));

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// Entra JWT bearer is the only authentication mechanism. Every caller is resolved to a
// dashboard user; there is no credential that bypasses Entra.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration, "AzureAd");

// Resolves the bearer caller to a dashboard user (role + DbUserValidated).
builder.Services.AddScoped<IClaimsTransformation, DbUserClaimsTransformation>();

builder.Services.AddAuthorization(options =>
{
    // Every request must be authenticated and resolve to an active dashboard user.
    var dashboardUser = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireClaim("DbUserValidated", "true")
        .Build();

    options.FallbackPolicy = dashboardUser;
    options.AddPolicy(AuthorizationPolicies.DashboardUser, dashboardUser);

    // DefaultPolicy stays authentication-only so GET /api/me remains reachable for a
    // caller who is not DB-validated; the SPA reads it to show the access-denied view.
});

var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod());
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
