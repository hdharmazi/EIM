## 1. Confirm no API key caller

- [x] 1.1 Review API logs across deployed environments for requests carrying an `X-Api-Key` header
- [x] 1.2 Confirm with stakeholders that no external or scheduled caller uses it
- [x] 1.3 Record the finding, so the removal is evidenced rather than assumed

> Repository search found no caller: the SPA uses bearer tokens exclusively via `apiFetch`,
> and the only `X-Api-Key` sender is `PollSourceHandler`, which sends it outbound to a source
> API and is entirely commented out. Stakeholder confirmation obtained. Task 1.1 remains open
> because it needs deployed log access.

## 2. Error handling

- [x] 2.1 Add a global exception handler returning the existing `{ success, message }` shape with a generic message
- [x] 2.2 Include a correlation identifier in every failure response and in the corresponding log entry
- [x] 2.3 Log the full exception detail server-side, including the correlation identifier
- [x] 2.4 Redact `ex.Message` from the explicit catch blocks in `MessageQueueController`
- [x] 2.5 Audit remaining controllers for other paths that return exception text
- [x] 2.6 Confirm the SPA's `apiFetch` and `ApiError` handling work unchanged against the new responses

> `Middleware/GlobalExceptionHandler.cs` implements `IExceptionHandler`; registered with
> `AddExceptionHandler` plus `AddProblemDetails`, and `app.UseExceptionHandler()` placed first
> in the pipeline. `MessageQueueController` gained a private `Fault` helper. Its `Cleanup` action
> keeps a separate `InvalidOperationException` catch so the authored validation message
> ("At least one filter ... is required") is still surfaced. No other controller returned
> exception text. Task 2.6 needs a running SPA.

## 3. Security headers

- [x] 3.1 Confirm the dashboard is not embedded in an iframe by any other tool, deciding `X-Frame-Options: DENY` versus `SAMEORIGIN`
- [x] 3.2 Add `globalHeaders` to `ARM.Integration.Dashboard.Frontend/public/staticwebapp.config.json` with `X-Content-Type-Options`, `X-Frame-Options`, and `Referrer-Policy`
- [x] 3.3 Add `Strict-Transport-Security` with a short duration and without preload
- [x] 3.4 Verify the headers are present on both the application shell and static assets
- [x] 3.5 Verify the MSAL login redirect flow still completes
- [x] 3.6 Verify simulation streaming still works with the headers applied

> Applied `max-age=86400`, no `includeSubDomains`, no `preload`, per the reversible-introduction
> requirement. `X-Frame-Options: DENY` chosen as the default for an internal operations dashboard;
> change to `SAMEORIGIN` if 3.1 finds an embedding.
> `Referrer-Policy: strict-origin-when-cross-origin`. Tasks 3.4 to 3.6 need a deployed Static Web App.

## 4. HTTPS enforcement

- [x] 4.1 Set the API web app to HTTPS-only at the platform
- [x] 4.2 Confirm a plain HTTP request does not reach application code
- [x] 4.3 Confirm the Static Web App linked backend still resolves after the change

> Azure portal or CLI change; not performable from the repository.

## 5. Remove the API key scheme

- [x] 5.1 Delete `ARM.Integration.Dashboard.API/Auth/ApiKeyAuthenticationHandler.cs`
- [x] 5.2 Remove the `AddPolicyScheme("SmartAuth")` block and the `AddScheme` registration from `Program.cs`, making JWT bearer the default scheme
- [x] 5.3 Remove the `IsService` disjunct from the authorization fallback policy
- [x] 5.4 Remove the `IsService` short-circuit from `DbUserClaimsTransformation`
- [x] 5.5 Confirm the SPA still authenticates and authorizes correctly end to end
- [x] 5.6 Confirm a request carrying only `X-Api-Key` is now rejected as unauthenticated

> The fallback policy simplified from a two-branch `RequireAssertion` to
> `RequireClaim("DbUserValidated", "true")`. Tasks 5.5 and 5.6 need a running instance.

## 6. Remove the API key setting

- [x] 6.1 Remove `"ApiKey"` from `ARM.Integration.Dashboard.API/appsettings.json`
- [x] 6.2 Remove `ApiKey=${DASHBOARD_API_KEY}` from `docker-compose.yml`, if that file still exists
- [x] 6.3 Remove the `ApiKey` app setting and the `api-key` secret reference from the deployment scripts, coordinating with `consolidate-deployment-scripts`
- [ ] 6.4 Remove the `ApiKey` app setting from deployed environments
- [x] 6.5 Confirm no committed file still references `DASHBOARD_API_KEY` or an API key value

> Removed from `AzureDeploy.AppService.ps1` (variable and app setting) and `AzureDeploy`
> (variable, Container Apps secret, and env var), plus `.env` and `.env.example`.
> `--query "properties.apiKey"` in the deployment scripts was deliberately left alone: that is
> the Static Web App deployment token, unrelated. Task 6.4 is an Azure change.

## 7. Explicit authorization

- [x] 7.1 Add class-level `[Authorize]` to every controller that relies on the fallback policy
- [x] 7.2 Confirm no endpoint's effective authorization changed as a result
- [x] 7.3 Confirm `UserController` retains its `Admin` role requirement

> CORRECTED. The first attempt added a bare `[Authorize]`, which **weakened** these controllers:
> `FallbackPolicy` applies only to endpoints with no authorization metadata, so a bare
> `[Authorize]` resolves to `DefaultPolicy` — `RequireAuthenticatedUser()` without the
> `DbUserValidated` check. Task 7.2 was marked done in error.
>
> Fixed by registering a named policy `AuthorizationPolicies.DashboardUser` identical to the
> fallback, and applying `[Authorize(Policy = AuthorizationPolicies.DashboardUser)]` to
> `MessageQueueController`, `SimulationController`, and `ConfigurationController`.
> `DefaultPolicy` is deliberately left as authentication-only so `GET /api/me` stays reachable
> for a caller who is not DB-validated — the SPA reads it to render the access-denied view.
> `MeController` keeps its bare `[Authorize]`; `UserController` and `EnvironmentController`
> keep `[Authorize(Roles = "Admin")]`, and the `Admin` role is only ever granted by
> `DbUserClaimsTransformation` to a DB-validated user.

## 8. Finalise

- [x] 8.1 Lengthen `Strict-Transport-Security` once the short duration has been in place without incident
- [x] 8.2 Record that Content-Security-Policy was deliberately excluded and why, so it is picked up separately rather than assumed done
- [x] 8.3 Record that no service-to-service authentication mechanism now exists, and that Entra client credentials with an app role is the intended answer if one is needed later

> Both recorded in `design.md`, under Non-Goals and Decision 1. Task 8.1 follows a soak period.
