using ARM.Integration.EIM.Orchestration;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

// Register all orchestrations as a named dictionary for DI-based dispatch
builder.Services.AddSingleton<IReadOnlyDictionary<string, IOrchestration>>(_ =>
    new Dictionary<string, IOrchestration>(StringComparer.OrdinalIgnoreCase)
    {
        ["Customer_To_Capture"] = new Customer_To_Capture(),
        ["Customer_To_D365FSC"] = new Customer_To_D365FSC(),
        ["Contact_To_Capture"] = new Contact_To_Capture(),
        ["Contact_To_D365FSC"] = new Contact_To_D365FSC(),
        ["PartyContact_To_D365FSC"] = new PartyContact_To_D365FSC(),
        ["CustomerAddress_To_Capture"] = new CustomerAddress_To_Capture(),
        ["CustomerAddress_To_D365FSC"] = new CustomerAddress_To_D365FSC(),
        ["PartyContact_To_Capture"] = new PartyContact_To_Capture(),
        ["SalesOrder_To_D365FSC"] = new SalesOrder_To_D365FSC(),
        ["Sample"] = new Sample(),
    });

builder.Build().Run();
