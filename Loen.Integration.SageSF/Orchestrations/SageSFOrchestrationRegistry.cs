using ARM.Integration.EIM.Orchestration;

namespace Loen.Integration.SageSF.Orchestrations;

/// <summary>
/// Registry of Sage↔Salesforce orchestrations, keyed by message Type.
/// Merged with Wilson's registry when the dashboard reprocesses messages.
/// </summary>
public static class SageSFOrchestrationRegistry
{
    public static IReadOnlyDictionary<string, IOrchestration> Create() =>
        new Dictionary<string, IOrchestration>(StringComparer.OrdinalIgnoreCase)
        {
            ["Customer_To_Salesforce"] = new Customer_To_Salesforce(),
            ["Contact_To_Salesforce"] = new Contact_To_Salesforce(),
            ["Item_To_Salesforce"] = new Item_To_Salesforce(),
            // Price is handled by Item_To_Salesforce (PricebookEntry upserted alongside Product2)
        };
}
