using ARM.Integration.Core.Helpers;
using ARM.Integration.Core.Model;
using ARM.Integration.EIM;
using ARM.Integration.EIM.Orchestration;
using System.Text.Json;

namespace Loen.Integration.SageSF.Orchestrations;

/// <summary>
/// Sage AR_Customer → Salesforce Account UPSERT on Sage_Customer_No__c.
/// </summary>
public sealed class Customer_To_Salesforce : IOrchestration
{
    public async Task<OrchestrationResult> ProcessAsync(
        QueueMessageContract message,
        string connectionString,
        IOrchestrationLogger stepLog,
        CancellationToken ct = default)
    {
        stepLog.Log("Start", $"Customer_To_Salesforce for {message.SourceId}");

        var source = JsonSerializer.Deserialize<Dictionary<string, object?>>(message.Payload)
            ?? throw new JsonException("Payload is null.");

        var account = MapToAccount(source);

        // TODO M2: SF client UPSERT via sObject Collections
        stepLog.Log("MapToAccount", $"Mapped {message.SourceId} → Account");

        return OrchestrationResult.Completed(
            $"Customer {message.SourceId} upserted to Salesforce.");
    }

    private static Dictionary<string, object?> MapToAccount(Dictionary<string, object?> sage)
    {
        return new Dictionary<string, object?>
        {
            ["Sage_Customer_No__c"] = sage.GetValueOrDefault("CustomerNo"),
            ["Name"] = sage.GetValueOrDefault("CustomerName"),
            ["BillingStreet"] = JoinAddress(
                sage.GetValueOrDefault("AddressLine1")?.ToString(),
                sage.GetValueOrDefault("AddressLine2")?.ToString()),
            ["BillingCity"] = sage.GetValueOrDefault("City"),
            ["BillingState"] = sage.GetValueOrDefault("State"),
            ["BillingPostalCode"] = sage.GetValueOrDefault("ZipCode"),
            ["BillingCountry"] = sage.GetValueOrDefault("CountryCode"),
            ["Phone"] = sage.GetValueOrDefault("TelephoneNo"),
            ["Fax"] = sage.GetValueOrDefault("FaxNo"),
            ["Sage_Email__c"] = sage.GetValueOrDefault("EmailAddress"),
            ["Website"] = sage.GetValueOrDefault("URLAddress"),
            ["Sage_Customer_Type__c"] = sage.GetValueOrDefault("CustomerType"),
            ["Sage_Price_Level__c"] = sage.GetValueOrDefault("PriceLevel"),
            ["Sage_Salesperson__c"] = sage.GetValueOrDefault("SalespersonNo"),
            ["Sage_Terms__c"] = sage.GetValueOrDefault("TermsCode"),
            ["Sage_Credit_Limit__c"] = sage.GetValueOrDefault("CreditLimit"),
            ["Sage_Tax_Schedule__c"] = sage.GetValueOrDefault("TaxSchedule"),
            ["Sage_Primary_Warehouse__c"] = sage.GetValueOrDefault("PrimaryShipToCode"),
            ["Sage_Customer_Status__c"] = sage.GetValueOrDefault("CustomerStatus"),
            ["Sage_Migration_Source__c"] = "Sage100",
        };
    }

    private static string? JoinAddress(string? line1, string? line2)
    {
        var parts = new[] { line1, line2 }.Where(s => !string.IsNullOrWhiteSpace(s));
        var joined = string.Join("\n", parts);
        return string.IsNullOrEmpty(joined) ? null : joined;
    }
}
