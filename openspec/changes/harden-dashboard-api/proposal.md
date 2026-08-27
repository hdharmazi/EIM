## Why

Three security weaknesses in `ARM.Integration.Dashboard.API`, all cheap to fix and none dependent on other work.

**The API key is an unused administrative bypass.** `ApiKeyAuthenticationHandler` grants a caller presenting a matching `X-Api-Key` the claims `IsService`, `DbUserValidated`, and the `Admin` role — full access to every endpoint including `UserController`, without any Entra involvement. The expected value is supplied by `$DASHBOARD_API_KEY` in the deployment scripts, where it is a hand-typed placeholder, so the live value exists only in whoever last ran a deployment. It is not held in a secret store and cannot be rotated systematically.

Nothing calls it. A search of the repository finds no caller: the SPA uses bearer tokens exclusively via `apiFetch`, and the only `X-Api-Key` sender is `PollSourceHandler`, which sends it *outbound* to a source API and is entirely commented out. Confirmed with stakeholders that no external caller uses it either.

**Exception details reach the browser.** `MessageQueueController` returns `$"ERROR: {ex.Message}"` to the client on failure, and there is no global exception handler, so unhandled exceptions elsewhere are surfaced by the framework's defaults.

**No security response headers, and no enforced HTTPS.** The frontend sends no `Strict-Transport-Security`, `X-Content-Type-Options`, `X-Frame-Options`, or `Referrer-Policy`. `public/staticwebapp.config.json` currently defines only `navigationFallback`. The API has no `UseHttpsRedirection` and no verified platform-level HTTPS enforcement.

## What Changes

- **The `X-Api-Key` authentication scheme is removed entirely.** **BREAKING** — any caller relying on it loses access. `ApiKeyAuthenticationHandler` is deleted, the policy scheme that routes between it and JWT bearer is removed, and JWT bearer becomes the default scheme.
- **Authorization tightens as a result.** The fallback policy currently accepts `IsService` **or** `DbUserValidated`; removing the API key removes the `IsService` path, so every caller must resolve to an active row in `USERTABLE`. The corresponding short-circuit in `DbUserClaimsTransformation` is removed.
- **The `ApiKey` setting is removed** from `appsettings.json`, `docker-compose.yml`, and the deployment scripts, eliminating a secret from every deployment.
- **A global exception handler is added**, returning a generic message with a correlation identifier while logging the detail server-side. The existing `{ success, message }` response shape is preserved so the SPA's `apiFetch` needs no change, and the explicit handlers that currently return `ex.Message` are changed to redact it.
- **Security headers are added via `staticwebapp.config.json` `globalHeaders`**: `Strict-Transport-Security`, `X-Content-Type-Options`, `X-Frame-Options`, and `Referrer-Policy`. These belong in the Static Web App configuration, not in `nginx.conf.template`, which `consolidate-deployment-scripts` deletes.
- **Content-Security-Policy is deliberately excluded.** A strict policy interacts with MSAL redirects and with the inline styles emitted by the component library, so it needs its own iteration rather than being added untested alongside four safe headers.
- **HTTPS is enforced at the platform**, by setting the web app to HTTPS-only rather than adding redirection middleware behind a TLS-terminating proxy.
- **Explicit `[Authorize]` is added at controller level** as defence in depth. The fallback policy already covers every endpoint, but it is invisible at the call site; an explicit attribute means a future `[AllowAnonymous]` added for local testing is a visible change rather than a silent hole.

## Capabilities

### New Capabilities

- `api-error-handling`: Unhandled and handled failures return a safe response carrying a correlation identifier, with diagnostic detail recorded server-side rather than sent to the caller.
- `web-security-headers`: The browser-facing application sends security response headers on every response.

### Modified Capabilities

- `dashboard-spa-shell`: The API no longer retains `X-Api-Key` authentication for non-interactive service callers. Bearer token validation and user resolution are unchanged.

## Impact

**Removed**
- `ARM.Integration.Dashboard.API/Auth/ApiKeyAuthenticationHandler.cs`.
- The `AddPolicyScheme("SmartAuth")` forwarding block and the `AddScheme` registration in `Program.cs`.
- The `IsService` disjunct in the authorization fallback policy, and the `IsService` short-circuit in `DbUserClaimsTransformation`.
- `"ApiKey"` from `appsettings.json`; `ApiKey=${DASHBOARD_API_KEY}` from `docker-compose.yml`; the `ApiKey` app setting and `api-key` secret from the deployment scripts.

**Changed**
- `Program.cs`: JWT bearer becomes the default scheme; a global exception handler is registered.
- `MessageQueueController`: exception messages redacted from responses.
- `public/staticwebapp.config.json`: `globalHeaders` added.
- Controllers gain explicit `[Authorize]`.

**Coordination**
- `consolidate-deployment-scripts` removes the `ApiKey` setting from the scripts it consolidates, and deletes `nginx.conf.template`. Whichever change lands second reconciles the overlap; neither depends on the other.

**Not included**
- Content-Security-Policy.
- Managed Identity for Service Bus and SQL, deferred separately.
- Rotation of the committed Service Bus key, deferred separately.
