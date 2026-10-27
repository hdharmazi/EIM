using System.Text.Json.Serialization;

namespace ARM.Integration.D365FSC.SampleEngApp.Models;

/// <summary>Envelope returned by D365 F&amp;O OData collection queries.</summary>
public sealed record ODataCollectionResponse<T>
{
    [JsonPropertyName("@odata.context")]
    public string? Context { get; init; }

    [JsonPropertyName("value")]
    public IReadOnlyList<T> Value { get; init; } = [];

    /// <summary>Present when more rows remain. The gateway rewrites this to a gateway URL, so it can be requested as-is.</summary>
    [JsonPropertyName("@odata.nextLink")]
    public string? NextLink { get; init; }
}
