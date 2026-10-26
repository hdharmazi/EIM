using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Azure.Functions.Worker;
using Wilson.Integration.Orchestrations.Configuration;
using Xunit;

namespace Wilson.Integration.Tests;

/// <summary>
/// Spec deployment-automation: the Service Bus queues are listed in <c>serviceBusQueues</c>. Deploy-Wilson.ps1 creates
/// <c>&lt;queuePrefix&gt;-&lt;name&gt;</c> for each entry and writes its <c>setting</c> on the Automation app, so the list,
/// <see cref="RequiredSettings.Queues"/> and the Automation triggers must agree.
/// </summary>
public partial class DeploymentConfigTests
{
    // Today's queues, in order; changing them renames production queues (a new queue is created, the old one stays).
    private static readonly (string Name, string Setting)[] ShippedQueues =
    [
        ("d365fsc-dataevent", "D365FscDataEventQueue"),
        ("d365fsc-businessevents", "D365BusinessEventQueue"),
        ("d365fsc-to-capture", "D365ToCaptureQueue"),
        ("capture-to-d365fsc", "CaptureToD365Queue"),
        ("capture-to-d365fsc-response", "CaptureToD365QueueResponse"),
    ];

    private static JsonArray QueueList(JsonObject config, string source)
    {
        Assert.True(config["serviceBusQueues"] is JsonArray, $"{source}: serviceBusQueues must be a list");
        var list = config["serviceBusQueues"]!.AsArray();
        Assert.NotEmpty(list);
        return list;
    }

    [Fact]
    public void Every_configuration_lists_the_required_service_bus_queues()
    {
        foreach (var name in ConfigurationNames())
        {
            var queues = QueueList(Merged(name), name);
            var names = queues.Select(q => Text(q, "name")).ToList();
            var settings = queues.Select(q => Text(q, "setting")).Where(s => s is not null).Cast<string>().ToList();

            Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n), $"{name}: every serviceBusQueues entry has a name"));
            Assert.True(names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Count,
                $"{name}: serviceBusQueues names must be unique (case-insensitive): {string.Join(", ", names)}");
            Assert.True(settings.Distinct(StringComparer.OrdinalIgnoreCase).Count() == settings.Count,
                $"{name}: serviceBusQueues settings must be unique (case-insensitive): {string.Join(", ", settings)}");
            Assert.All(RequiredSettings.Queues, required => Assert.True(settings.Contains(required, StringComparer.Ordinal),
                $"{name}: serviceBusQueues has no entry with the setting '{required}', which the Automation app requires"));
        }
    }

    [Fact]
    public void Shipped_service_bus_queues_are_pinned()
    {
        var queues = QueueList(LoadObject("deploy.base.json"), "deploy.base.json");

        Assert.Equal(ShippedQueues, queues.Select(q => (Text(q, "name") ?? "", Text(q, "setting") ?? "")).ToArray());
        Assert.All(queues, q => Assert.NotEqual(true, Flag(q, "requiresSession")));
        Assert.All(queues, q => Assert.NotEqual(false, Flag(q, "duplicateDetection")));
    }

    [Fact]
    public void Every_service_bus_trigger_binds_to_a_configured_queue_setting()
    {
        var settings = QueueList(LoadObject("deploy.base.json"), "deploy.base.json")
            .Select(q => Text(q, "setting"))
            .Where(s => s is not null)
            .ToHashSet(StringComparer.Ordinal);

        var triggers = AutomationFunctionNamesTests.Functions()
            .SelectMany(f => f.Method.GetParameters()
                .Select(p => p.GetCustomAttribute<ServiceBusTriggerAttribute>())
                .Where(a => a is not null)
                .Select(a => (Function: f.Name, Trigger: a!)))
            .ToList();

        Assert.NotEmpty(triggers);
        foreach (var (function, trigger) in triggers)
        {
            var queue = trigger.QueueName ?? "";
            Assert.True(queue.Length > 2 && queue.StartsWith('%') && queue.EndsWith('%'),
                $"{function} binds to '{queue}'; a Service Bus trigger names its queue through a %setting% expression");
            Assert.True(settings.Contains(queue[1..^1]),
                $"{function} binds to '{queue}', but no serviceBusQueues entry in deploy.base.json has the setting '{queue[1..^1]}'");
        }
    }
}
