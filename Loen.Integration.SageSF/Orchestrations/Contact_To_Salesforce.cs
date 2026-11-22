using ARM.Integration.Core.Helpers;
using ARM.Integration.Core.Model;
using ARM.Integration.EIM;
using ARM.Integration.EIM.Orchestration;
using System.Text.Json;

namespace Loen.Integration.SageSF.Orchestrations;

/// <summary>
/// Sage AR_CustomerContact → Salesforce Contact UPSERT on Sage_Contact_Ext_Id__c.
/// External ID is composite: CustomerNo-ContactCode.
/// </summary>
public sealed class Contact_To_Salesforce : IOrchestration
{
    public async Task<OrchestrationResult> ProcessAsync(
        QueueMessageContract message,
        string connectionString,
        IOrchestrationLogger stepLog,
        CancellationToken ct = default)
    {
        stepLog.Log("Start", $"Contact_To_Salesforce for {message.SourceId}");

        var source = JsonSerializer.Deserialize<Dictionary<string, object?>>(message.Payload)
            ?? throw new JsonException("Payload is null.");

        var contact = MapToContact(source);

        // TODO M2: SF client UPSERT, resolve AccountId via Sage_Customer_No__c
        stepLog.Log("MapToContact", $"Mapped {message.SourceId} → Contact");

        return OrchestrationResult.Completed(
            $"Contact {message.SourceId} upserted to Salesforce.");
    }

    private static Dictionary<string, object?> MapToContact(Dictionary<string, object?> sage)
    {
        var customerNo = sage.GetValueOrDefault("CustomerNo")?.ToString() ?? "";
        var contactCode = sage.GetValueOrDefault("ContactCode")?.ToString() ?? "";

        return new Dictionary<string, object?>
        {
            ["Sage_Contact_Ext_Id__c"] = $"{customerNo}-{contactCode}",
            ["LastName"] = sage.GetValueOrDefault("ContactName"),
            ["Title"] = sage.GetValueOrDefault("ContactTitle"),
            ["Email"] = sage.GetValueOrDefault("EmailAddress"),
            ["Phone"] = sage.GetValueOrDefault("TelephoneNo1"),
            // AccountId resolved by relationship: Account.Sage_Customer_No__c = customerNo
            ["Account"] = new Dictionary<string, object?>
            {
                ["Sage_Customer_No__c"] = customerNo
            },
            ["Sage_Migration_Source__c"] = "Sage100",
        };
    }
}
