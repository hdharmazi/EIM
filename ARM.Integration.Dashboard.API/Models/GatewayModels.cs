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

    /// <summary>
    /// The gateway base address this consumer calls, and the one its key is verified against. Null
    /// means the dashboard's own configured gateway is used. Not a secret: it is the address the
    /// consumer is given, and it grants nothing on its own.
    /// </summary>
    public string? GatewayUrl { get; init; }

    /// <summary>
    /// The gateway application that accepted this key, or null when that is not known — either the
    /// key predates verification, or it was stored without being checked. Null means "unknown",
    /// never "no application", and the two must not be presented alike.
    /// </summary>
    public string? VerifiedApp { get; init; }

    /// <summary>
    /// When the key was verified. A point in time, not a continuing guarantee: the key may have been
    /// deleted in Azure since.
    /// </summary>
    public DateTime? VerifiedOn { get; init; }

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

    /// <summary>
    /// The gateway base address for this consumer. Optional; blank falls back to the dashboard's
    /// configured gateway. Validated against the allowed host suffixes before it is stored, so it
    /// cannot aim the server's verification request at an arbitrary host.
    /// </summary>
    public string? GatewayUrl { get; init; }
}

/// <summary>
/// A function key submitted for registration. Hashed on arrival; the value itself is never stored,
/// logged or returned.
/// </summary>
public sealed record GatewayConsumerKeyInput
{
    public required string Key { get; init; }

    /// <summary>
    /// Which configured gateway to verify against, used only when the consumer has no address of its
    /// own. A name, not an address: the caller chooses from the server's list rather than supplying
    /// a URL, so verification cannot be pointed at a host that would accept anything.
    /// </summary>
    public string? GatewayApp { get; init; }
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
