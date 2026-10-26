using System.Security.Claims;
using System.Text.Json;
using ARM.Integration.Dashboard.API.Controllers;
using ARM.Integration.Dashboard.API.Models;
using ARM.Integration.Dashboard.API.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ARM.Integration.D365FSC.Gateway.Tests;

/// <summary>
/// Spec simulation-console: the dashboard API's Simulation:Enabled switch. Off unless it is exactly a true boolean;
/// when off, the Simulation controller is not served, the service publishes nothing, and /api/me says so.
/// </summary>
public class SimulationSwitchTests
{
    // Syntactically invalid, so creating a Service Bus client from it would throw. Holds no real value.
    private const string FakeServiceBusConnection = "not-a-connection-string";

    private static IConfiguration Config(string? enabled)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:EIMDB"] = "unused",
            ["Simulation:ServiceBusConnectionString"] = FakeServiceBusConnection,
        };
        if (enabled is not null)
            values[SimulationFeature.SettingKey] = enabled;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("false", false)]
    [InlineData("yes", false)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    public void Simulation_is_on_only_for_a_true_boolean(string? value, bool expected)
    {
        Assert.Equal(expected, SimulationFeature.IsEnabled(Config(value)));
    }

    private static IReadOnlyList<Type> Controllers(string? enabled)
    {
        var manager = new ApplicationPartManager();
        manager.ApplicationParts.Add(new AssemblyPart(typeof(SimulationController).Assembly));
        manager.FeatureProviders.Add(new ControllerFeatureProvider());
        manager.FeatureProviders.Add(new SimulationControllerFeatureProvider(Config(enabled)));

        var feature = new ControllerFeature();
        manager.PopulateFeature(feature);
        return feature.Controllers.Select(c => c.AsType()).ToList();
    }

    [Fact]
    public void Disabled_removes_only_the_simulation_controller()
    {
        var controllers = Controllers(enabled: null);

        Assert.DoesNotContain(typeof(SimulationController), controllers);
        Assert.Contains(typeof(MeController), controllers);
    }

    [Fact]
    public void Enabled_serves_the_simulation_controller()
    {
        var controllers = Controllers(enabled: "true");

        Assert.Contains(typeof(SimulationController), controllers);
        Assert.Contains(typeof(MeController), controllers);
    }

    [Fact]
    public async Task Disabled_service_yields_one_error_and_publishes_nothing()
    {
        var config = Config(enabled: null);
        var service = new SimulationService(config, new ConfigurationRepository(config));
        // An environment and a scenario that would reach the database and Service Bus if the guard were missing.
        var request = new SimulationRequest { Scenario = "d365-customer-create", Environment = "BAT" };

        var events = new List<SimulationEvent>();
        await foreach (var e in service.RunAsync(request))
            events.Add(e);

        var only = Assert.Single(events);
        Assert.Equal("error", only.Level);
        Assert.Contains("disabled", only.Text, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement Me(string? enabled)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("DisplayName", "Test User"),
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim(ClaimTypes.Role, "Operator"),
            new Claim("DbUserValidated", "true"),
        ], "Test"));
        var controller = new MeController(Config(enabled))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } },
        };

        var ok = Assert.IsType<OkObjectResult>(controller.Get());
        return JsonSerializer.SerializeToElement(ok.Value);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("true", true)]
    public void Me_reports_the_simulation_feature_and_keeps_its_fields(string? enabled, bool expected)
    {
        var me = Me(enabled);

        Assert.Equal(expected, me.GetProperty("features").GetProperty("simulation").GetBoolean());
        Assert.Equal("Test User", me.GetProperty("name").GetString());
        Assert.Equal(["Admin", "Operator"], me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.True(me.GetProperty("dbUserValidated").GetBoolean());
    }

    private static string ApiFile(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ARM.Integration.Wilson.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "ARM.Integration.Dashboard.API", name);
    }

    [Theory]
    [InlineData("appsettings.json", false)]
    [InlineData("appsettings.Development.json", true)]
    public void Committed_settings_turn_simulation_off_by_default_and_on_for_local_development(string file, bool expected)
    {
        var config = new ConfigurationBuilder().AddJsonFile(ApiFile(file), optional: false).Build();

        Assert.Equal(expected.ToString(), config[SimulationFeature.SettingKey], ignoreCase: true);
        Assert.Equal(expected, SimulationFeature.IsEnabled(config));
    }
}
