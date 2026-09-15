## Context

`ARM.Integration.EIM.Customer` is an isolated-worker .NET 10 function app whose triggers are
exclusively Service Bus and Timer. There are no HTTP-triggered functions anywhere in the solution;
`ARM.Integration.Dashboard.API` is a separate ASP.NET Core app secured by Entra JWT for interactive
users, not machine callers.

Two pieces of the required behaviour already exist and are in production use:

```
EIMUtilities.GetEnvironment("D365FSC", env, eimdb)          -> ENVIRONMENTTABLE.VALUE (JSON)
D365FSCServiceCollectionExtensions.CreateStandaloneClient(json) -> ID365FSCClient
```

`D365ServiceActionOrchestration`, `BaseCrudOrchestration` and `BaseForwardOrchestration` all use
that pair. `D365FSCOptions` is explicitly documented as bindable from "environment JSON", so
`ENVIRONMENTTABLE` is already the intended home for per-environment D365 connection settings.

Constraints that shaped this design:

- **No APIM**, and none planned. Everything APIM would have provided must be free or hand-rolled.
- **No fixed egress IP** for the consumer, so Function App access restrictions are unavailable and
  the function key is the only network-layer gate.
- **One shared `eimdb`** serves dev, dev1, bat and test. Any schema migration hits all of them
  simultaneously.
- **Scope is a framework, not a single integration**: any verb, any entity, any custom service.
  Day-one usage is one consumer doing GET on one entity.
- Grants are administered by business/support users through the dashboard, without database access.

## Goals / Non-Goals

**Goals:**
- A synchronous HTTP front door to D365 F&SC, addressable per environment, usable by callers who
  cannot hold Entra credentials.
- Transparent enough that standard OData clients work unmodified, including paging.
- Authorization that is data-driven and administrable from the dashboard, with no deploy required to
  grant or revoke access.
- Extensible to new entities, verbs and services without code changes.
- Zero blast radius on the existing capture pipeline.

**Non-Goals:**
- Reimplementing the async orchestration pipeline. This path never touches `MESSAGEQUEUETABLE`,
  `ROUTETABLE` or any orchestration.
- Payload validation or transformation. The gateway does not interpret request or response bodies
  beyond URL rewriting.
- Rate limiting or quotas per consumer. Bounded only globally, by instance count.
- Secret management improvements. Credentials stay where they are today.

## Decisions

### Placement: a separate function app

A new `ARM.Integration.D365FSC.Gateway` project rather than new triggers in
`ARM.Integration.EIM.Customer`.

Different trigger model, different scaling profile, different hosting plan, and — decisively — a
different exposure class. The existing app is internal and reachable only via Service Bus; this one
is internet-facing. Co-locating them would mean a gateway traffic burst scales (and can destabilise)
the capture pipeline, and a gateway vulnerability would sit inside the same process as the
orchestrations. Shared code comes from `ARM.Integration.Core`.

### Forwarding: raw HTTP, not the typed client

The gateway reuses `D365AuthDelegatingHandler` over an `HttpClient`, but **not** `D365FSCClient`.

`D365FSCClient` is a typed client for callers that know their shape: its CRUD methods hardcode
`data/{entity}` and append a `cross-company` segment from configuration, while `CallServiceAsync`
takes a whole relative path. Mapping incoming requests onto those methods would require a verb
switch, would not cover bound actions cleanly, and would silently inject query parameters the caller
did not ask for.

Forwarding method, path, raw query string, body and an allow-listed header set gives one code path
for data entities, bound actions, custom services and anything D365 adds later.

*Alternative considered:* extend `ID365FSCClient` with a generic `SendAsync`. Rejected — it would
modify a class on the hot path of the live capture pipeline for the benefit of one new caller.

Consequence: `cross-company=true` is no longer injected automatically. Callers must send it
themselves or D365 scopes results to the service principal's default company. This is correct for a
transparent proxy but is a documented integration gotcha.

### Client lifetime: cache keyed on the configuration content hash

`CreateStandaloneClient` builds a fresh `HttpClient` **and** a fresh
`ConfidentialClientApplication` per call. Harmless for a Service Bus handler running at
`maxConcurrentCalls: 1`; unacceptable for an HTTP gateway, where it causes socket churn and a full
AAD token round-trip on every request because MSAL's cache is per-application-instance.

The gateway reads `ENVIRONMENTTABLE` on every request (an indexed two-column PK lookup on a tiny
table) and uses `SHA256(configJson)` as the key into a client cache:

```
json   = GetEnvironment(profile, env)
client = cache.GetOrAdd(SHA256(json), () => BuildClient(json))
```

Editing the row in the dashboard changes the hash, which produces a new client automatically and
lets the old entry age out. There is no invalidation logic, no TTL to tune, and no staleness window.

This matters because Flex Consumption scales out: any in-process cache with an explicit flush
endpoint would only flush the one instance that received the flush call. Content-hash keying is the
only approach that stays correct across instances without coordination.

### Environment lookup moves into Core, with the legacy path preserved exactly

`EIMUtilities.GetEnvironment` lives in `ARM.Integration.EIM` and is synchronous. The gateway can use
neither property: referencing that assembly would drag the orchestration stack into an
internet-facing app, and a blocking database call on a request thread starves the thread pool at the
concurrency an HTTP front end is expected to reach.

The lookup therefore moves to `ARM.Integration.Core.Helpers.EnvironmentStore`, which exposes the
existing synchronous method plus `GetValueAsync` and `TryGetValueAsync`. `EIMUtilities.GetEnvironment`
becomes a one-line delegate, so all six existing call sites are untouched.

The synchronous overload deliberately still goes through `SQLServerHelper`. That helper wraps every
exception in a plain `Exception`, so a SQL failure from this path has never surfaced as a
`SqlException`. `DefaultFailureClassifier` has no `SqlException` arm and flattens the whole inner
chain via `Unwrap`, so the wrapper is in fact classification-neutral — but relying on that analysis
would be an unnecessary risk when keeping the existing call intact costs nothing. Only the new async
overloads, which have no legacy callers, use SqlClient directly.

`TryGetValueAsync` returns null rather than throwing, so the gateway can map "not configured" to 404
without exception-based control flow on its hot path.

*Alternative considered:* leaving the method where it is and referencing `ARM.Integration.EIM` from
the gateway. Rejected on both dependency direction and the synchronous-call problem.

### Routing: mirror D365's own structure, versioned

```
/api/v1/d365fsc/{environment}/data/{*path}                     GET POST PATCH
/api/v1/d365fsc/{environment}/services/{group}/{svc}/{op}      POST
```

A single `/{environment}/{entity}` route becomes ambiguous once custom services exist, which live
under a different D365 root (`/api/services/`, not `/data/`). Mirroring D365's own segments removes
the ambiguity, makes the gateway URL guessable for anyone who knows D365, and reduces the
`@odata.nextLink` rewrite to a single prefix substitution.

The `v1` segment costs three characters and is retained because the route shape already changed once
during design (adding the `data/` segment when service support came into scope). Breaking changes to
route shape, error format or auth headers are otherwise a flag-day coordination across every
consumer.

### Paging: server-side `@odata.nextLink` rewrite

D365 returns `@odata.nextLink` as an absolute URL to the D365 host. Passed through untouched, the
caller receives a link it cannot follow — it has no AAD credential — so paging silently dead-ends on
page two. The `Location` header on a 201 has the identical problem.

Both are rewritten by substituting `{d365Base}/data/` with
`{gatewayBase}/api/v1/d365fsc/{ENV}/data/`.

*Alternatives considered:*
- *Pass through untouched and document that clients strip the prefix themselves.* Workable, but the
  gateway stops being a standards-compliant OData endpoint and every consumer must hand-roll paging
  instead of using their client library's built-in support.
- *Full JSON parse, mutate, re-serialise.* Roughly 200–500 ms and ~3× body memory for a large page.

The chosen ordinal substitution is a single linear scan — on the order of 1–3% of total request time
against a D365 round trip of 200–2000 ms. It also incidentally corrects `@odata.context`, which a
targeted JSON edit would not.

Trade-off: rewriting requires buffering the response rather than streaming it. Accepted for v1;
memory is bounded by instance size and instance count rather than by a query cap.

No `$top` cap is imposed. D365 paginates on its own, so a cap guards against something already
guarded, and `Prefer: odata.maxpagesize` is forwarded so clients control page size themselves.

### Authentication: Azure Functions host keys

Function-level host keys, presented in the `x-functions-key` header. Requests with an invalid key are
rejected by the Functions host and never reach application code, so there is no key comparison to
get wrong and no secret stored in the database.

*Alternatives considered:*
- *APIM subscription keys.* Would also supply quotas, rate limiting and IP filtering as
  configuration. Unavailable — no APIM in the tenant.
- *Custom `X-Api-Key` with a hashed key table.* More capable (expiry, per-key scopes) but requires
  hand-rolled hashing, fixed-time comparison and cache-versus-revocation-lag tuning. Notably, the
  Dashboard API's previous `X-Api-Key` scheme was removed as a hardening measure because presenting
  the key implicitly granted `Admin` — a precedent worth not repeating.

Required hygiene, given the key is the sole gate:
- Function-level keys only, never `_master` (which can call the admin API and mint further keys).
- Header only; `?code=` in the query string is rejected with 400, since query strings are recorded
  in App Insights, App Service logs and intermediate proxies.
- Rotation is additive: create `<consumer>-2`, migrate, delete `<consumer>-1`. No downtime, no
  deploy.
- Emergency revocation is deleting the key in Azure — host-enforced, immediate, and effective across
  all instances without touching the database.

Known limit: host keys carry no expiry and are managed only via portal or CLI. At roughly five
consumers this stops being sufficient and should be revisited.

### Consumer identity: a label now, verifiable later

The Functions host validates the key but does not tell application code which key matched. Whether
`x-functions-key` is forwarded to the isolated worker at all is unverified.

The design is deliberately independent of that answer. `X-Consumer-Id` is required from day one, so
the client contract is fixed, and `GATEWAYCONSUMER.KEYHASH` is **nullable**:

| `KEYHASH` | Behaviour |
|---|---|
| `NULL` | Identity taken from `X-Consumer-Id`, unverified, logged as such |
| populated | `SHA256(x-functions-key)` must match, else 403 |

With a single consumer, spoofing `X-Consumer-Id` resolves to the same consumer — there is nothing to
escalate to, so unverified identity is not a security boundary yet. That ceases to be true the
moment a second consumer exists, which is why it is captured as a spec-level constraint rather than
a backlog note.

If the spike shows the header reaches the worker, populating `KEYHASH` enables verification with no
client-side change.

### Connection resolution: the system profile is never caller-supplied

`SYSTEM` in `ENVIRONMENTTABLE` already functions as a profile axis (`CAPTURE`, `SOURCESYSTEM`,
`D365FSC`). The gateway adds `D365FSC-GW`, holding a distinct Entra app registration.

```
X-Consumer-Id -> GATEWAYCONSUMER.SYSTEMPROFILE -> ENVIRONMENTTABLE(SYSTEM, ENVIRONMENT) -> config
                        (server-side)                  (environment from the URL)
```

If `system` were a route segment, a caller could redirect the gateway at `SYSTEM = 'D365FSC'` — the
integration service principal, which holds far broader D365 permissions. That is privilege
escalation by URL editing. Resolving the profile from the consumer record closes it structurally
rather than by validation.

Choosing `D365FSC-GW` over a longer name is deliberate: `EnvironmentRepository` orders by
`SYSTEM, ENVIRONMENT`, so gateway and integration credentials for each environment appear adjacent
in the dashboard.

A separate app registration also gives D365-side attribution for free — records created through the
gateway carry a different `CreatedBy` service principal. Retrofitting that later is impossible.

*Alternative considered:* adding a third PK column to `ENVIRONMENTTABLE` for purpose. Rejected — it
breaks every existing reader for something the existing key already expresses.

### Authorization: verb-precise storage, entity-centric UI

```sql
GATEWAYPERMISSION (CONSUMERID, ENVIRONMENT, RESOURCETYPE, RESOURCE, VERB)
                   RESOURCETYPE ∈ { DATA, ACTION, SERVICE }
```

Granting at entity level rather than verb level would mean a consumer allowed to read `CustomersV3`
could also delete them — an invisible over-grant, because nobody granting "CustomersV3" is thinking
about DELETE.

The storage model and the administrative model are deliberately different. The dashboard presents
one row per entity with a verb matrix, so an admin thinks in entities while the system enforces in
capabilities. Actions and services get a single toggle, since both are always POST.

`ACTION` is separated from `DATA` because reading `SalesOrderHeadersV2` and confirming one are
different privileges that merely share a URL prefix.

No wildcards. Bulk granting is handled by multi-select in the entity picker, which writes explicit
rows — the same convenience without a rule that silently absorbs future entities.

`DELETE` is modelled but not grantable in v1.

### Two authorization bypasses that must be blocked explicitly

**`$batch`.** A single POST to `/data/$batch` carries arbitrary sub-requests in a multipart body
that the allow-list never inspects. One grant would silently confer everything. It is rejected
outright; supporting it would require parsing and individually authorizing each sub-request.

**Path parsing.** Deriving the authorized resource from a `{*path}` catch-all is a well-known bypass
surface — encoded slashes (`%2F`), doubled separators, trailing dots, case and unicode
normalisation. Paths are canonicalised first and parsed strictly, rejecting anything that does not
match an expected shape rather than best-effort extracting an entity name. Hostile-input tests are a
requirement, not a nicety.

`$expand` is permitted but noted: it reaches related entities that were never allow-listed. Revisit
if a consumer's grants and their `$expand` usage diverge meaningfully.

### Matching: case-insensitive in, canonical value out

Callers may send any casing for environment, entity and consumer. The database collation
(`SQL_Latin1_General_CP1_CI_AS`) already makes lookups case-insensitive and the primary key prevents
`DEV` and `dev` coexisting, so this works largely by default — but it must be verified, not assumed,
including column-level collation overrides.

The subtlety is that **D365 OData entity names are case-sensitive in the URL**. A case-insensitive
match that then forwards the caller's spelling would authorize correctly and then 404 at D365 — a
confusing failure that looks like a permissions problem. So the rule is: match case-insensitively,
then forward the spelling stored in `GATEWAYPERMISSION.RESOURCE`. The allow-list becomes the single
source of canonical spelling.

Normalising to upper case in code is for consistency of log and metric dimensions, not for the
database's benefit.

### Idempotency on POST

Every write in this design is a POST — entity creates, bound actions and custom services alike — and
none are naturally idempotent. A lost response (network, or the ~230 s platform limit on HTTP
responses) leaves the client unable to distinguish "never arrived" from "succeeded, answer lost". A
retry is the only sane client behaviour and is also what creates the duplicate ERP record.

`Idempotency-Key` is therefore **required on POST** (400 if absent). Requests are recorded in
`GATEWAYIDEMPOTENCY`, keyed by `(CONSUMERID, IDEMPOTENCYKEY)`, storing a request fingerprint and the
completed response for replay.

- **Same key, different body** → 409. Replaying the earlier response would silently return the wrong
  record.
- **Same key, concurrent** → the `INPROGRESS` row's PK insert loses the race and returns 409, rather
  than both instances forwarding.
- Retention ~24 h, purged by a timer function following the existing `RetrySweepHandler` pattern.

SQL-backed rather than in-memory, because Flex Consumption scales out and an in-process store would
let two instances each forward the same retry.

`PATCH` accepts a key but does not require one: OData PATCH to a specific key converges. The
exception — D365 entities whose X++ `update()` logic has side effects — is why the key is honoured
when sent.

A per-consumer exemption flag is deliberately not built. It can be added if someone demonstrates a
need.

### Hosting: Flex Consumption

Linux-only (as required), scales to zero, and — the actual reason — provides `alwaysReady`
instances, per-instance concurrency control and `maximumInstanceCount`.

Cold start matters more here than for the existing app: a synchronous caller notices it, and on a
cold instance it stacks with the first AAD token acquisition. One always-ready instance removes
both.

`maximumInstanceCount` matters more still. Without APIM it is the only rate limiter, and the failure
mode it guards against is specific:

```
consumer burst -> gateway scales out -> D365 throttles (429)
                                     -> the resilience handler retries 429s
                                     -> amplified load -> D365 tenant degrades
                                     -> the capture pipeline degrades with it
```

The gateway and the integration share one D365 tenant. Bounding instances is how a third party's bad
afternoon is prevented from becoming the sales-order pipeline's bad afternoon. Start low; raising the
cap is trivial.

Note that `az functionapp create` for Flex Consumption takes a different shape than the existing
plan-based invocation in `Deploy-Eim.ps1` — a new branch, not a copied block.

### Schema widening

`ENVIRONMENT` `nvarchar(10)` → `nvarchar(30)` for parity with `SYSTEM` and headroom beyond names
like `CDB-PROD`. It is a PK column, so the migration is drop-PK, alter, recreate-PK; the table is
tiny so the operation itself is instantaneous.

`VALUE` `varchar(1000)` → `nvarchar(4000)`. `varchar` → `nvarchar` is a safe widening for existing
ASCII content. 4000 is the largest width that still fits in-row, avoiding LOB semantics, and leaves
roughly 10× headroom over a current D365 config blob. `nvarchar(max)` is deliberately not used —
nothing here justifies it, and it invites pasting certificates into a configuration column.

### Dashboard: full CRUD, with the entity list sourced from D365

Grants are administered by business and support users without database access, so consumer and
permission management are in scope for this change rather than deferred.

Free-text entity names would be the main source of support tickets: a typo produces a grant that
looks correct in the UI and silently never matches, and a mis-cased one produces a 404 from D365
rather than a clean 403. The picker is therefore populated from D365's OData **service document**
(`GET /data/`), which lists entity sets and is small — unlike `$metadata`, which is many megabytes.
The Dashboard API can fetch it via `CreateStandaloneClient`, which introduces no new credential
exposure since it already reads `ENVIRONMENTTABLE`. Cached per environment.

Custom services have no equivalent discovery endpoint and stay free-text with a "Test" action.

`CREATEDBY` / `CREATEDON` on `GATEWAYPERMISSION` capture grant provenance from the dashboard JWT,
since there is no approval gate.

## Risks / Trade-offs

**The allow-list is the primary control, not defence in depth.** Write scope means the gateway
service principal cannot be read-only in D365, so the usual backstop is unavailable.
→ Deny by default, no wildcards, explicit per-verb grants, `$batch` blocked, strict path
canonicalisation with hostile-input test coverage, and a D365 security role scoped as tightly as the
granted operations allow.

**The function key is the only network-layer gate.** No APIM and no fixed consumer egress IP.
→ Function-level keys only, header-only presentation, documented rotation runbook, alerting on 403
spikes as a probe detector. If the consumer can supply an egress CIDR range even without a single
fixed IP, Function App access restrictions should be added — that recovers most of what APIM would
have provided.

**Consumer identity is unverified in v1.** A leaked key plus a guessed `X-Consumer-Id` would be
indistinguishable from the legitimate consumer.
→ Harmless with one consumer, since there is nothing to escalate to. Enforced as a spec constraint
that a second consumer requires identity verification first.

**Any support user can grant production write access.** No role separation, by explicit decision.
→ Grant provenance via `CREATEDBY`/`CREATEDON`, the permission grid makes grants visible, and an
alert on production grant changes turns this from an incident-time discovery into a same-day one.

**D365 credentials sit in plaintext in `ENVIRONMENTTABLE.VALUE`**, and `environment-management`
deliberately allows any validated dashboard user to reveal them. This change adds a second,
internet-reachable credential under that policy.
→ Consciously accepted. `SecretRef` pointing at Key Vault is additive to the existing JSON, so
adopting it later needs no schema change. The unused `kv-d365fsc-wtc-nonprod2` vault is already
provisioned.

**Response buffering.** Rewriting `@odata.nextLink` precludes streaming.
→ Bounded by instance memory and `maximumInstanceCount`, plus a request payload size cap. Revisit
with a tail-targeted streaming rewrite only if it proves to be a real constraint.

**A gateway traffic burst can throttle the shared D365 tenant** and degrade the capture pipeline.
→ `maximumInstanceCount` and per-instance concurrency, both starting conservative.

**The `ENVIRONMENTTABLE` migration hits every environment at once**, since one `eimdb` serves dev,
dev1, bat and test.
→ Both alterations are widening and backward-compatible; no existing reader breaks. Run in a
maintenance window regardless. The table is small enough that the ALTER is effectively instant.

**Two existing `ENVIRONMENTTABLE` readers, `EIMUtilities.GetEnvironment` and `EnvironmentRepository`,
now serve a second consumer class.** Neither is modified, but both are on the gateway's request path.
→ The gateway calls `GetEnvironment` only; the dashboard repository is unchanged.

## Migration Plan

1. Widen `ENVIRONMENTTABLE` (`ENVIRONMENT` → `nvarchar(30)`, `VALUE` → `nvarchar(4000)`) via
   DbMigrator. Backward-compatible; existing readers unaffected.
2. Create `GATEWAYCONSUMER`, `GATEWAYPERMISSION`, `GATEWAYIDEMPOTENCY`. Additive; nothing reads them
   until the gateway deploys.
3. Register the gateway Entra app; grant it a D365 security role scoped to the intended operations.
   Insert `ENVIRONMENTTABLE('D365FSC-GW', '<ENV>', '<config json>')` — dashboard or migration.
4. Deploy the gateway function app (Flex Consumption). No consumers exist yet, so it is inert.
5. Create a function-level host key named for the consumer. Insert the `GATEWAYCONSUMER` row and the
   initial `GATEWAYPERMISSION` grants.
6. Deploy the dashboard API and frontend changes.

**Rollback:** delete the function key — immediate, host-enforced, no deploy. Or set
`GATEWAYCONSUMER.ENABLED = 0`. The gateway app can be stopped or deleted independently; nothing in
the capture pipeline depends on it. The schema changes are widening and need no rollback.

## Open Questions

- ~~**Does `x-functions-key` reach the isolated worker?**~~ **RESOLVED 2026-09-15** against the
  deployed dev1 app: it does. A request whose key hashed to the stored `KEYHASH` passed verification
  and reached connection resolution; a mismatched consumer was refused with 403. Verified identity is
  therefore available immediately, so every consumer should be created with `KEYHASH` populated and
  the single-enabled-consumer limitation never needs to apply in practice. The nullable column
  remains as the fallback the design was built around.
- ~~**Is `eimdb` case-insensitive?**~~ **RESOLVED**: `SQL_Latin1_General_CP1_CI_AS` on both `eimdb`
  and `eimdb_dev`, with no column-level override.
- ~~**Is `@odata.nextLink` absolute?**~~ **RESOLVED**: yes. Note the DEV org returns `$skip`/`$top`
  rather than `$skiptoken`; the verbatim query pass-through handles both.
- ~~**Is Flex Consumption available in the target region?**~~ **RESOLVED**: `centralus` is supported.
- **Can the consumer supply an egress CIDR range** even without a single fixed IP? Would enable
  access restrictions, the highest-value remaining control.
