using System.Net;

namespace ARM.Integration.Dashboard.API.Services;

public enum GatewayKeyVerdict
{
    /// <summary>The gateway accepted the key. It was issued by that application.</summary>
    Accepted,

    /// <summary>The gateway returned 401. Azure did not issue this key for that application.</summary>
    Rejected,

    /// <summary>The gateway could not be reached, so nothing was learned about the key.</summary>
    Unreachable,

    /// <summary>Deliberately not checked, because the configured gateway cannot enforce keys.</summary>
    Unchecked,
}

/// <param name="Verdict">What was learned.</param>
/// <param name="GatewayApp">The application that answered, when one did.</param>
/// <param name="Detail">Why, in terms an administrator can act on.</param>
public sealed record GatewayKeyVerification(GatewayKeyVerdict Verdict, string? GatewayApp, string Detail);

/// <summary>A gateway a consumer's key may be verified against.</summary>
public sealed record GatewayTarget(string Name, string BaseUrl);

/// <summary>The outcome of checking a submitted gateway address against policy.</summary>
/// <param name="Target">The gateway to probe, or null when the address was refused.</param>
/// <param name="Error">Why it was refused, in terms an administrator can correct.</param>
public sealed record GatewayAddressCheck(GatewayTarget? Target, string? Error);

public sealed class GatewayKeyVerifierOptions
{
    /// <summary>The gateway written by deployment. Always available, and the default selection.</summary>
    public string? BaseUrl { get; set; }
    public string? AppName { get; set; }

    /// <summary>
    /// Further gateways a consumer may be pointed at. Configured on the server, never supplied by the
    /// caller: verification only means something if the set of possible answerers is fixed by
    /// somebody other than the person submitting the key.
    /// </summary>
    public List<GatewayTarget> Applications { get; set; } = [];

    /// <summary>
    /// Generous by default: the gateway runs on Flex Consumption, and a scaled-to-zero app can take
    /// seconds to answer. A timeout here is reported as unreachable, never as a rejection.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Host suffixes an administrator may point a consumer at. The dashboard server makes the
    /// verification request itself, so an unrestricted address would turn this screen into a way to
    /// probe anything the server can reach, including addresses only reachable from inside Azure.
    /// An empty list disables the restriction.
    /// </summary>
    public List<string> AllowedHostSuffixes { get; set; } = ["azurewebsites.net"];

    /// <summary>Every configured gateway, the deployment one first, without duplicates.</summary>
    public IReadOnlyList<GatewayTarget> ResolveTargets()
    {
        var targets = new List<GatewayTarget>();

        if (!string.IsNullOrWhiteSpace(BaseUrl))
        {
            targets.Add(new(
                string.IsNullOrWhiteSpace(AppName) ? HostOf(BaseUrl) : AppName,
                BaseUrl));
        }

        foreach (var extra in Applications)
        {
            if (string.IsNullOrWhiteSpace(extra.BaseUrl)) continue;
            if (targets.Any(t => string.Equals(t.Name, extra.Name, StringComparison.OrdinalIgnoreCase))) continue;

            targets.Add(extra with
            {
                Name = string.IsNullOrWhiteSpace(extra.Name) ? HostOf(extra.BaseUrl) : extra.Name,
            });
        }

        return targets;
    }

    /// <summary>
    /// Decides whether an administrator-supplied gateway address may be used, and normalises it.
    /// </summary>
    public GatewayAddressCheck CheckAddress(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return new(null, "A gateway address is required.");
        }

        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return new(null, "The gateway address is not a valid URL. Include the scheme, for example https://my-gateway.azurewebsites.net.");
        }

        // http is tolerated only on loopback, where there is no certificate to present and nothing
        // on the wire to intercept. Anywhere else it would send the function key in clear.
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        {
            return new(null, "The gateway address must use https.");
        }

        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            return new(null, "The gateway address must be a plain base address, with no credentials, query string or fragment.");
        }

        if (!uri.IsLoopback
            && AllowedHostSuffixes.Count > 0
            && !AllowedHostSuffixes.Any(suffix => HostEndsWith(uri.Host, suffix)))
        {
            return new(null, $"The gateway address must be hosted under {string.Join(" or ", AllowedHostSuffixes)}.");
        }

        // Without a trailing slash, resolving the probe path relative to this address would discard
        // the last segment, so an address with a path would be probed at the wrong place.
        var normalized = uri.AbsoluteUri.EndsWith('/') ? uri.AbsoluteUri : uri.AbsoluteUri + "/";

        return new(new GatewayTarget(AppNameOf(uri.Host), normalized), null);
    }

    private static bool HostEndsWith(string host, string suffix)
    {
        suffix = suffix.TrimStart('.');
        return host.Equals(suffix, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith($".{suffix}", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The function app name, which is the first label of an azurewebsites.net host.</summary>
    private static string AppNameOf(string host)
    {
        var dot = host.IndexOf('.');
        return dot > 0 ? host[..dot] : host;
    }

    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
}

/// <summary>
/// Checks that a submitted function key was actually issued by the gateway, before its hash is stored.
/// </summary>
/// <remarks>
/// The probe requests a data path but deliberately omits <c>X-Consumer-Id</c>. The gateway rejects a
/// missing consumer id before it looks up the consumer, evaluates grants, or resolves the
/// environment's D365 credentials — so a probe reads no secret, calls no D365 tenant and writes no
/// idempotency record, and cannot be steered into doing so by choosing a different path.
/// </remarks>
public sealed class GatewayKeyVerifier(
    HttpClient http,
    GatewayKeyVerifierOptions options,
    ILogger<GatewayKeyVerifier>? logger = null)
{
    private const string ProbePath = "api/v1/d365fsc/PROBE/data/probe";

    /// <summary>
    /// Stamped by the gateway on every response its worker produces. Absent on the platform's own
    /// 401, and absent from anything that is not this gateway.
    /// </summary>
    private const string CorrelationHeader = "X-Correlation-Id";

    /// <summary>The gateways a key may be checked against, for the caller to choose from.</summary>
    public IReadOnlyList<GatewayTarget> Targets => options.ResolveTargets();

    /// <summary>Whether an administrator-supplied gateway address is usable, and why not if it is not.</summary>
    public GatewayAddressCheck CheckAddress(string? baseUrl) => options.CheckAddress(baseUrl);

    /// <summary>
    /// Checks a key against the address recorded on a consumer, rather than one of the gateways this
    /// dashboard was deployed with.
    /// </summary>
    /// <param name="baseUrl">
    /// The consumer's own gateway address. Run through <see cref="CheckAddress"/> first: the
    /// dashboard server issues this request, so an unchecked address would let this screen reach any
    /// host the server can.
    /// </param>
    public async Task<GatewayKeyVerification> VerifyAtAsync(
        string key, string baseUrl, CancellationToken cancellationToken = default)
    {
        var check = options.CheckAddress(baseUrl);

        return check.Target is null
            ? new(GatewayKeyVerdict.Unreachable, null, check.Error!)
            : await ProbeAsync(key, check.Target, cancellationToken);
    }

    /// <param name="key">The submitted function key. Never stored, logged or echoed.</param>
    /// <param name="gatewayApp">
    /// Which configured gateway to check against. Null selects the only one when a single gateway is
    /// configured, and is rejected when there is more than one — guessing on the caller's behalf
    /// would record a verification against an application they did not choose.
    /// </param>
    public async Task<GatewayKeyVerification> VerifyAsync(
        string key, string? gatewayApp = null, CancellationToken cancellationToken = default)
    {
        var targets = options.ResolveTargets();

        if (targets.Count == 0)
        {
            return new(GatewayKeyVerdict.Unreachable,
                null,
                "No gateway address is configured, so the key could not be checked.");
        }

        GatewayTarget target;
        if (string.IsNullOrWhiteSpace(gatewayApp))
        {
            if (targets.Count > 1)
            {
                return new(GatewayKeyVerdict.Unreachable,
                    null,
                    "More than one gateway is configured, so the one to verify against must be chosen.");
            }

            target = targets[0];
        }
        else
        {
            var match = targets.FirstOrDefault(
                t => string.Equals(t.Name, gatewayApp, StringComparison.OrdinalIgnoreCase));

            // Refused rather than falling back to a default: an unknown name means the caller meant
            // something the server does not know about, and silently checking a different gateway
            // would record a verification that misstates what was checked.
            if (match is null)
            {
                return new(GatewayKeyVerdict.Unreachable,
                    null,
                    $"'{gatewayApp}' is not a configured gateway, so the key could not be checked.");
            }

            target = match;
        }

        return await ProbeAsync(key, target, cancellationToken);
    }

    private async Task<GatewayKeyVerification> ProbeAsync(
        string key, GatewayTarget target, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(target.BaseUrl, UriKind.Absolute, out var baseUri))
        {
            return new(GatewayKeyVerdict.Unreachable,
                target.Name,
                $"The configured address for '{target.Name}' is not a valid URL.");
        }

        // A local Functions host does not enforce AuthorizationLevel.Function, so probing loopback
        // would accept any string and report it as verified - the exact defect this class exists to
        // prevent, reintroduced where it is least likely to be noticed.
        if (baseUri.IsLoopback)
        {
            return new(GatewayKeyVerdict.Unchecked,
                null,
                "The gateway is running locally, where keys are not enforced, so the key was stored without being verified.");
        }

        var app = target.Name;

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, ProbePath));
        request.Headers.TryAddWithoutValidation("x-functions-key", key);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Deliberately not logged with the exception's request details, which can carry headers.
            logger?.LogWarning("Gateway key verification could not reach {App}: {Reason}", app, ex.GetType().Name);

            return new(GatewayKeyVerdict.Unreachable,
                app,
                $"{app} could not be reached, so the key could not be checked. It was not stored.");
        }

        using (response)
        {
            // 401 is the platform refusing a key it did not issue. It is produced before the worker
            // runs, so it is the one status that means the key itself is wrong.
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new(GatewayKeyVerdict.Rejected,
                    app,
                    $"{app} rejected this key. Check it was copied whole, and that it belongs to that application.");
            }

            // Getting past the platform is necessary but not sufficient. Any other app under the same
            // domain answers too - a Functions app with no such route returns 404, which would
            // otherwise be read as acceptance and record a key as verified against something that is
            // not a gateway at all. Only this gateway stamps a correlation id.
            if (!response.Headers.Contains(CorrelationHeader))
            {
                return new(GatewayKeyVerdict.Unreachable,
                    app,
                    $"{app} answered, but not as a D365 gateway (HTTP {(int)response.StatusCode}). Check the gateway address is correct. The key was not stored.");
            }

            return new(GatewayKeyVerdict.Accepted, app, $"Verified against {app}.");
        }
    }
}
