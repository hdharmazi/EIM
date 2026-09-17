using ARM.Integration.D365FSC.SampleClient;
using ARM.Integration.D365FSC.SampleClient.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    // Resolve appsettings.json next to the executable rather than the current working directory.
    ContentRootPath = AppContext.BaseDirectory
});

// Environment variables win, so the function key never has to be committed: set Gateway__FunctionKey.
builder.Configuration.AddEnvironmentVariables();

builder.Services.Configure<GatewayOptions>(
    builder.Configuration.GetSection(GatewayOptions.SectionName));

builder.Services.AddHttpClient<D365FscGatewayClient>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(4);
});

using var host = builder.Build();

var options = new GatewayOptions();
builder.Configuration.GetSection(GatewayOptions.SectionName).Bind(options);

try
{
    options.Validate();
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"Configuration error: {ex.Message}");
    return 1;
}

var client = host.Services.GetRequiredService<D365FscGatewayClient>();

var shippingDateFrom = new DateOnly(2026, 7, 28);
var shippingDateTo = new DateOnly(2026, 7, 28);
const string dataAreaId = "wtc";

Console.WriteLine($"Gateway     : {options.BaseUrl}");
Console.WriteLine($"Environment : {options.Environment}");
Console.WriteLine($"Consumer    : {options.ConsumerId}");
Console.WriteLine($"Company     : {dataAreaId}");
Console.WriteLine($"Shipping    : {shippingDateFrom:yyyy-MM-dd} .. {shippingDateTo:yyyy-MM-dd}");
Console.WriteLine();

try
{
    IReadOnlyList<WtcEngineeringAppStatus> statuses = await client.GetEngineeringAppStatusesAsync(
        shippingDateFrom,
        shippingDateTo,
        dataAreaId,
        crossCompany: true);

    Console.WriteLine($"Request     : {client.LastRequestUrl}");
    Console.WriteLine($"Correlation : {client.LastCorrelationId}");
    Console.WriteLine($"Rows        : {statuses.Count}");
    Console.WriteLine();

    foreach (var status in statuses)
    {
        Console.WriteLine($"  Sales order    : {status.SalesId} line {status.LineNum} ({status.DataAreaId})");
        Console.WriteLine($"  Customer       : {status.CustAccount} - {status.SalesName}");
        Console.WriteLine($"  Item / model   : {status.ItemId} - {status.Make} {status.Model} ({status.MfgYear})");
        Console.WriteLine($"  Shipping date  : {WtcEngineeringAppStatus.Format(status.ShippingDateConfirmed)}");
        Console.WriteLine($"  Release date   : {WtcEngineeringAppStatus.Format(status.ReleaseDate)}");
        Console.WriteLine($"  Engineering    : {status.EngineeringStatus}");
        Console.WriteLine($"  Order status   : {status.SalesTableStatus} / line {status.SalesLineStatus}");
        Console.WriteLine($"  Drawing appr.  : {status.DrawingApproved ?? "(not set)"}");
        Console.WriteLine();
    }

    var firstStatus = statuses.FirstOrDefault();
    if (firstStatus is not null)
    {
        Console.WriteLine($"First row SalesLineRecId: {firstStatus.SalesLineRecId}");
        Console.WriteLine($"First row InventTransId : {firstStatus.InventTransId}");
    }

    return 0;
}
catch (GatewayException ex)
{
    Console.Error.WriteLine($"Request failed : {client.LastRequestUrl}");
    Console.Error.WriteLine($"Status         : {(int)ex.StatusCode} {ex.StatusCode}");
    Console.Error.WriteLine($"Error code     : {ex.ErrorCode ?? "(none - not a gateway error body)"}");
    Console.Error.WriteLine($"Correlation id : {ex.CorrelationId ?? "(none)"}");
    Console.Error.WriteLine($"Body           : {ex.RawBody}");
    return 1;
}
catch (TaskCanceledException)
{
    Console.Error.WriteLine("Request timed out. The gateway aborts requests after 3m45s.");
    return 1;
}
