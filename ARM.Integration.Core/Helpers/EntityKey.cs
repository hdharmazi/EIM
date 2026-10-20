namespace ARM.Integration.Core.Helpers;

/// <summary>
/// Immutable builder for OData entity key segments.
/// <example>
/// <code>
/// var key = EntityKey.Create("dataAreaId", "WTC").Add("CustomerAccount", "279896");
/// // produces: dataAreaId='WTC',CustomerAccount='279896'
/// </code>
/// </example>
/// </summary>
public sealed class EntityKey
{
    private readonly List<KeyValuePair<string, string>> _segments = [];

    private EntityKey() { }

    public static EntityKey Create(string key, string value)
    {
        var ek = new EntityKey();
        return ek.Add(key, value);
    }

    public EntityKey Add(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new MissingEntityKeyException(key);

        _segments.Add(new(key, value));
        return this;
    }

    public override string ToString() =>
        string.Join(",", _segments.Select(s => $"{s.Key}='{Uri.EscapeDataString(s.Value)}'"));
}
