namespace ARM.Integration.EIM.Utilities;

/// <summary>
/// Resolves the entity business key from D365 FSC target attributes.
/// Used at the handler level to set SourceId before DB insert and orchestration.
/// </summary>
public static class BusinessKeyResolver
{
    private static readonly Dictionary<string, string> EntityKeyFields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mserp_custcustomerv3entity"]     = "mserp_customeraccount",
        ["mserp_smmcontactpersonv2entity"]  = "mserp_contactpersonid",
        ["mserp_custpostaladdressv2entity"] = "mserp_addresslocationid",
        ["mserp_wtc_custpostaladdressdeletedv2entity"] = "mserp_addresslocationid",
        ["mserp_dirpartycontactv3entity"]   = "mserp_electronicaddressid",
    };

    /// <summary>
    /// Extracts the business key value from D365 target attributes for the given entity.
    /// </summary>
    /// <param name="entityName">The D365 primary entity name (e.g., "mserp_custcustomerv3entity").</param>
    /// <param name="targetAttributes">Parsed target attributes from the data event.</param>
    /// <returns>The business key value, or null if the entity or field is not found.</returns>
    public static string? GetBusinessKey(string entityName, Dictionary<string, object> targetAttributes)
    {
        if (!EntityKeyFields.TryGetValue(entityName, out var keyField))
            return null;

        if (!targetAttributes.TryGetValue(keyField, out var value))
            return null;

        return value?.ToString()?.Trim();
    }
}
