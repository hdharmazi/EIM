namespace ARM.Integration.Dashboard.API.Models;

/// <summary>
/// A gateway consumer as returned by the API.
/// </summary>
/// <remarks>
/// There is deliberately no key field of any kind. The secret lives in the Azure Functions host key
/// store, and the database holds only its hash; exposing either through the dashboard would put an
/// internet-facing credential behind a screen whose whole purpose is convenience.
/// <see cref="IdentityVerified"/> reports whether a hash is recorded, without revealing it.
/// </remarks>
public sealed record GatewayConsumerRecord
{
    public required string ConsumerId { get; init; }
    public required string SystemProfile { get; init; }
    public required bool Enabled { get; init; }
    public DateTime? ExpiresOn { get; init; }
    public string? Description { get; init; }
    public required bool IdentityVerified { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime? CreatedOn { get; init; }
    public string? ModifiedBy { get; init; }
    public DateTime? ModifiedOn { get; init; }
}

/// <summary>Create/update payload. Accepts no key material, so none can be submitted.</summary>
public sealed record GatewayConsumerInput
{
    public required string ConsumerId { get; init; }
    public required string SystemProfile { get; init; }
    public bool Enabled { get; init; }
    public DateTime? ExpiresOn { get; init; }
    public string? Description { get; init; }
}

public sealed record GatewayPermissionRecord
{
    public required string ConsumerId { get; init; }
    public required string Environment { get; init; }
    public required string ResourceType { get; init; }
    public required string Resource { get; init; }
    public required string Verb { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime? CreatedOn { get; init; }
}

public sealed record GatewayPermissionInput
{
    public required string Environment { get; init; }
    public required string ResourceType { get; init; }
    public required string Resource { get; init; }
    public required string Verb { get; init; }
}
