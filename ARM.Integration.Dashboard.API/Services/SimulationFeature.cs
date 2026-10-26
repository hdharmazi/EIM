using ARM.Integration.Dashboard.API.Controllers;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace ARM.Integration.Dashboard.API.Services;

/// <summary>
/// The Simulation console switch (<c>Simulation:Enabled</c>). Fail-safe: only a value that parses as boolean
/// <c>true</c> turns it on; absent, empty, <c>false</c> and anything unparseable mean off. Production deploys
/// <c>Simulation__Enabled=false</c>, so its dashboard cannot publish simulated messages.
/// </summary>
public static class SimulationFeature
{
    public const string SettingKey = "Simulation:Enabled";

    public static bool IsEnabled(IConfiguration config)
        => bool.TryParse(config[SettingKey], out var enabled) && enabled;
}

/// <summary>
/// Removes <see cref="SimulationController"/> when simulation is off, so every <c>/api/simulation/*</c> route
/// returns 404. Runs after the default controller provider.
/// </summary>
public sealed class SimulationControllerFeatureProvider(IConfiguration config) : IApplicationFeatureProvider<ControllerFeature>
{
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        if (SimulationFeature.IsEnabled(config)) return;

        foreach (var controller in feature.Controllers.Where(c => c.AsType() == typeof(SimulationController)).ToList())
            feature.Controllers.Remove(controller);
    }
}
