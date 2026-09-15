using ARM.Integration.D365FSC.Gateway.Routing;
using Xunit;

namespace ARM.Integration.D365FSC.Gateway.Tests;

public class GatewayPathParserTests
{
    [Theory]
    [InlineData("CustomersV3", "CustomersV3", "")]
    [InlineData("SalesOrderHeadersV2", "SalesOrderHeadersV2", "")]
    [InlineData("WTC_CustPostalAddress", "WTC_CustPostalAddress", "")]
    [InlineData("CustomersV3(dataAreaId='usmf',CustomerAccount='C001')", "CustomersV3", "(dataAreaId='usmf',CustomerAccount='C001')")]
    [InlineData("CustomersV3('C-001')", "CustomersV3", "('C-001')")]
    public void Accepts_well_formed_entity_paths(string raw, string expectedName, string expectedPredicate)
    {
        var result = GatewayPathParser.ParseDataPath(raw);

        Assert.True(result.IsSuccess, result.Failure);
        Assert.Equal(GatewayResourceType.Data, result.Resource!.Type);
        Assert.Equal(expectedName, result.Resource.Name);
        Assert.Equal(expectedPredicate, result.Resource.KeyPredicate);
    }

    [Fact]
    public void Bound_action_authorizes_on_entity_and_action()
    {
        var result = GatewayPathParser.ParseDataPath(
            "SalesOrderHeadersV2(dataAreaId='usmf',SalesOrderNumber='SO-1')/Microsoft.Dynamics.DataEntities.Confirm");

        Assert.True(result.IsSuccess, result.Failure);
        Assert.Equal(GatewayResourceType.Action, result.Resource!.Type);
        Assert.Equal("SalesOrderHeadersV2/Confirm", result.Resource.Name);
        Assert.Equal("Microsoft.Dynamics.DataEntities.", result.Resource.ActionNamespace);
    }

    [Fact]
    public void Unqualified_bound_action_is_accepted()
    {
        var result = GatewayPathParser.ParseDataPath("SalesOrderHeadersV2/Confirm");

        Assert.True(result.IsSuccess, result.Failure);
        Assert.Equal("SalesOrderHeadersV2/Confirm", result.Resource!.Name);
        Assert.Null(result.Resource.ActionNamespace);
    }

    [Theory]
    // Encoded separators — the classic way to make the authorizer and the upstream disagree.
    [InlineData("CustomersV3%2FMicrosoft.Dynamics.DataEntities.Delete")]
    [InlineData("CustomersV3%2fConfirm")]
    [InlineData("CustomersV3%5CConfirm")]
    [InlineData("Customers%2E%2E%2FSalesOrderHeadersV2")]
    // Double-encoding.
    [InlineData("CustomersV3%252FConfirm")]
    // Traversal and separator abuse.
    [InlineData("../SalesOrderHeadersV2")]
    [InlineData("CustomersV3/../SalesOrderHeadersV2")]
    [InlineData("CustomersV3//Confirm")]
    [InlineData("CustomersV3\\Confirm")]
    [InlineData("./CustomersV3")]
    // System resources: $batch bundles sub-requests this layer never sees.
    [InlineData("$batch")]
    [InlineData("$metadata")]
    [InlineData("$crossCompany")]
    // Structural nonsense.
    [InlineData("CustomersV3(dataAreaId='usmf'")]
    [InlineData("CustomersV3)")]
    [InlineData("CustomersV3(a)(b")]
    [InlineData("CustomersV3/Confirm/Extra")]
    [InlineData("CustomersV3/")]
    [InlineData("/")]
    [InlineData("")]
    [InlineData("   ")]
    // Name-shape violations.
    [InlineData("3CustomersV3")]
    [InlineData("Customers-V3")]
    [InlineData("Customers V3")]
    [InlineData("CustomersV3/Confirm.")]
    [InlineData("CustomersV3/..Confirm")]
    [InlineData("CustomersV3%00")]
    public void Rejects_hostile_or_malformed_paths(string raw)
    {
        var result = GatewayPathParser.ParseDataPath(raw);

        Assert.False(result.IsSuccess, $"expected rejection but parsed as '{result.Resource?.Name}'");
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public void Rejects_paths_beyond_the_length_limit()
    {
        Assert.False(GatewayPathParser.ParseDataPath(new string('a', 2000)).IsSuccess);
    }

    [Fact]
    public void Control_characters_are_rejected()
    {
        Assert.False(GatewayPathParser.ParseDataPath("CustomersV3\r\nX-Injected: 1").IsSuccess);
    }

    [Theory]
    [InlineData("WTCServices", "CustomerService", "CreateCustomer")]
    [InlineData("Grp_1", "Svc_2", "Op_3")]
    public void Accepts_well_formed_service_paths(string group, string service, string operation)
    {
        var result = GatewayPathParser.ParseServicePath(group, service, operation);

        Assert.True(result.IsSuccess, result.Failure);
        Assert.Equal(GatewayResourceType.Service, result.Resource!.Type);
        Assert.Equal($"{group}/{service}/{operation}", result.Resource.Name);
    }

    [Theory]
    [InlineData("WTC/Services", "CustomerService", "CreateCustomer")]
    [InlineData("WTCServices", "Customer%2FService", "CreateCustomer")]
    [InlineData("WTCServices", "CustomerService", "..")]
    [InlineData("", "CustomerService", "CreateCustomer")]
    [InlineData("WTCServices", "", "CreateCustomer")]
    [InlineData("WTCServices", "CustomerService", "")]
    [InlineData("WTCServices", "CustomerService", "Create.Customer")]
    public void Rejects_malformed_service_paths(string group, string service, string operation)
    {
        Assert.False(GatewayPathParser.ParseServicePath(group, service, operation).IsSuccess);
    }

    [Fact]
    public void Entity_name_never_contains_percent_encoding()
    {
        // The authorized substring must be literal, so no decoding step can change what it means
        // after the decision has been made.
        foreach (var raw in new[] { "Customers%563", "%43ustomersV3", "CustomersV3%20" })
        {
            var result = GatewayPathParser.ParseDataPath(raw);
            Assert.False(result.IsSuccess, $"'{raw}' should not parse");
        }
    }
}
