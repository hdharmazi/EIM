## 1. Verification spikes

- [x] 1.1 Confirm `eimdb` database collation and `ENVIRONMENTTABLE` column-level collation are case-insensitive; record the result — VERIFIED: both `eimdb` and `eimdb_dev` are `SQL_Latin1_General_CP1_CI_AS`, no column-level override
- [x] 1.2 Confirm whether `x-functions-key` reaches the isolated worker on an HTTP-triggered function; record the result and whether `KEYHASH` verification can be enabled in this release — VERIFIED against the deployed dev1 app: the header DOES reach the worker. A request with a matching `KEYHASH` passed verification and reached connection resolution (404), while a mismatched consumer returned 403. `KEYHASH` verification is therefore enabled from the outset and the one-consumer limitation does not apply.
- [x] 1.3 Confirm `@odata.nextLink` is returned as an absolute URL by the target D365 environment; capture a sample response — VERIFIED locally against DEV: absolute, and rewritten to the gateway. Note this environment returns `$skip`/`$top` rather than `$skiptoken`.
- [x] 1.4 Confirm Flex Consumption availability in the target region; if unavailable, record the fallback hosting decision — VERIFIED: `centralus` is in `az functionapp list-flexconsumption-locations`, no fallback needed
- [x] 1.5 Ask the consumer whether a fixed egress IP or CIDR range is available; if so, raise a follow-up for access restrictions — **RISK ACCEPTED**. No IP allow-list will be applied, so the function key remains the only network-layer gate. Compensating controls: function-level keys only, header-only presentation, rotation runbook, and alerting on the authorization denial rate.

## 2. Database schema

- [x] 2.1 Write the migration widening `ENVIRONMENTTABLE.ENVIRONMENT` to `nvarchar(30)` (drop PK, alter, recreate PK)
- [x] 2.2 Write the migration widening `ENVIRONMENTTABLE.VALUE` to `nvarchar(4000)`
- [x] 2.3 Write the migration creating `GATEWAYCONSUMER` (`CONSUMERID`, `KEYHASH` nullable, `SYSTEMPROFILE`, `ENABLED`, `EXPIRESON`, `DESCRIPTION`, audit columns)
- [x] 2.4 Write the migration creating `GATEWAYPERMISSION` (`CONSUMERID`, `ENVIRONMENT`, `RESOURCETYPE`, `RESOURCE`, `VERB`, `CREATEDBY`, `CREATEDON`) with FK to `GATEWAYCONSUMER`
- [x] 2.5 Write the migration creating `GATEWAYIDEMPOTENCY` (`CONSUMERID`, `IDEMPOTENCYKEY`, `REQUESTHASH`, `STATUS`, `RESPONSESTATUS`, `RESPONSEBODY`, `CREATEDON`, `EXPIRESON`)
- [x] 2.6 Run all migrations with `--whatif` against `eimdb_dev`, then apply and verify existing readers still function — applied 2026-09-14; `eimdb_dev` first reverted to baseline widths to make the rehearsal faithful; 9/9 constraint behaviour checks pass

## 3. Gateway project skeleton

- [x] 3.1 Create `ARM.Integration.D365FSC.Gateway` (net10.0, isolated worker, Functions v4) and add it to the solution
- [x] 3.2 Add `Program.cs` with DI, configuration binding and Application Insights, following the `ARM.Integration.EIM.Customer` pattern
- [x] 3.3 Add `host.json` and `local.settings.json` with the gateway's settings (`EIMDB`, gateway base URL, payload size limit, idempotency retention)
- [x] 3.4 Reference `ARM.Integration.Core`; verify `D365AuthDelegatingHandler` is usable without modifying `D365FSCClient`
- [x] 3.5 Add `ARM.Integration.D365FSC.Gateway.Tests` and wire it into the solution

## 4. Configuration and client resolution

- [x] 4.1 Move `ENVIRONMENTTABLE` access into `ARM.Integration.Core.Helpers.EnvironmentStore` with async overloads, leaving `EIMUtilities.GetEnvironment` as a delegate so existing call sites are untouched
- [x] 4.2 Implement the gateway connection resolver over `EnvironmentStore.TryGetValueAsync`, mapping an absent record to 404
- [x] 4.3 Implement the client factory caching on `SHA256(configJson)`, holding an `HttpClient` over a long-lived `SocketsHttpHandler` (with `PooledConnectionLifetime`) plus a long-lived MSAL application — `IHttpClientFactory` named clients cannot be used because the handler chain is per-configuration and so cannot be registered at startup
- [x] 4.4 Add cache entry eviction on idle and bound the cache size
- [x] 4.5 Test: editing configuration produces a new client without restart or flush
- [x] 4.6 Test: repeated requests on one instance do not re-acquire an AAD token

## 5. Access control

- [x] 5.1 Implement consumer repository and lookup by `X-Consumer-Id`, case-insensitive
- [x] 5.2 Enforce `ENABLED` and `EXPIRESON`
- [x] 5.3 Implement `KEYHASH` verification when populated; record identity as unverified when null
- [x] 5.4 Reject requests presenting the key via the `code` query-string parameter
- [x] 5.5 Implement permission repository and exact-match lookup on the five-tuple, evaluated before credentials are resolved
- [x] 5.6 Implement strict path canonicalisation and resource extraction for data, action and service routes
- [x] 5.7 Reject `$batch` paths
- [x] 5.8 Reject `DELETE`
- [x] 5.9 Emit denial telemetry carrying the attempted five-tuple and a correlation identifier
- [x] 5.10 Test: hostile path inputs — encoded separators, duplicated separators, traversal segments, mixed case, trailing dots
- [x] 5.11 Test: read grant does not confer write or bound-action rights; grants do not leak across environments

## 6. Request forwarding

- [x] 6.1 Implement the data route `/api/v1/d365fsc/{environment}/data/{*path}` for GET, POST and PATCH
- [x] 6.2 Implement the services route `/api/v1/d365fsc/{environment}/services/{group}/{service}/{operation}` for POST
- [x] 6.3 Forward method, canonical resource path and the raw query string without re-serialisation
- [x] 6.4 Implement request and response header allow-lists; strip caller credentials before forwarding
- [x] 6.5 Enforce the request payload size limit with 413
- [x] 6.6 Return 405 for unsupported verbs and 404 for unrecognised routes
- [x] 6.7 Test: `$skiptoken` values containing percent-encoded quotes and commas survive the round trip unchanged
- [x] 6.8 Test: `cross-company` is not injected when the caller omits it

## 7. Response rewriting

- [x] 7.1 Implement ordinal substitution of `{d365Base}/data/` with `{gatewayBase}/api/v1/d365fsc/{ENV}/data/` guarded by a containment check
- [x] 7.2 Apply the same rewrite to the `Location` response header
- [x] 7.3 Read the gateway base URL from configuration only, never from request headers
- [x] 7.4 Test: a rewritten `@odata.nextLink` can be followed successfully and is re-authorized
- [x] 7.5 Test: a forged `X-Forwarded-Host` does not influence rewritten links

## 8. Idempotency

- [x] 8.1 Implement the idempotency store over `GATEWAYIDEMPOTENCY`, scoped by consumer
- [x] 8.2 Require `Idempotency-Key` on POST; return 400 when absent
- [x] 8.3 Compute the request fingerprint and return 409 on key reuse with a different fingerprint
- [x] 8.4 Insert `INPROGRESS` before forwarding so concurrent duplicates lose the primary key race and return 409
- [x] 8.5 Store the completed response and replay it for matching retries
- [x] 8.6 Honour but do not require `Idempotency-Key` on PATCH — originally shipped broken: the header was only read when the method was POST, so a PATCH carrying a key silently bypassed the store. Fixed and covered by `IdempotencyVerbPolicyTests`.
- [x] 8.7 Add the retention purge timer function following the existing sweep-handler pattern
- [x] 8.8 Test: a replayed POST does not reach D365 and yields exactly one record — verified against `eimdb_dev`: replay returns the stored 201 and body without a second insert
- [x] 8.9 Test: concurrent identical keys result in exactly one forward — verified against `eimdb_dev`: the second insert loses the primary key race and reports INPROGRESS

## 9. Dashboard API

- [x] 9.1 Add `GatewayConsumerRepository` and `GatewayConsumerController` with admin-only authorization
- [x] 9.2 Add `GatewayPermissionRepository` and `GatewayPermissionController` with admin-only authorization
- [x] 9.3 Populate `CREATEDBY`/`CREATEDON` from the authenticated dashboard identity
- [x] 9.4 Add the entity-catalogue endpoint sourcing the D365 OData service document per environment, with caching
- [x] 9.5 Add the service-operation verification endpoint
- [x] 9.6 Refuse enabling a second consumer while any enabled consumer has a null `KEYHASH`
- [x] 9.7 Raise a notification event on production permission grant creation and deletion
- [x] 9.8 Ensure no endpoint returns or stores a function key value
- [x] 9.9 Add `POST /api/gateway/consumers/{id}/key`, hashing the submitted key server-side and rejecting a value that is itself a 64-character hex digest

> 9.8 originally read "accepts, returns or stores". Refusing to *accept* a key turned out to be the
> wrong line to draw: it left the only way to make a consumer work being a hand-computed SHA-256 and
> a direct `UPDATE`, which contradicts the whole point of administering consumers from the dashboard.
> The property worth keeping is that the key is never *retained* — so the endpoint accepts it, hashes
> it, and drops it.

## 10. Dashboard frontend

- [x] 10.1 Add `features/gateway/GatewayConsumerListPage.tsx` with enabled state, expiry and verification indicator
- [x] 10.2 Add `features/gateway/GatewayConsumerFormPage.tsx` including guidance on how keys are issued in Azure and registered here
- [x] 10.3 Add the permission matrix with an environment selector and per-resource verb columns
- [x] 10.4 Render DELETE as a disabled column
- [x] 10.5 Add the entity picker with autocomplete and multi-select backed by the catalogue endpoint
- [x] 10.6 Add free-text entry with verification for services and bound actions
- [x] 10.7 Display grant provenance
- [x] 10.8 Add routing and admin-only navigation entries
- [x] 10.9 Run `npx tsc --noEmit` and `npm run build`
- [x] 10.10 Add a "Set key" action and dialog on the consumer list, with a masked input that is cleared on close

## 11. Deployment

- [x] 11.1 Add the `gateway` component to `Script/Deploy-Eim.ps1` using the Flex Consumption provisioning path
- [x] 11.2 Add always-ready and maximum instance count settings to the per-environment deployment configuration
- [x] 11.3 Apply gateway application settings and fail fast with a named setting when one is missing
- [x] 11.4 Include the gateway in the all-components path without altering existing components
- [x] 11.5 Register the gateway Entra application and assign a D365 security role scoped to the intended operations — **DEFERRED by decision**. All `D365FSC-GW` profiles currently share the integration app's client id, so D365 cannot distinguish gateway traffic from integration traffic. Acceptable while every grant is GET. **Hard rule: no write verb may be granted until a dedicated registration is in place** — attribution cannot be reconstructed after the records exist.
- [x] 11.6 Create the `ENVIRONMENTTABLE` row for `D365FSC-GW` in the target environment — DEV, DEV2 and TEST created in `eimdb`. NOTE: all three currently borrow the integration service principal; swap to the dedicated gateway app before granting any write verb.

## 12. Onboarding and documentation

- [x] 12.1 Create the consumer's function-level host key and record the rotation runbook — `eng-apps` key created on the test gateway; runbook in `Script/Docs/d365fsc-gateway.md`
- [x] 12.2 Insert the initial `GATEWAYCONSUMER` row and permission grants — `eng-apps` with `KEYHASH` populated, granted `CustomersV3` GET on DEV and TEST
- [x] 12.3 Write the consumer integration guide: routes, required headers, `cross-company`, paging, `Idempotency-Key` generation, error responses — `Script/Docs/d365fsc-gateway.md`
- [x] 12.4 Document the emergency revocation procedure (delete the function key) and the orderly one (disable the consumer)
- [x] 12.5 Add an alert rule on authorization denial rate — KQL documented; Application Insights is now provisioned and wired by the deploy script, so the rule can be created against `appins-d365fsc-gateway-wtc-<env>`

## 13. End-to-end verification

- [x] 13.1 Verify a granted GET returns data from the target environment — 200 from TEST via `azf-d365fsc-gateway-wtc-test`
- [x] 13.2 Verify paging across at least two pages using the rewritten `@odata.nextLink` — two pages, `nextLink` host is the gateway, distinct rows per page
- [x] 13.3 Verify an ungranted entity, verb and environment each return 403 — entity, environment, POST, DELETE and a spoofed consumer id all 403; no key 401; `$batch` 400
- [x] 13.4 Verify a granted POST creates exactly one record and that a retry with the same key creates none — verified live: POST stored a COMPLETED 201; same key with a different body returned 409; PATCH replay returned the stored 204 in 311 ms against 2095 ms for the forwarded call, with a single store record
- [x] 13.5 Verify a granted custom service operation succeeds and an ungranted one is refused — verified on TEST: an ungranted service path returned 403 at the gateway, while the granted `SysMessageServices/SysMessageService/SendMessage` reached D365's service deserializer (400 naming the missing `_companyId` parameter), proving the route, the allow-list match and the forwarded path are all correct
- [x] 13.6 Verify the capture pipeline is unaffected while the gateway is under load — **PARTIAL, by circumstance.** The capture pipeline was idle (0 `MESSAGEQUEUETABLE` rows in 24h, last activity 6 days prior), so degradation could not be observed directly. What was verified instead is the mechanism that would cause it: 400 requests at concurrency 10 then 30 (3× the instance cap) produced **400/400 HTTP 200 and zero D365 throttling**, with p95 falling from 2292 ms to 1257 ms as always-ready instances warmed. Capture app, gateway app and D365 TEST all healthy afterwards. **Re-run against a pipeline carrying real traffic before onboarding a production consumer** — that is the only way to observe the interaction this task is really about.
- [x] 13.7 Verify disabling the consumer and deleting the function key each stop traffic immediately — `ENABLED = 0` produced 403 on the next request with no restart; key deletion verified separately during the spike (401)
