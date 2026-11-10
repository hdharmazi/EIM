namespace ARM.Integration.Core.Helpers;

/// <summary>
/// Thrown when a D365 custom service answers successfully at the HTTP level but reports that it
/// rejected the request (for example <c>Success = false</c> with a business validation error).
/// Retrying cannot help, so the failure is terminal.
/// </summary>
public sealed class D365ServiceRejectedException(string serviceMethod, string? businessKey, string? errorMessage)
    : InvalidOperationException(string.IsNullOrWhiteSpace(errorMessage) ? DefaultMessage : errorMessage)
{
    private const string DefaultMessage = "D365 rejected the request.";

    /// <summary>The D365 service operation that rejected the request.</summary>
    public string ServiceMethod { get; } = serviceMethod;

    /// <summary>The business key of the rejected request (e.g. the PBLId), when known.</summary>
    public string? BusinessKey { get; } = businessKey;
}
