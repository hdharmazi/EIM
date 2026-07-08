using ARM.Integration.EIM.Orchestration;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

// Register all orchestrations as a named dictionary for DI-based dispatch
builder.Services.AddSingleton<IReadOnlyDictionary<string, IOrchestration>>(_ =>
    OrchestrationRegistry.CreateDefault());

builder.Build().Run();
