using System.Text.Json.Serialization;

namespace ARM.Integration.D365FSC.SampleClient.Models;

/// <summary>Error body emitted by the gateway itself, e.g. <c>{"error":"forbidden","correlationId":"9f2c..."}</c>.</summary>
public sealed record GatewayError
{
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }
}
