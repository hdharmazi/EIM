namespace ARM.Integration.EIM.Utilities;

/// <summary>
/// Resolves the entity business key from D365 FSC target attributes.
/// Used at the handler level to set SourceId before DB insert and orchestration.
/// </summary>
public static class BusinessKeyResolver
{
    internal static readonly Dictionary<string, string> EntityKeyFields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mserp_wtc_custcustomerentity"]  = "mserp_customeraccount",
        ["mserp_smmcontactpersonv2entity"]  = "mserp_contactpersonid",
        ["mserp_wtc_custpostaladdressv2entity"] = "mserp_addresslocationid",
        ["mserp_wtc_custpostaladdressdeletedv2entity"] = "mserp_addresslocationid",
        ["mserp_dirpartycontactv3entity"]   = "mserp_electronicaddressid",
    };

    /// <summary>
    /// Extracts the business key value from D365 target attributes for the given entity.
    /// </summary>
    /// <param name="entityName">The D365 primary entity name (e.g., "mserp_wtc_custcustomerentity").</param>
    /// <param name="targetAttributes">Parsed target attributes from the data event, or null when the event has none.</param>
    /// <returns>
    /// The trimmed business key value, or null if the attributes are null, the entity is not registered,
    /// the key attribute is absent, or its value is null, empty, or whitespace.
    /// </returns>
    public static string? GetBusinessKey(string entityName, Dictionary<string, object>? targetAttributes)
    {
        if (targetAttributes is null)
            return null;

        if (!EntityKeyFields.TryGetValue(entityName, out var keyField))
            return null;

        if (!targetAttributes.TryGetValue(keyField, out var value))
            return null;

        var key = value?.ToString()?.Trim();
        return string.IsNullOrEmpty(key) ? null : key;
    }

    /// <summary>
    /// Returns the business key attribute registered for the given entity.
    /// </summary>
    /// <param name="entityName">The D365 primary entity name.</param>
    /// <returns>The registered attribute name, or null if the entity is not registered.</returns>
    public static string? GetKeyField(string entityName)
        => EntityKeyFields.TryGetValue(entityName, out var keyField) ? keyField : null;
}
