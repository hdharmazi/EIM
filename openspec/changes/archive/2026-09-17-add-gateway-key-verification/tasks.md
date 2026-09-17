## 1. Schema

- [x] 1.1 Add `Script/SQL/Migrations/012_add_gateway_key_verification.sql` with two additive nullable columns on `GATEWAYCONSUMER`: the gateway application that accepted the key, and the UTC time it was verified
- [x] 1.2 Guard the migration so it is idempotent in the style of `011_add_gateway_tables.sql`
- [x] 1.3 Apply the migration to `eimdb_dev` through the migrator, not by hand, and confirm existing rows are untouched
- [x] 1.4 Apply the migration to `eimdb` and confirm `eng-apps` and `eng-apps-dev` keep their key hashes

## 2. Configuration

- [x] 2.1 Add the shared gateway's name and base URL to the Dashboard API configuration — one gateway, following `share-gateway-across-nonprod`
- [x] 2.2 Source the value from the same place `gateway.baseUrl` is set for deployment, so the two cannot drift
- [x] 2.3 SUPERSEDED: the deferral is no longer needed. `deploy.bat.json` gained a `gateway` block when the shared gateway landed, so `Gateway__BaseUrl` and `Gateway__AppName` are wired and verification works in BAT.
- [x] 2.4 Make the probe timeout configurable, defaulting generously enough for a Flex Consumption cold start
- [x] 2.5 Wire the settings through `Script/Deploy-Eim.ps1` for the `api` component
- [x] 2.6 Skip the probe when the configured gateway is a loopback address and record the key as unchecked — the local Functions host does not enforce keys (verified 2026-09-16: a nonsense key reached the worker instead of returning 401), so probing localhost would mark any string as verified

## 3. Verification service

- [x] 3.1 Add a service that probes the gateway with `GET {baseUrl}/api/v1/d365fsc/PROBE/data/probe`, sending `x-functions-key` and deliberately omitting `X-Consumer-Id`
- [x] 3.2 Map the response to accepted / rejected / unreachable — 401 is the only rejection, any other status is acceptance, and a timeout or transport failure is unreachable
- [x] 3.3 Record the gateway application name on acceptance, so the stored result says what was checked rather than merely that something was
- [x] 3.4 Skip the probe and return unchecked when the configured gateway is a loopback address
- [x] 3.5 Register the HTTP client with a pooled handler rather than constructing one per request
- [x] 3.6 Ensure no code path logs the key, including on failure, timeout or unhandled exception

> 3.5 also disables automatic redirects. A redirect would forward the `x-functions-key` header to
> whatever host the response pointed at, which is not a risk worth carrying for a probe that has no
> legitimate reason to follow one.

## 4. Dashboard API

- [x] 4.1 Call the verification service in `SetKey` before hashing, and refuse to write when the result is not an acceptance
- [x] 4.2 Return distinguishable responses for rejected and unable-to-check, naming the applications tried
- [x] 4.3 Leave any existing key hash untouched when verification does not succeed
- [x] 4.4 Persist the accepting application and verification timestamp alongside the hash in a single update
- [x] 4.5 Expose the application and verification timestamp on the consumer read model, and confirm neither the key nor its hash is ever returned
- [x] 4.6 Log the outcome and the accepting application, never the key

## 5. Frontend

- [x] 5.1 Show the verification date next to the verified state on the consumer list
- [x] 5.2 Show the gateway application, rendering a consumer with a hash but no verification result as unknown and visually distinct from verified
- [x] 5.3 Render an unchecked registration distinctly from a verified one, so a locally registered key is never shown as verified
- [x] 5.4 Report rejected and unable-to-check as different messages in the key dialog, keeping the dialog open so the value can be corrected
- [x] 5.5 Warn in the dialog, when a key is already registered, that registering a different key stops the current one being accepted
- [x] 5.6 Show a pending state during the probe, since a cold start can take seconds
- [x] 5.7 Run `npx tsc --noEmit` and `npm run build`

## 5a. Gateway selection

- [x] 5a.1 Hold the gateway list in server configuration, keyed by name, and expose it read-only
- [x] 5a.2 Accept a gateway name, never an address, on the key registration request
- [x] 5a.3 Preselect when one gateway is configured; require an explicit choice when several are
- [x] 5a.4 Refuse an unknown gateway name rather than falling back to a default
- [x] 5a.5 Show the chosen gateway and its base address in the key dialog
- [x] 5a.6 Route the list endpoint absolutely so it cannot shadow a consumer named "gateways"

> Added after the initial implementation. The address had been configuration only, which was right,
> but the dialog never said what a key was being checked against — so an administrator had no way to
> tell where their key was going. Selection from a server-held list gives per-consumer targets without
> letting the submitter choose the host that answers, which would make a green tick meaningless.

## 5b. Gateway address on the consumer

- [x] 5b.1 Add `Script/SQL/Migrations/013_add_gateway_consumer_address.sql` with a nullable address column on `GATEWAYCONSUMER`
- [x] 5b.2 Carry the address through the consumer record, input, repository projection and writes
- [x] 5b.3 Add a gateway address field to the consumer create and edit form, with the configured gateway shown as the placeholder
- [x] 5b.4 Validate and normalise the address on save, against a configured list of allowed host suffixes
- [x] 5b.5 Verify a key against the consumer's own address when it has one, falling back to the configured list when it does not
- [x] 5b.6 Show each consumer's gateway in the list, and the resolved target in the key dialog
- [x] 5b.7 Test the address policy and verification against an address outside the configured list
- [x] 5b.8 Accept only a response carrying the gateway's correlation header, not merely any non-401

> 5b.8 fixes a defect introduced by 5b.5. While the address came only from server configuration the
> target was always a real gateway, so "anything that is not 401" was a safe inference. Making the
> address editable broke that: every app under `azurewebsites.net` answers, and a sibling Functions
> app returns 404 for the probe path — observed against `azf-eim-customer-wtc-bat` on 2026-09-16.
> That would have recorded a key as Verified against an application that can never honour it, which
> is the exact false assurance this change exists to remove.

> The list-based selection alone still needed a redeploy to add a gateway, which put a routine
> onboarding step behind a deployment. The address now lives on the consumer, where an administrator
> can set it. The property that makes verification mean something is kept by policy rather than by a
> fixed list: the scheme must be https and the host must sit under an allowed suffix, so the
> submitter still cannot nominate a host of their choosing to answer the probe.


## 6. Tests

- [x] 6.1 Test the response mapping: 401 rejects, 400 accepts, 200 accepts, timeout is unreachable
- [x] 6.2 Test that an unreachable gateway yields unable-to-check, not rejected
- [x] 6.3 Test that the gateway application name is recorded on acceptance
- [x] 6.4 Test that a failed verification leaves an existing key hash unchanged
- [x] 6.5 Test that the hex-digest rejection still runs before any network call is made
- [x] 6.6 Add a gateway authorizer test asserting the decision is unchanged by verification metadata
- [x] 6.7 Test that a loopback gateway stores the key without probing and records it as unchecked, not verified

> 6.4 and 6.5 were blocked on the controller depending on the concrete repository, which needs a
> database. Resolved by extracting `IGatewayConsumerRepository` in the dashboard API — a small,
> contained change rather than a test-shaped workaround. The rules these cover are the reason the
> endpoint exists, so they should not have been the hardest part to reach.
>
> 6.5 asserts the probe handler was never invoked, not merely that the request was refused: the point
> is that a digest is caught before a key-shaped value is put on the wire.

## 7. Documentation

- [x] 7.1 Correct `Script/Docs/d365fsc-gateway.md` to say host keys, not function-level keys, and explain the difference where a reader will meet it
- [x] 7.2 Add the standing constraint that a host key reaches every HTTP function in the application, so no unauthorized HTTP function may be added to a gateway app
- [x] 7.3 Update onboarding to describe the probe, including what a rejection and an unable-to-check result mean
- [x] 7.4 Record that a consumer holds one key for one gateway application, and that this is why `eng-apps` and `eng-apps-dev` are separate rows

## 8. Verification against real environments

- [x] 8.1 Confirm a genuine key from one gateway is accepted and the correct application is recorded
- [x] 8.2 Confirm a random string is refused with a rejection message
- [x] 8.3 Confirm a valid key for a *different* function app is refused, proving the probe discriminates between applications rather than merely detecting well-formed keys (verified by hand 2026-09-16: a dev gateway key returns 401 from the test gateway)
- [x] 8.4 Stop or block a gateway and confirm the result is unable-to-check, not rejected
- [x] 8.5 Check App Insights to confirm probe traffic does not appear in the `Gateway denied` query used for alerting, and fix or document it if it does

> 8.1 confirmed in production: consumer `test-1` carries `VERIFIEDAPP = azf-d365fsc-gateway-wtc-nonprod`
> from a key registered through the dashboard, and a live request with that key returns 200.
>
> 8.4 confirmed two ways rather than by stopping a shared gateway: a hostname that does not resolve
> fails DNS and is reported unreachable, and an address that answers without the gateway's correlation
> header is likewise unreachable rather than rejected.
>
> 8.5 confirmed clean. Probes appear only as `requests` with `resultCode=400`; no trace matches
> `missing consumer`, `PROBE` or `malformed`, so the `Gateway denied` alert query is unaffected. The
> earlier note that this was blocked on permissions was wrong — the blocker was the tooling. Querying
> the managed workspace through ARM needs a role the operator lacks, but the `application-insights`
> CLI extension queries through the component, which the operator can already read. No access change
> was needed.
>
> The same query independently confirmed the live 403 diagnosis:
> `Gateway denied: consumer=test-1 ... reason=key does not match consumer`.
- [x] 8.6 DROPPED, then resolved by events: this was to leave the existing `eng-apps` and `eng-apps-dev` hashes alone, showing a recorded key against an unknown gateway application until someone re-registered them. Both consumers were deleted instead, so no row is left in that state. `test-1` is the only consumer and was registered through the verified path from the start.
