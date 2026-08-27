using System.Reflection;
using ARM.Integration.EIM;
using ARM.Integration.EIM.Orchestration;
using Wilson.Integration.Orchestrations;
using Xunit;

namespace ARM.Integration.EIM.Tests;

/// <summary>
/// Guards the one-way dependency between the engine and client-owned orchestrations.
/// Without this, the next convenience reference in engine code silently restores the cycle
/// and nobody finds out until the assemblies are split again.
/// </summary>
public class EngineBoundaryTests
{
    private static readonly Assembly Engine = typeof(EIMController).Assembly;
    private static readonly Assembly Client = typeof(OrchestrationRegistry).Assembly;

    [Fact]
    public void Engine_does_not_reference_the_client_assembly()
    {
        var referenced = Engine.GetReferencedAssemblies().Select(a => a.Name).ToArray();

        Assert.DoesNotContain(Client.GetName().Name, referenced);
    }

    [Fact]
    public void Client_references_the_engine()
    {
        var referenced = Client.GetReferencedAssemblies().Select(a => a.Name).ToArray();

        Assert.Contains(Engine.GetName().Name, referenced);
    }

    [Fact]
    public void Engine_declares_no_concrete_orchestration()
    {
        var concrete = Engine.GetTypes()
            .Where(t => typeof(IOrchestration).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .Select(t => t.FullName)
            .ToArray();

        Assert.Empty(concrete);
    }

    [Fact]
    public void Every_registered_orchestration_is_client_owned()
    {
        foreach (var (type, orchestration) in OrchestrationRegistry.CreateDefault())
        {
            Assert.Equal(Client, orchestration.GetType().Assembly);
            Assert.False(string.IsNullOrWhiteSpace(type));
        }
    }

    [Fact]
    public void Registry_keys_match_their_implementation_type_names()
    {
        // Dispatch is by key, but a key that disagrees with its class name makes a message's
        // TYPE column impossible to trace back to code.
        foreach (var (key, orchestration) in OrchestrationRegistry.CreateDefault())
        {
            Assert.Equal(orchestration.GetType().Name, key);
        }
    }
}
