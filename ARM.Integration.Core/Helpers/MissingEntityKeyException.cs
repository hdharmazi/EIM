namespace ARM.Integration.Core.Helpers;

/// <summary>
/// Thrown when an OData entity key is built from a null or blank value, so the source message
/// cannot identify the D365 record to update or delete.
/// </summary>
public sealed class MissingEntityKeyException(string keyField)
    : InvalidOperationException($"Required key field '{keyField}' is missing from the source message.")
{
    public string KeyField { get; } = keyField;
}
