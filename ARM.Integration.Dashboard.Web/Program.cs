using ARM.Integration.Dashboard.Web.Middleware;
using ARM.Integration.Dashboard.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMicrosoftIdentityWebAppAuthentication(builder.Configuration, "AzureAd");

builder.Services.Configure<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions>(
    Microsoft.Identity.Web.Constants.AzureAd, options =>
{
    options.Events.OnRemoteFailure = context =>
    {
        // AADSTS50105 = user not assigned to the application
        if (context.Failure?.Message?.Contains("AADSTS50105") == true)
        {
            context.HandleResponse();
            context.Response.Redirect("/AccessDenied");
        }
        return Task.CompletedTask;
    };
});

builder.Services.AddControllersWithViews()
    .AddMicrosoftIdentityUI();

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = options.DefaultPolicy;
});

var apiBaseUrl = builder.Configuration["DashboardApi:BaseUrl"]
    ?? throw new InvalidOperationException("DashboardApi:BaseUrl configuration is missing.");
var apiKey = builder.Configuration["DashboardApi:ApiKey"]
    ?? throw new InvalidOperationException("DashboardApi:ApiKey configuration is missing.");

builder.Services.AddHttpClient<IMessageQueueService, MessageQueueApiService>(client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
    client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
});
builder.Services.AddHttpClient<IEnvironmentService, EnvironmentApiService>(client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
    client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
});
builder.Services.AddHttpClient<IUserService, UserApiService>(client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
    client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
});

builder.Services.AddScoped<IClaimsTransformation, DatabaseUserClaimsTransformation>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<DatabaseUserAuthorizationMiddleware>();

app.MapStaticAssets();

app.MapGet("/AccessDenied", () => Results.Content(
    """
    <!DOCTYPE html>
    <html><head><title>Access Denied</title>
    <link rel="stylesheet" href="/lib/bootstrap/dist/css/bootstrap.min.css" /></head>
    <body class="bg-light d-flex align-items-center justify-content-center" style="min-height:100vh">
      <div class="text-center">
        <h1 class="display-1 text-danger">🔒</h1>
        <h3>Access Denied</h3>
        <p class="text-muted">You are not authorized to access this application.<br/>
        Please contact your administrator to request access.</p>
        <a href="/MicrosoftIdentity/Account/SignOut" class="btn btn-outline-secondary btn-sm mt-2">Sign out</a>
      </div>
    </body></html>
    """, "text/html"));

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();
