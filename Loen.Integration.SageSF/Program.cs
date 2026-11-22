using Loen.Integration.SageSF.Orchestrations;
using Loen.Integration.SageSF.Sage;
using Loen.Integration.SageSF.Salesforce;
using Loen.Integration.SageSF.Sync;
using Loen.Integration.SageSF.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "Loen Sage-Salesforce Integration";
});

builder.Services.AddSingleton<SyncLinkRepository>();
builder.Services.AddSingleton<HashCalculator>();
builder.Services.AddSingleton<SageReaderFactory>();
builder.Services.AddSingleton<SalesforceClientFactory>();
builder.Services.AddSingleton(SageSFOrchestrationRegistry.Create());
builder.Services.AddHostedService<SyncWorker>();

var host = builder.Build();
host.Run();
