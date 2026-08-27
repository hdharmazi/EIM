using ARM.Integration.EIM;
using ARM.Integration.EIM.Orchestration;
namespace Wilson.Integration.Orchestrations;

/// <summary>
/// Central factory for the message-type → orchestration mapping used for message
/// dispatch and reprocessing. Keeping a single source here prevents the map from
/// drifting between the Functions host and the Dashboard API (which reprocesses).
/// </summary>
public static class OrchestrationRegistry
{
    /// <summary>Builds the default set of registered orchestrations keyed by message Type.</summary>
    public static IReadOnlyDictionary<string, IOrchestration> CreateDefault() =>
        new Dictionary<string, IOrchestration>(StringComparer.OrdinalIgnoreCase)
        {
            ["Customer_To_Capture"] = new Customer_To_Capture(),
            ["Customer_To_D365FSC"] = new Customer_To_D365FSC(),
            ["Contact_To_Capture"] = new Contact_To_Capture(),
            ["Contact_To_D365FSC"] = new Contact_To_D365FSC(),
            ["PartyContact_To_D365FSC"] = new PartyContact_To_D365FSC(),
            ["CustomerAddress_To_Capture"] = new CustomerAddress_To_Capture(),
            ["CustomerAddress_To_D365FSC"] = new CustomerAddress_To_D365FSC(),
            ["PartyContact_To_Capture"] = new PartyContact_To_Capture(),
            ["SalesOrder_To_D365FSC"] = new SalesOrder_To_D365FSC(),
            ["Sample"] = new Sample(),
        };
}
