using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text.Json;

namespace ARM.Integration.Core.Helpers;

public static class D365FSCServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ID365FSCClient"/> with <see cref="IHttpClientFactory"/>,
    /// automatic Bearer-token injection, and resilience policies.
    /// </summary>
    /// <example>
    /// <code>
    /// // In Program.cs / Startup
    /// builder.Services.AddD365FSCClient(builder.Configuration);
    ///
    /// // In your service / orchestration
    /// public class MyService(ID365FSCClient d365) { ... }
    /// </code>
    /// </example>
    public static IServiceCollection AddD365FSCClient(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = D365FSCOptions.SectionName)
    {
        services.Configure<D365FSCOptions>(configuration.GetSection(sectionName));
        services.AddTransient<D365AuthDelegatingHandler>();

        services.AddHttpClient<ID365FSCClient, D365FSCClient>()
            .AddHttpMessageHandler<D365AuthDelegatingHandler>()
            .AddStandardResilienceHandler(options =>
            {
                options.Retry.MaxRetryAttempts = 3;
                options.Retry.BackoffType = Polly.DelayBackoffType.Exponential;
                options.Retry.UseJitter = true;
                options.Retry.ShouldHandle = args =>
                    ValueTask.FromResult(
                        args.Outcome.Result?.StatusCode == HttpStatusCode.TooManyRequests
                        || args.Outcome.Result?.StatusCode >= HttpStatusCode.InternalServerError);
            });

        return services;
    }

    /// <summary>
    /// Creates a standalone <see cref="ID365FSCClient"/> from a raw config JSON string
    /// (for legacy/non-DI callers such as Azure Functions v4 or orchestration classes).
    /// </summary>
    public static ID365FSCClient CreateStandaloneClient(
        string configurationJson, ILogger<D365FSCClient>? logger = null)
    {
        var opts = JsonSerializer.Deserialize<D365FSCOptions>(configurationJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new ArgumentException("Invalid D365 configuration JSON.");

        var optionsWrapper = Options.Create(opts);

        var handler = new D365AuthDelegatingHandler(optionsWrapper)
        {
            InnerHandler = new HttpClientHandler()
        };

        var httpClient = new HttpClient(handler) { Timeout = opts.Timeout };

        return new D365FSCClient(
            httpClient,
            optionsWrapper,
            logger ?? NullLogger<D365FSCClient>.Instance);
    }
}
