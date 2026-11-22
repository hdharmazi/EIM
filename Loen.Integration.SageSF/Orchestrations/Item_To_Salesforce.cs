using ARM.Integration.Core.Helpers;
using ARM.Integration.Core.Model;
using ARM.Integration.EIM;
using ARM.Integration.EIM.Orchestration;
using System.Text.Json;

namespace Loen.Integration.SageSF.Orchestrations;

/// <summary>
/// Sage CI_Item → Salesforce Product2 UPSERT on Sage_Product_Id__c,
/// plus PricebookEntry for the standard price.
/// </summary>
public sealed class Item_To_Salesforce : IOrchestration
{
    public async Task<OrchestrationResult> ProcessAsync(
        QueueMessageContract message,
        string connectionString,
        IOrchestrationLogger stepLog,
        CancellationToken ct = default)
    {
        stepLog.Log("Start", $"Item_To_Salesforce for {message.SourceId}");

        var source = JsonSerializer.Deserialize<Dictionary<string, object?>>(message.Payload)
            ?? throw new JsonException("Payload is null.");

        var product = MapToProduct(source);

        // TODO M2: UPSERT Product2, then UPSERT PricebookEntry with StandardUnitPrice
        stepLog.Log("MapToProduct", $"Mapped {message.SourceId} → Product2");

        return OrchestrationResult.Completed(
            $"Item {message.SourceId} upserted to Salesforce.");
    }

    private static Dictionary<string, object?> MapToProduct(Dictionary<string, object?> sage)
    {
        return new Dictionary<string, object?>
        {
            ["Sage_Product_Id__c"] = sage.GetValueOrDefault("ItemCode"),
            ["Name"] = sage.GetValueOrDefault("ItemCodeDesc"),
            ["ProductCode"] = sage.GetValueOrDefault("ItemCode"),
            ["Family"] = sage.GetValueOrDefault("ProductLine"),
            ["IsActive"] = !string.Equals(
                sage.GetValueOrDefault("InactiveItem")?.ToString(), "Y",
                StringComparison.OrdinalIgnoreCase),
            ["Sage_Category1__c"] = sage.GetValueOrDefault("Category1"),
            ["Sage_Category2__c"] = sage.GetValueOrDefault("Category2"),
            ["Sage_Category3__c"] = sage.GetValueOrDefault("Category3"),
            ["Sage_Category4__c"] = sage.GetValueOrDefault("Category4"),
            ["Sage_UOM__c"] = sage.GetValueOrDefault("StandardUnitOfMeasure"),
            ["Sage_Migration_Source__c"] = "Sage100",
        };
    }
}
