using ARM.Integration.D365FSC.Gateway.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ARM.Integration.D365FSC.Gateway.Tests;

/// <summary>
/// Spec integration-host-layout, "Fail-fast configuration validation": the gateway builds its options at
/// startup, so a missing setting stops the host naming the key rather than failing the first request.
/// </summary>
public class GatewayStartupTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void Missing_EIMDB_throws_naming_the_setting()
    {
        var config = Config(("Gateway:BaseUrl", "http://localhost:7072"));

        var ex = Assert.Throws<InvalidOperationException>(() => GatewayOptions.FromConfiguration(config));

        Assert.Contains("EIMDB", ex.Message);
    }

    [Fact]
    public void Missing_base_url_throws_naming_the_setting()
    {
        var config = Config(("EIMDB", "Server=.;Database=eimdb_test;Integrated Security=true"));

        var ex = Assert.Throws<InvalidOperationException>(() => GatewayOptions.FromConfiguration(config));

        Assert.Contains("Gateway:BaseUrl", ex.Message);
        Assert.DoesNotContain("eimdb_test", ex.Message);
    }

    [Fact]
    public void Complete_configuration_builds_options()
    {
        var config = Config(("EIMDB", "Server=.;Database=eimdb_test;Integrated Security=true"), ("Gateway:BaseUrl", "http://localhost:7072/"));

        var options = GatewayOptions.FromConfiguration(config);

        Assert.Equal("http://localhost:7072", options.NormalisedBaseUrl);
    }
}
