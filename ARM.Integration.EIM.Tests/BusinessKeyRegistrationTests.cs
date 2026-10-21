using ARM.Integration.EIM.Customer;
using ARM.Integration.EIM.Utilities;
using System.Text.Json;
using Xunit;

namespace ARM.Integration.EIM.Tests;

/// <summary>
/// Keeps the handler's entity routes and the business key registrations in step, so a routed
/// entity cannot silently fall back to the operation id as its SourceId.
/// </summary>
public class BusinessKeyRegistrationTests
{
    // Entities whose fixture predates TestData/SourceEntities; every other routed entity uses <entity>.json there.
    private static readonly Dictionary<string, string> FixtureOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mserp_wtc_custpostaladdressdeletedv2entity"] = Path.Combine("TestData", "CustomerAddressDeleted.OnExternalUpdated.json"),
    };

    private static string FixturePath(string entity) =>
        Path.Combine(AppContext.BaseDirectory, FixtureOverrides.TryGetValue(entity, out var path)
            ? path
            : Path.Combine("TestData", "SourceEntities", $"{entity}.json"));

    [Fact]
    public void EveryRoutedEntity_HasRegisteredBusinessKey()
    {
        var missing = QueueDataEventsHandler.EntityRoutes.Keys
            .Where(e => !BusinessKeyResolver.EntityKeyFields.ContainsKey(e))
            .ToList();

        Assert.True(missing.Count == 0,
            $"Routed entities with no registered business key: [{string.Join(", ", missing)}]. " +
            $"Registered: [{string.Join(", ", BusinessKeyResolver.EntityKeyFields.Keys)}].");
    }

    [Fact]
    public void EveryRegisteredBusinessKey_IsRouted()
    {
        var unrouted = BusinessKeyResolver.EntityKeyFields.Keys
            .Where(e => !QueueDataEventsHandler.EntityRoutes.ContainsKey(e))
            .ToList();

        Assert.True(unrouted.Count == 0,
            $"Registered business keys for unrouted entities: [{string.Join(", ", unrouted)}]. " +
            $"Routed: [{string.Join(", ", QueueDataEventsHandler.EntityRoutes.Keys)}].");
    }

    [Fact]
    public void EveryRoutedEntity_ResolvesBusinessKeyFromFixture()
    {
        var failures = new List<string>();

        foreach (var entity in QueueDataEventsHandler.EntityRoutes.Keys)
        {
            var path = FixturePath(entity);
            if (!File.Exists(path))
            {
                failures.Add($"{entity}: no fixture at {path}");
                continue;
            }

            var context = JsonSerializer.Deserialize<EventContext>(File.ReadAllText(path))!;
            var key = BusinessKeyResolver.GetBusinessKey(
                context.PrimaryEntityName, D365FSCEventParser.GetTargetAttributes(context.InputParameters));

            if (string.IsNullOrEmpty(key))
                failures.Add($"{entity}: business key did not resolve from its fixture");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    public static TheoryData<string> RoutedEntities()
    {
        var data = new TheoryData<string>();
        foreach (var entity in QueueDataEventsHandler.EntityRoutes.Keys)
            data.Add(entity);
        return data;
    }

    [Theory]
    [MemberData(nameof(RoutedEntities))]
    public void GetBusinessKey_NullTargetAttributes_ReturnsNull(string entity)
    {
        Assert.Null(BusinessKeyResolver.GetBusinessKey(entity, null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void GetBusinessKey_BlankOrWhitespaceValue_ReturnsNull(string? value)
    {
        var attrs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["mserp_addresslocationid"] = value!,
        };

        Assert.Null(BusinessKeyResolver.GetBusinessKey("mserp_wtc_custpostaladdressv2entity", attrs));
    }

    [Fact]
    public void GetBusinessKey_UnregisteredEntity_ReturnsNull()
    {
        var attrs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["mserp_addresslocationid"] = "000146087",
        };

        Assert.Null(BusinessKeyResolver.GetBusinessKey("mserp_unregisteredentity", attrs));
    }

    [Fact]
    public void GetKeyField_ReturnsRegisteredAttribute()
    {
        Assert.Equal("mserp_addresslocationid", BusinessKeyResolver.GetKeyField("mserp_wtc_custpostaladdressv2entity"));
        Assert.Null(BusinessKeyResolver.GetKeyField("mserp_unregisteredentity"));
    }

    [Fact]
    public void GetTargetAttributes_NoTargetParameter_ReturnsNull()
    {
        var inputs = new List<InputParameter>
        {
            new() { Key = "ChangedFields", Value = JsonDocument.Parse("[\"mserp_addresscity\"]").RootElement },
        };

        Assert.Null(D365FSCEventParser.GetTargetAttributes(inputs));
    }

    [Fact]
    public void GetTargetAttributes_TargetWithoutValue_ReturnsNull()
    {
        var inputs = new List<InputParameter> { new() { Key = "Target" } };

        Assert.Null(D365FSCEventParser.GetTargetAttributes(inputs));
    }

    [Fact]
    public void GetTargetAttributes_TargetValueNull_ReturnsNull()
    {
        var inputs = JsonSerializer.Deserialize<List<InputParameter>>("[{\"key\":\"Target\",\"value\":null}]")!;

        Assert.Null(D365FSCEventParser.GetTargetAttributes(inputs));
    }

    [Fact]
    public void GetTargetAttributes_TargetWithoutAttributes_ReturnsNull()
    {
        var inputs = new List<InputParameter>
        {
            new() { Key = "Target", Value = JsonDocument.Parse("{\"Id\":\"00000000-0000-0000-0000-000000000000\"}").RootElement },
        };

        Assert.Null(D365FSCEventParser.GetTargetAttributes(inputs));
    }
}
