namespace ARM.Integration.D365FSC.SampleClient;

public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    /// <summary>Gateway root, e.g. <c>https://azf-d365fsc-gateway-wtc-nonprod.azurewebsites.net</c>.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>D365 environment slug issued to you, e.g. <c>DEV2</c>.</summary>
    public string Environment { get; set; } = string.Empty;

    /// <summary>Sent as the <c>X-Consumer-Id</c> header, e.g. <c>eng-apps</c>.</summary>
    public string ConsumerId { get; set; } = string.Empty;

    /// <summary>Sent as the <c>x-functions-key</c> header. Supply via the Gateway__FunctionKey environment variable.</summary>
    public string FunctionKey { get; set; } = string.Empty;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl))
            throw new InvalidOperationException("Gateway:BaseUrl is not configured.");
        if (string.IsNullOrWhiteSpace(Environment))
            throw new InvalidOperationException("Gateway:Environment is not configured.");
        if (string.IsNullOrWhiteSpace(ConsumerId))
            throw new InvalidOperationException("Gateway:ConsumerId is not configured.");
        if (string.IsNullOrWhiteSpace(FunctionKey) || FunctionKey.StartsWith('<'))
            throw new InvalidOperationException(
                "Gateway:FunctionKey is not configured. Set the Gateway__FunctionKey environment variable.");
    }
}
