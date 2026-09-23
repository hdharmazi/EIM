using ARM.Integration.Dashboard.API.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ARM.Integration.D365FSC.Gateway.Tests;

public class MessageQueueRepositoryRegistrationTests
{
    /// <summary>
    /// The cleanup tuning parameters are optional ints nothing registers, so the container must fall
    /// back to their defaults rather than failing on the first request.
    /// </summary>
    [Fact]
    public void Resolves_from_the_container_without_registering_cleanup_tuning()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:EIMDB"] = "unused" })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddScoped<MessageQueueRepository>();

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<MessageQueueRepository>());
    }
}
