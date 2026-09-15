using Microsoft.Extensions.Configuration;

namespace ARM.Integration.D365FSC.Gateway.Configuration;

/// <summary>
/// Bootstrap configuration for the gateway. Everything here is needed before the database can be
/// read; policy — consumers, grants, connection profiles — lives in the database instead, so it can
/// be changed from the dashboard without a deployment.
/// </summary>
public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    /// <summary>Connection string for the EIM database. Supplied as the <c>EIMDB</c> setting.</summary>
    public required string EimDbConnectionString { get; init; }

    /// <summary>
    /// Public base URL of this gateway, used when rewriting D365 URLs in <c>@odata.nextLink</c> and
    /// the <c>Location</c> header. Read from configuration rather than request headers because
    /// <c>Host</c> and <c>X-Forwarded-Host</c> are caller-controlled.
    /// </summary>
    public required string BaseUrl { get; init; }

    public int MaxRequestBytes { get; init; } = 4 * 1024 * 1024;

    public TimeSpan IdempotencyRetention { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Base URL with any trailing slash removed, for concatenation.</summary>
    public string NormalisedBaseUrl => BaseUrl.TrimEnd('/');

    /// <summary>
    /// Reads and validates settings, failing at startup rather than on the first request. A missing
    /// setting names itself; a gateway that starts without a base URL would serve unfollowable
    /// paging links instead of failing.
    /// </summary>
    public static GatewayOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);

        var options = new GatewayOptions
        {
            EimDbConnectionString = Required(configuration["EIMDB"], "EIMDB"),
            BaseUrl = Required(section["BaseUrl"], $"{SectionName}:BaseUrl"),
            MaxRequestBytes = PositiveInt(section["MaxRequestBytes"], $"{SectionName}:MaxRequestBytes", 4 * 1024 * 1024),
            IdempotencyRetention = PositiveTimeSpan(section["IdempotencyRetention"], $"{SectionName}:IdempotencyRetention", TimeSpan.FromHours(24)),
        };

        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException($"{SectionName}:BaseUrl must be an absolute URL.");
        }

        return options;
    }

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Required setting '{name}' is not configured.")
            : value;

    private static int PositiveInt(string? value, string name, int fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;

        return int.TryParse(value, out var parsed) && parsed > 0
            ? parsed
            : throw new InvalidOperationException($"Setting '{name}' must be a positive whole number.");
    }

    private static TimeSpan PositiveTimeSpan(string? value, string name, TimeSpan fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;

        return TimeSpan.TryParse(value, out var parsed) && parsed > TimeSpan.Zero
            ? parsed
            : throw new InvalidOperationException($"Setting '{name}' must be a positive timespan.");
    }
}
