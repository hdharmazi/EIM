using System.Reflection;
using Microsoft.Azure.Functions.Worker;
using Wilson.Integration.Automation.Functions;
using Xunit;

namespace Wilson.Integration.Tests;

/// <summary>
/// Spec integration-host-layout: function names are stable, and no inactive trigger template ships.
/// A function name is what the host indexes, what <c>AzureWebJobs.&lt;name&gt;.Disabled</c> gates and
/// what the deployment script lists, so a rename is an operational change rather than a refactor.
/// </summary>
public class AutomationFunctionNamesTests
{
    private const string RenameHint =
        "Function names are listed in automation.gatedFunctions in script/deployment/config/deploy.base.json; "
        + "a rename must change that list and the app's AzureWebJobs.<name>.Disabled settings together.";

    private static readonly Assembly Automation = typeof(QueueDataEventsHandler).Assembly;

    private static readonly string[] ExpectedNames =
    [
        "AwaitingD365SweepHandler",
        "QueueCaptureCustomer",
        "QueueDataEventsHandler",
        "QueueMessageQueueBusinessEventHandler",
        "ReleaseSweepHandler",
        "RetrySweepHandler",
    ];

    private static IReadOnlyList<(string Name, MethodInfo Method)> Functions() =>
        Automation.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Select(m => (Attribute: m.GetCustomAttribute<FunctionAttribute>(), Method: m))
            .Where(x => x.Attribute is not null)
            .Select(x => (x.Attribute!.Name, x.Method))
            .ToList();

    private static MethodInfo Function(string name) =>
        Functions().SingleOrDefault(f => f.Name == name).Method
        ?? throw new Xunit.Sdk.XunitException($"Function '{name}' not found. {RenameHint}");

    [Fact]
    public void Exactly_the_six_function_names_exist()
    {
        var actual = Functions().Select(f => f.Name).Order(StringComparer.Ordinal).ToArray();

        Assert.True(ExpectedNames.SequenceEqual(actual),
            $"Expected [{string.Join(", ", ExpectedNames)}] but found [{string.Join(", ", actual)}]. {RenameHint}");
    }

    [Theory]
    [InlineData("QueueCaptureCustomer", "%CaptureToD365Queue%")]
    [InlineData("QueueDataEventsHandler", "%D365FscDataEventQueue%")]
    [InlineData("QueueMessageQueueBusinessEventHandler", "%D365BusinessEventQueue%")]
    public void Service_bus_triggers_bind_the_expected_queue_and_connection(string function, string queueExpression)
    {
        var trigger = Function(function).GetParameters()
            .Select(p => p.GetCustomAttribute<ServiceBusTriggerAttribute>())
            .SingleOrDefault(a => a is not null);

        Assert.NotNull(trigger);
        Assert.Equal(queueExpression, trigger.QueueName);
        Assert.Equal("ServiceBus", trigger.Connection);
    }

    [Theory]
    [InlineData("AwaitingD365SweepHandler", "%AwaitingD365SweepSchedule%")]
    [InlineData("ReleaseSweepHandler", "%ReleaseSweepSchedule%")]
    [InlineData("RetrySweepHandler", "%RetrySweepSchedule%")]
    public void Timer_triggers_bind_the_expected_schedule(string function, string scheduleExpression)
    {
        var trigger = Function(function).GetParameters()
            .Select(p => p.GetCustomAttribute<TimerTriggerAttribute>())
            .SingleOrDefault(a => a is not null);

        Assert.NotNull(trigger);
        Assert.Equal(scheduleExpression, trigger.Schedule);
    }

    [Fact]
    public void No_inactive_polling_trigger_template_exists()
    {
        Assert.DoesNotContain(Automation.GetTypes(), t => t.Name == "PollSourceHandler");
    }
}
