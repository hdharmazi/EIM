using System.Text.RegularExpressions;

namespace ARM.Integration.D365FSC.Gateway.Routing;

public enum GatewayResourceType
{
    Data,
    Action,
    Service
}

/// <summary>
/// A request target that has been reduced to something authorizable.
/// <paramref name="Name"/> is what is matched against <c>GATEWAYPERMISSION.RESOURCE</c>:
/// <c>CustomersV3</c> for data, <c>CustomersV3/Confirm</c> for a bound action, and
/// <c>Group/Service/Operation</c> for a custom service.
/// </summary>
public sealed record GatewayResource(
    GatewayResourceType Type,
    string Name,
    string KeyPredicate = "",
    string? ActionNamespace = null);

public sealed record GatewayPathResult(GatewayResource? Resource, string? Failure)
{
    public bool IsSuccess => Resource is not null;

    public static GatewayPathResult Ok(GatewayResource resource) => new(resource, null);
    public static GatewayPathResult Fail(string reason) => new(null, reason);
}

/// <summary>
/// Reduces a request path to an authorizable resource, rejecting anything that does not match an
/// expected shape.
/// </summary>
/// <remarks>
/// This runs on the RAW, still-percent-encoded path. Deriving an authorization subject from a URL is
/// a well-known bypass surface — encoded separators, duplicated separators, traversal segments,
/// trailing dots — and every one of those tricks works by making the string the authorizer inspects
/// differ from the string the upstream server acts on. So the portion that gets authorized (the
/// entity or service name) is required to be a plain identifier with no encoding in it at all, and
/// anything unparseable is refused rather than salvaged.
/// </remarks>
public static class GatewayPathParser
{
    // D365 entity set and service names: an identifier. No dots, no encoding, no separators.
    private static readonly Regex Identifier = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    // Bound actions arrive fully qualified, e.g. Microsoft.Dynamics.DataEntities.Confirm.
    private static readonly Regex QualifiedAction = new(@"^[A-Za-z_][A-Za-z0-9_.]*$", RegexOptions.Compiled);

    private const int MaxPathLength = 1024;

    /// <summary>Parses the tail of a <c>/data/</c> route into a data or bound-action resource.</summary>
    public static GatewayPathResult ParseDataPath(string? rawTail)
    {
        if (string.IsNullOrWhiteSpace(rawTail)) return GatewayPathResult.Fail("empty path");
        if (rawTail.Length > MaxPathLength) return GatewayPathResult.Fail("path too long");

        var guard = RejectSuspiciousPath(rawTail);
        if (guard is not null) return GatewayPathResult.Fail(guard);

        var segments = rawTail.Split('/');
        if (segments.Length is 0 or > 2) return GatewayPathResult.Fail("unexpected segment count");
        if (segments.Any(string.IsNullOrEmpty)) return GatewayPathResult.Fail("empty segment");

        var (entity, predicate, entityFailure) = SplitEntityAndPredicate(segments[0]);
        if (entityFailure is not null) return GatewayPathResult.Fail(entityFailure);

        if (segments.Length == 1)
        {
            return GatewayPathResult.Ok(new GatewayResource(GatewayResourceType.Data, entity!, predicate));
        }

        var actionSegment = segments[1];
        if (!QualifiedAction.IsMatch(actionSegment)) return GatewayPathResult.Fail("malformed action segment");
        if (actionSegment.EndsWith('.') || actionSegment.Contains("..", StringComparison.Ordinal))
            return GatewayPathResult.Fail("malformed action segment");

        var lastDot = actionSegment.LastIndexOf('.');
        var actionName = lastDot >= 0 ? actionSegment[(lastDot + 1)..] : actionSegment;
        var actionNamespace = lastDot >= 0 ? actionSegment[..(lastDot + 1)] : null;

        if (!Identifier.IsMatch(actionName)) return GatewayPathResult.Fail("malformed action name");

        return GatewayPathResult.Ok(new GatewayResource(
            GatewayResourceType.Action, $"{entity}/{actionName}", predicate, actionNamespace));
    }

    /// <summary>Parses the three segments of a <c>/services/</c> route.</summary>
    public static GatewayPathResult ParseServicePath(string? group, string? service, string? operation)
    {
        foreach (var part in new[] { group, service, operation })
        {
            if (string.IsNullOrWhiteSpace(part)) return GatewayPathResult.Fail("empty service segment");
            if (!Identifier.IsMatch(part)) return GatewayPathResult.Fail("malformed service segment");
        }

        return GatewayPathResult.Ok(
            new GatewayResource(GatewayResourceType.Service, $"{group}/{service}/{operation}"));
    }

    private static string? RejectSuspiciousPath(string rawTail)
    {
        // An encoded separator is only ever an attempt to make this parser and D365 disagree about
        // where a segment ends.
        if (rawTail.Contains("%2f", StringComparison.OrdinalIgnoreCase)) return "encoded path separator";
        if (rawTail.Contains("%5c", StringComparison.OrdinalIgnoreCase)) return "encoded path separator";
        if (rawTail.Contains("%00", StringComparison.OrdinalIgnoreCase)) return "encoded null byte";
        if (rawTail.Contains('\\')) return "backslash separator";
        if (rawTail.Contains("//", StringComparison.Ordinal)) return "duplicated separator";
        if (rawTail.Any(char.IsControl)) return "control character";

        // Trimming a stray separator here would be normalisation, and normalising a path into
        // validity is how the authorized string stops matching the forwarded one.
        if (rawTail.StartsWith('/') || rawTail.EndsWith('/')) return "leading or trailing separator";

        var segments = rawTail.Split('/');
        if (segments.Any(s => s is "." or "..")) return "traversal segment";

        return null;
    }

    private static (string? Entity, string Predicate, string? Failure) SplitEntityAndPredicate(string segment)
    {
        var open = segment.IndexOf('(');

        if (open < 0)
        {
            if (segment.Contains(')')) return (null, "", "unbalanced key predicate");
            return Identifier.IsMatch(segment)
                ? (segment, "", null)
                : (null, "", segment.StartsWith('$') ? "system resource not permitted" : "malformed entity name");
        }

        var entity = segment[..open];
        var predicate = segment[open..];

        if (!Identifier.IsMatch(entity))
        {
            return (null, "", entity.StartsWith('$') ? "system resource not permitted" : "malformed entity name");
        }

        if (!predicate.EndsWith(')') || predicate.Count(c => c == '(') != predicate.Count(c => c == ')'))
        {
            return (null, "", "unbalanced key predicate");
        }

        return (entity, predicate, null);
    }
}
