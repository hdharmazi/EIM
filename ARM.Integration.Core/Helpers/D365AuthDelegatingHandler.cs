using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using System.Net.Http.Headers;

namespace ARM.Integration.Core.Helpers;

/// <summary>
/// <see cref="DelegatingHandler"/> that acquires and caches a Bearer token
/// via MSAL before each outgoing request. Tokens are cached by MSAL internally.
/// </summary>
public sealed class D365AuthDelegatingHandler : DelegatingHandler
{
    private readonly IConfidentialClientApplication _msalApp;
    private readonly string[] _scopes;

    public D365AuthDelegatingHandler(IOptions<D365FSCOptions> options)
    {
        var o = options.Value;
        string authority = o.IsOnPremise ? o.AuthURL : $"{o.AuthURL}/{o.Tenant}";
        string scope     = o.IsOnPremise ? o.URL     : $"{o.URL}/.default";

        _scopes  = [scope];
        _msalApp = ConfidentialClientApplicationBuilder
            .Create(o.ClientId)
            .WithAuthority(authority)
            .WithClientSecret(o.Secret)
            .Build();
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var authResult = await _msalApp
            .AcquireTokenForClient(_scopes)
            .ExecuteAsync(cancellationToken);

        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", authResult.AccessToken);

        return await base.SendAsync(request, cancellationToken);
    }
}
