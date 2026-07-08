using ARM.Integration.Dashboard.API.Auth;
using ARM.Integration.Dashboard.API.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Identity.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddScoped<MessageQueueRepository>();
builder.Services.AddScoped<EnvironmentRepository>();
builder.Services.AddScoped<UserRepository>();
builder.Services.AddScoped<SimulationService>();
builder.Services.AddScoped<SalesOrderExcelParser>();

// Dual authentication: Entra JWT bearer for the SPA, X-Api-Key for service callers.
// A policy scheme routes each request to the right handler based on its headers.
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = "SmartAuth";
    options.DefaultChallengeScheme = "SmartAuth";
})
.AddPolicyScheme("SmartAuth", "ApiKey or Bearer", options =>
{
    options.ForwardDefaultSelector = context =>
        context.Request.Headers.ContainsKey("X-Api-Key")
            ? ApiKeyAuthenticationHandler.SchemeName
            : JwtBearerDefaults.AuthenticationScheme;
})
.AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
    ApiKeyAuthenticationHandler.SchemeName, null)
.AddMicrosoftIdentityWebApi(builder.Configuration, "AzureAd");

// Resolves the bearer caller to a dashboard user (role + DbUserValidated).
builder.Services.AddScoped<IClaimsTransformation, DbUserClaimsTransformation>();

builder.Services.AddAuthorization(options =>
{
    // Every request must be authenticated AND be either a trusted service caller
    // or a DB-validated user.
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireAssertion(ctx =>
            ctx.User.HasClaim("IsService", "true") ||
            ctx.User.HasClaim("DbUserValidated", "true"))
        .Build();
});

var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod());
});

var app = builder.Build();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
