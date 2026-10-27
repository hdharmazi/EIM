using ARM.Integration.D365FSC.SampleEngApp;
using ARM.Integration.D365FSC.SampleEngApp.Models;
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

// Stand-ins for printList.dateRunStart / printList.dateRunEnd in the existing print-list code.
var dateRunStart = new DateTime(2026, 7, 28);
var dateRunEnd = new DateTime(2026, 7, 28);
const string dataAreaId = "wtc";
string? itemRoutePrefix = null; // The old query used "0SLV"; null returns every route.

Console.WriteLine($"Gateway     : {options.BaseUrl}");
Console.WriteLine($"Environment : {options.Environment}");
Console.WriteLine($"Consumer    : {options.ConsumerId}");
Console.WriteLine($"Company     : {dataAreaId}");
Console.WriteLine($"Shipping    : {dateRunStart:yyyy-MM-dd} .. {dateRunEnd:yyyy-MM-dd}");
Console.WriteLine($"Route prefix: {itemRoutePrefix ?? "(any)"}");
Console.WriteLine();

try
{
    // BEFORE (LINQ-to-SQL against the replicated database):
    //
    //   var line = (from sl in ctx.SALESLINES
    //               join stb in ctx.SALESTABLES ... 
    //               where sl.ITEMROUTEID.StartsWith("0SLV")
    //                  && sl.SHIPPINGDATECONFIRMED >= printList.dateRunStart
    //                  && sl.SHIPPINGDATECONFIRMED <= printList.dateRunEnd
    //                  && sl.DATAAREAID.Equals("WTC")
    //               join prodT in ctx.PRODTABLEs ... into ProdTable from pt in ProdTable.DefaultIfEmpty()
    //               join td in ctx.WTC_TRAILERDATA ... into trailData from trailerD in trailData.DefaultIfEmpty()
    //               select new LineTable { SID = sl.SALESID, salesLine = sl, salesTable = stb,
    //                                      prodTable = pt, trailData = trailerD }).ToList();
    //
    // AFTER (D365 performs the joins; the gateway returns flat rows that are re-nested here):
    var line = (await client.GetEngineeringAppStatusesAsync(
            DateOnly.FromDateTime(dateRunStart),
            DateOnly.FromDateTime(dateRunEnd),
            dataAreaId,
            itemRoutePrefix))
        .Select(LineTable.FromStatus)
        .ToList();

    Console.WriteLine($"Request     : {client.LastRequestUrl}");
    Console.WriteLine($"Correlation : {client.LastCorrelationId}");
    Console.WriteLine($"Pages       : {client.PageCount}");
    Console.WriteLine($"Rows        : {line.Count}");
    Console.WriteLine();

    foreach (var l in line)
    {
        Console.WriteLine($"  Sales order    : {l.SID} line {l.salesLine.LINENUM} ({l.salesLine.DATAAREAID})");
        Console.WriteLine($"  Customer       : {l.salesTable.CUSTACCOUNT} - {l.salesTable.SALESNAME}");
        Console.WriteLine($"  Item / route   : {l.salesLine.ITEMID} / {l.salesLine.ITEMROUTEID}");
        Console.WriteLine($"  Shipping date  : {FormatDate(l.salesLine.SHIPPINGDATECONFIRMED)}");
        Console.WriteLine($"  Engineering    : {l.salesLine.ENGINEERINGSTATUS}");
        Console.WriteLine($"  Order status   : {l.salesTable.SALESSTATUS} / line {l.salesLine.SALESSTATUS}");
        Console.WriteLine($"  Drawing appr.  : {l.salesTable.DRAWINGAPPROVED ?? "(not set)"}");
        Console.WriteLine(l.prodTable is null
            ? "  Production     : (none)"
            : $"  Production     : {l.prodTable.PRODID} {l.prodTable.PRODSTATUS}, released {FormatDate(l.prodTable.RELEASEDDATE)}");
        Console.WriteLine(l.trailData is null
            ? "  Trailer        : (none)"
            : $"  Trailer        : {l.trailData.MAKE} {l.trailData.MODEL} ({l.trailData.MFGYEAR})");
        Console.WriteLine();
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

static string FormatDate(DateTime value) => value == LineTable.NoDate ? "-" : value.ToString("yyyy-MM-dd");