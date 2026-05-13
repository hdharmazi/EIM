using ARM.Integration.Core.Helpers;
using ARM.Integration.Core.Model;
using System.Text.Json;

namespace ARM.Integration.EIM.Orchestration;

/// <summary>
/// D365 FSC-specific CRUD orchestration that executes Create/Update/Delete
/// against a D365 Finance &amp; Supply Chain OData endpoint via <see cref="ID365FSCClient"/>.
/// 
/// Use this as the base class when the TARGET system is D365 FSC.
/// For non-D365 targets (e.g., Shopify REST, SAP RFC), extend <see cref="BaseCrudOrchestration{TSource,TDest}"/> directly.
/// </summary>
/// <typeparam name="TSource">The source system message contract type.</typeparam>
/// <typeparam name="TD365">The D365 FSC entity type (e.g., CustomersEntity).</typeparam>
public abstract class D365CrudOrchestration<TSource, TD365> : BaseCrudOrchestration<TSource, TD365>
    where TSource : class
    where TD365 : class
{
    /// <summary>The D365 OData entity set name (e.g., "CustomersV3", "ContactPersonV2Entity").</summary>
    protected abstract string D365EntityName { get; }

    /// <summary>Builds the OData entity key for Update/Delete operations.</summary>
    protected abstract EntityKey BuildEntityKey(TD365 d365Entity);

    protected override async Task<TD365?> ExecuteActionAsync(
        string action, TD365 target, string destinationConnection,
        IOrchestrationLogger stepLog, CancellationToken ct)
    {
        var d365Client = D365FSCServiceCollectionExtensions.CreateStandaloneClient(destinationConnection);

        if (action.Equals("New", StringComparison.OrdinalIgnoreCase))
        {
            stepLog.Log($"Create{ResponseEntityName}");
            var createResult = await d365Client.CreateRawAsync(
                D365EntityName, JsonSerializer.Serialize(target), ct);
            stepLog.Log($"Create{ResponseEntityName}", "Succeeded");

            return JsonSerializer.Deserialize<TD365>(createResult);
        }
        else if (action.Equals("Update", StringComparison.OrdinalIgnoreCase))
        {
            var key = BuildEntityKey(target);
            stepLog.Log($"Update{ResponseEntityName}", $"Keys: {key}");
            await d365Client.UpdateRawAsync(
                D365EntityName, key, JsonSerializer.Serialize(target), ct);
            stepLog.Log($"Update{ResponseEntityName}", "Succeeded");

            return null;
        }
        else if (action.Equals("Delete", StringComparison.OrdinalIgnoreCase))
        {
            var key = BuildEntityKey(target);
            stepLog.Log($"Delete{ResponseEntityName}", $"Keys: {key}");
            await d365Client.DeleteAsync(D365EntityName, key, ct);
            stepLog.Log($"Delete{ResponseEntityName}", "Succeeded");

            return null;
        }
        else
        {
            stepLog.LogWarning("UnsupportedAction", action);
            return null;
        }
    }
}
