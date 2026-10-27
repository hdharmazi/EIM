using ARM.Integration.EIM.Orchestration;
using System.Text.Json.Nodes;
using Xunit;

namespace ARM.Integration.EIM.Tests;

/// <summary>
/// The action hook on <see cref="D365EventForwardOrchestration{TDest}"/>: identity by default,
/// honoured when overridden, and always overridden in turn by <c>ForcedAction</c>.
/// </summary>
public class D365EventForwardOrchestrationTests
{
    private const string Marker = "test_supersedes";

    // Real OnExternalUpdated events always carry ChangedFields; pass at least one for update-shaped payloads.
    private static string Payload(string messageName, bool marker = false, params string[] changedFields)
    {
        var attributes = new JsonArray
        {
            new JsonObject { ["key"] = "test_name", ["value"] = "Example" },
            new JsonObject { ["key"] = "test_code", ["value"] = "E1" },
        };
        if (marker)
            attributes.Add(new JsonObject { ["key"] = Marker, ["value"] = true });

        var inputs = new JsonArray
        {
            new JsonObject
            {
                ["key"] = "Target",
                ["value"] = new JsonObject { ["LogicalName"] = "test_entity", ["Attributes"] = attributes },
            },
        };
        if (changedFields.Length > 0)
            inputs.Add(new JsonObject { ["key"] = "ChangedFields", ["value"] = new JsonArray(changedFields.Select(f => (JsonNode?)f).ToArray()) });

        return new JsonObject
        {
            ["MessageName"] = messageName,
            ["PrimaryEntityName"] = "test_entity",
            ["OperationId"] = "00000000-0000-0000-0000-000000000001",
            ["OperationCreatedOn"] = "/Date(1760889600000+0000)/",
            ["InputParameters"] = inputs,
        }.ToJsonString();
    }

    [Theory]
    [InlineData("OnExternalCreated", "New")]
    [InlineData("OnExternalUpdated", "Update")]
    [InlineData("OnExternalDeleted", "Delete")]
    public void DefaultResolveAction_KeepsActionFromMessageName(string messageName, string expected)
    {
        var (action, _, _, _, _) = new Plain().Parse(Payload(messageName, marker: true, "test_name"));

        Assert.Equal(expected, action);
    }

    [Fact]
    public void OverriddenResolveAction_IsHonoured_AndSeesEventAndTargetAttributes()
    {
        var sut = new Refining();

        var (action, hasTracked, _, _, _) = sut.Parse(Payload("OnExternalCreated", marker: true));

        Assert.Equal("Update", action);
        Assert.Equal("New", sut.SeenDerivedAction);
        Assert.Equal("OnExternalCreated", sut.SeenMessageName);
        // A creation refined to Update keeps every attribute as changed, so nothing is withheld.
        Assert.True(hasTracked);
        Assert.Equal("Example", sut.MappedName);
    }

    [Fact]
    public void OverriddenResolveAction_LeavesUnmarkedCreationAsNew()
    {
        var (action, _, _, _, _) = new Refining().Parse(Payload("OnExternalCreated"));

        Assert.Equal("New", action);
    }

    [Fact]
    public void OverriddenResolveAction_RunsBeforeUpdateFieldFiltering()
    {
        var sut = new Refining();

        var (action, hasTracked, _, _, _) = sut.Parse(Payload("OnExternalUpdated", marker: true, "test_code"));

        // The override only rewrites New; a genuine update keeps its changed-field filter.
        Assert.Equal("Update", action);
        Assert.True(hasTracked);
        Assert.Null(sut.MappedName);
    }

    [Theory]
    [InlineData("OnExternalCreated", "New")]
    [InlineData("OnExternalUpdated", "Update")]
    [InlineData("OnExternalDeleted", "Delete")]
    public void ForcedAction_WinsOverResolveAction(string messageName, string derived)
    {
        var sut = new AlwaysUpdateForcedDelete();

        var (action, _, _, _, _) = sut.Parse(Payload(messageName, marker: true, "test_name"));

        // The refinement sees the message-name action (not the forced one), and the forced action
        // is applied to its result, so a refinement that always answers Update still yields Delete.
        Assert.Equal(derived, sut.SeenDerivedAction);
        Assert.Equal("Delete", action);
    }

    private class Plain : D365EventForwardOrchestration<object>
    {
        public string? MappedName { get; private set; }

        protected override string EntityName => "Test";

        protected override object MapFields(Func<string, bool, string?> getField, string action)
        {
            MappedName = getField("test_name", false);
            getField("test_code", true);
            return new object();
        }

        public (string, bool, object, string?, string?) Parse(string payload) => ParseAndMap(payload);
    }

    private sealed class Refining : Plain
    {
        public string? SeenDerivedAction { get; private set; }
        public string? SeenMessageName { get; private set; }

        protected override string ResolveAction(string derivedAction, EventContext context,
            IReadOnlyDictionary<string, object> targetAttributes)
        {
            SeenDerivedAction = derivedAction;
            SeenMessageName = context.MessageName;
            return derivedAction == "New" && targetAttributes.ContainsKey(Marker) ? "Update" : derivedAction;
        }
    }

    private sealed class AlwaysUpdateForcedDelete : Plain
    {
        public string? SeenDerivedAction { get; private set; }

        protected override string? ForcedAction => "Delete";

        protected override string ResolveAction(string derivedAction, EventContext context,
            IReadOnlyDictionary<string, object> targetAttributes)
        {
            SeenDerivedAction = derivedAction;
            return "Update";
        }
    }
}
