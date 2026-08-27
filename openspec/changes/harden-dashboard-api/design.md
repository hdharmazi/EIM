## Context

`Program.cs` composes two authentication schemes behind a policy scheme that selects between them per request:

```csharp
.AddPolicyScheme("SmartAuth", "ApiKey or Bearer", options =>
{
    options.ForwardDefaultSelector = context =>
        context.Request.Headers.ContainsKey("X-Api-Key")
            ? ApiKeyAuthenticationHandler.SchemeName
            : JwtBearerDefaults.AuthenticationScheme;
})
```

and authorizes with a fallback policy accepting either identity:

```csharp
options.FallbackPolicy = new AuthorizationPolicyBuilder()
    .RequireAuthenticatedUser()
    .RequireAssertion(ctx =>
        ctx.User.HasClaim("IsService", "true") ||
        ctx.User.HasClaim("DbUserValidated", "true"))
    .Build();
```

A matching `X-Api-Key` yields `IsService`, `DbUserValidated`, and `Admin`:

```csharp
new Claim("IsService", "true"),
new Claim("DbUserValidated", "true"),
new Claim(ClaimTypes.Role, "Admin"),
```

So one static string grants administrative access to every endpoint, including those gated on the `Admin` role, without any Entra involvement. The expected value comes from `$DASHBOARD_API_KEY` in the deployment scripts, which is the literal placeholder `<YOUR_API_KEY-FUTUREPLACEHOLDER>`; the real value is entered by hand at deployment time and is not held anywhere it can be audited or rotated.

The scheme has no callers. The SPA routes every request through `apiFetch`, which attaches a bearer token. The only `X-Api-Key` occurrence outside the handler and its configuration is in `PollSourceHandler`, sending it outbound to a source API, in code that is entirely commented out. No external caller uses it.

Error handling is inconsistent and leaky. `MessageQueueController` returns `$"ERROR: {ex.Message}"`, and no global handler exists, so unhandled exceptions fall through to framework defaults.

The frontend sends no security headers. `public/staticwebapp.config.json` defines only `navigationFallback`. Notably, `nginx.conf.template` — which would have been the natural place for headers under the previous architecture — is deleted by `consolidate-deployment-scripts`, since the frontend is a Static Web App and does not use nginx.

## Goals / Non-Goals

**Goals:**

- No authentication path that bypasses Entra.
- No secret required by the API that exists only in someone's local edit of a deployment script.
- Failures return enough for a caller to act and enough for an operator to diagnose, without returning internals to the browser.
- A correlation identifier connects a client-visible failure to its server-side log entry.
- Security headers on every browser-facing response.
- The authorization posture is visible at each controller rather than implied by global configuration.

**Non-Goals:**

- Content-Security-Policy. It interacts with MSAL redirect flows and with inline styles from the component library, so it warrants its own iteration with testing rather than being bundled with four headers that are safe by inspection.
- Replacing the removed API key with another service-to-service mechanism. Nothing needs one today, and if something does later, the answer is Entra client credentials with an app role, not a shared static key.
- Managed Identity for Service Bus and SQL. Deferred separately.
- Rotating the committed Service Bus key. Deferred separately.
- Changing the SPA's error handling. The response shape is preserved specifically so the frontend needs no change.

## Decisions

### 1. The API key scheme is removed, not secured

The alternative is to keep it and fix its weaknesses: move the value into Key Vault, compare it in constant time, and narrow its claims so it is not automatically `Admin`.

Rejected, because that is real work to preserve a capability nothing uses, and it leaves a second authentication path that must be reasoned about in every future authorization change. A deleted bypass beats a well-managed one.

Consequence: the constant-time comparison item raised during review becomes moot, as does moving the key into a secret store.

### 2. Removal tightens authorization rather than merely simplifying it

```
  BEFORE                              AFTER
  authenticated                       authenticated
  AND ( IsService                     AND DbUserValidated
      | DbUserValidated )
        |
        +-- one static string grants
            Admin + DbUserValidated
```

Every caller must now resolve to an active row in `USERTABLE` through `DbUserClaimsTransformation`. There is no path to `Admin` that does not begin with a valid Entra token for a known, active user.

The `IsService` short-circuit in `DbUserClaimsTransformation` is removed at the same time; leaving it would be dead code that reads as though a service identity still exists.

### 3. The error response shape is preserved

The SPA's `apiFetch` and its `ApiError` type read `{ success, message }`. Switching to RFC 7807 `ProblemDetails` would be the better end state but changes the contract the frontend depends on, turning a self-contained API change into a coordinated one.

The handler therefore returns the existing shape with a safe message and a correlation identifier, and the detail is logged. `ProblemDetails` remains available later as a deliberate, separately scoped change.

### 4. Failures carry a correlation identifier

A generic message alone makes support harder than the status quo: today an operator at least sees the exception text. Returning an identifier that appears in the server log preserves diagnosability without disclosure, and it is the first piece of the end-to-end correlation that this system otherwise lacks.

### 5. Security headers belong in the Static Web App configuration

The frontend is served by a Static Web App, which terminates TLS itself. `globalHeaders` in `staticwebapp.config.json` applies to every response.

Two consequences of arriving here rather than at nginx:

- **`Strict-Transport-Security` is correct here.** The nginx container never terminated TLS — it listened on plain `8080` behind an Azure ingress — so it was the wrong layer.
- **The `add_header` inheritance hazard disappears.** In nginx, an `add_header` inside a `location` block discards headers inherited from the server block, so the `/assets/` location would have silently dropped them. `globalHeaders` has no such shadowing.

### 6. HTTPS is enforced at the platform, not by middleware

`app.UseHttpsRedirection()` behind a TLS-terminating proxy relies on forwarded headers being present and correct to know the original scheme, and redirects on a connection that is already plain HTTP internally. Setting the web app to HTTPS-only enforces it before a request reaches the application.

### 7. Explicit `[Authorize]` is added despite the fallback policy

The fallback policy already protects every endpoint, and controllers without authorization metadata are covered by it. This was verified during review, and an earlier report claiming those controllers were publicly reachable was wrong.

It is still added, because the protection is invisible at the call site. A future `[AllowAnonymous]` added for a quick test, or a change from `FallbackPolicy` to `DefaultPolicy`, would open the surface with nothing at the controller to contradict it. The attribute costs a line and makes the intent local.

## Risks / Trade-offs

- **An undiscovered caller depends on the API key** -> The repository has no caller and stakeholders confirm none externally. Detection is immediate and unambiguous: requests begin returning 401, and the calling identity is absent from the logs rather than failing subtly.
- **Generic error messages make support harder** -> Mitigated by the correlation identifier (Decision 4), which is strictly more useful than an exception string with no way to locate the corresponding log entry.
- **`Strict-Transport-Security` is difficult to reverse** -> Browsers honour it for its `max-age`. Introduce it with a short duration, confirm no HTTP-only dependency exists, then lengthen. Do not enable `preload` in this change.
- **`X-Frame-Options: DENY` breaks any embedding of the dashboard** -> Confirm the dashboard is not embedded in an iframe by any other tool before applying.
- **Overlap with `consolidate-deployment-scripts`** -> Both touch the `ApiKey` setting in the deployment scripts, and that change deletes `nginx.conf.template`. Neither depends on the other; whichever lands second reconciles the overlap.

## Migration Plan

1. **Confirm no caller.** Review API logs for requests carrying `X-Api-Key` before removing anything.
2. **Add the global exception handler and redact existing messages.** Independently valuable and independently deployable.
3. **Add security headers** with a short `Strict-Transport-Security` duration, and verify the SPA, MSAL redirect flow, and simulation streaming all still work.
4. **Enable HTTPS-only** on the web app.
5. **Remove the API key scheme**, simplify the fallback policy, and remove the claims transformation short-circuit.
6. **Remove the `ApiKey` setting** from configuration, compose, and the deployment scripts.
7. **Add explicit `[Authorize]`** to controllers and confirm behaviour is unchanged.
8. **Lengthen `Strict-Transport-Security`** once the shorter duration has been in place without incident.

**Rollback.** Steps 2 through 4 and 7 are individually revertible. Step 5 is revertible from version control, but the API key value would need to be re-supplied, so it is performed only after step 1 confirms no caller. `Strict-Transport-Security` is the one change browsers cache, which is why step 3 uses a short duration first.

## Open Questions

- **Is the dashboard embedded anywhere?** Determines whether `X-Frame-Options: DENY` or `SAMEORIGIN` is correct.
- **What `Strict-Transport-Security` duration is the eventual target, and is `preload` ever wanted?** Preload is effectively irreversible and should be a deliberate decision rather than a default.
- **Should `ProblemDetails` be adopted later?** It is the better contract, and adopting it means a coordinated API and frontend change. Worth doing before a second client consumes the API, not after.
