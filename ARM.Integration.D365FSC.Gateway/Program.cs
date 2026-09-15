using ARM.Integration.D365FSC.Gateway.Authorization;
using ARM.Integration.D365FSC.Gateway.Configuration;
using ARM.Integration.D365FSC.Gateway.Connections;
using ARM.Integration.D365FSC.Gateway.Forwarding;
using ARM.Integration.D365FSC.Gateway.Functions;
using ARM.Integration.D365FSC.Gateway.Idempotency;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = FunctionsApplication.CreateBuilder(args);

// ASP.NET Core integration rather than the HttpRequestData model: this app is a proxy, so it needs
// the request body as a stream and direct control over response headers and status.
builder.ConfigureFunctionsWebApplication();

builder.Services.AddSingleton(_ => GatewayOptions.FromConfiguration(builder.Configuration));
builder.Services.AddSingleton<IEnvironmentConfigurationSource, EnvironmentConfigurationSource>();
builder.Services.AddSingleton<ID365ConnectionProvider>(sp => new D365ConnectionProvider(
    sp.GetRequiredService<IEnvironmentConfigurationSource>(),
    sp.GetService<ILogger<D365ConnectionProvider>>()));

builder.Services.AddSingleton<IGatewayConsumerRepository, SqlGatewayConsumerRepository>();
builder.Services.AddSingleton<IGatewayPermissionRepository, SqlGatewayPermissionRepository>();
builder.Services.AddSingleton<GatewayAuthorizer>();

builder.Services.AddSingleton<IIdempotencyStore, SqlIdempotencyStore>();
builder.Services.AddSingleton<GatewayForwarder>();
builder.Services.AddSingleton<GatewayPipeline>();

builder.Build().Run();
