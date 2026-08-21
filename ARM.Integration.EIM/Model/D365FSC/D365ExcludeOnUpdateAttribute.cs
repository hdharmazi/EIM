namespace ARM.Integration.EIM;

/// <summary>
/// Marks a D365 FSC entity property as never belonging in the body of an Update (PATCH) request.
/// Properties carrying this attribute are removed from the serialized payload before it is sent;
/// Create (POST) and Delete payloads are unaffected.
/// <para>
/// This attribute is load-bearing for correctness, not documentation. Removing a usage changes the
/// wire payload sent to D365 and can reintroduce a rejected update or an unintended overwrite.
/// </para>
/// <para>
/// Three categories of field are excluded, distinguished only by <see cref="Reason"/>:
/// OData key fields (already supplied in the request URL), fields D365 refuses to modify, and
/// fields excluded by integration policy such as create-only defaults.
/// </para>
/// </summary>
/// <example>
/// <code>
/// [JsonPropertyName("PartyNumber")]
/// [D365ExcludeOnUpdate("D365 rejects PATCH: update not allowed for field 'PartyNumber'.")]
/// public string PartyNumber { get; set; }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class D365ExcludeOnUpdateAttribute : Attribute
{
    /// <summary>Why this field must not appear in an Update payload.</summary>
    public string Reason { get; }

    public D365ExcludeOnUpdateAttribute(string reason) => Reason = reason;
}
