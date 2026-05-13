using System.Text.Json.Serialization;

namespace ARM.Integration.Core.Helpers;

/// <summary>
/// Represents a D365 OData collection response (<c>{ "value": [...] }</c>).
/// </summary>
public sealed class D365ODataResponse<T>
{
    [JsonPropertyName("value")]
    public List<T> Value { get; set; } = [];

    [JsonPropertyName("@odata.nextLink")]
    public string? NextLink { get; set; }
}
