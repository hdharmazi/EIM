## Why

Third-party applications need to call D365 F&SC entities and custom services, but cannot be given
Entra app registrations or OAuth client credentials of their own. Today there is no HTTP entry point
into the solution at all — every existing trigger is Service Bus or Timer based, and the async
capture pipeline (`MESSAGEQUEUETABLE`, orchestrations) is the wrong shape for a synchronous
request/response caller.

The connection-resolution half of this problem is already solved and in production use:
`EIMUtilities.GetEnvironment(system, environment)` returns a JSON blob from `ENVIRONMENTTABLE` that
`D365FSCServiceCollectionExtensions.CreateStandaloneClient()` deserializes directly into a working
OData client. What is missing is an authenticated, authorized, environment-addressable HTTP front
door over it.

## What Changes

- **New Azure Function app** (`ARM.Integration.D365FSC.Gateway`), HTTP-triggered, deployed
  independently of `ARM.Integration.EIM.Customer`. Separate blast radius, separate scaling profile,
  internet-facing. Shares `ARM.Integration.Core`.
- **Transparent pass-through** to D365 F&SC over two routes that mirror D365's own path structure:
  - `/api/v1/d365fsc/{environment}/data/{*path}` — data entities and bound actions (GET, POST, PATCH)
  - `/api/v1/d365fsc/{environment}/services/{group}/{service}/{operation}` — custom services (POST)
- **Raw HTTP forwarding** rather than the typed `ID365FSCClient` CRUD methods. One code path serves
  entities, bound actions and custom services, and absorbs future D365 surface without code changes.
- **Authentication** via Azure Functions function-level host keys. No APIM, no custom key store.
  `X-Consumer-Id` carries consumer identity; `GATEWAYCONSUMER.KEYHASH` is nullable so key-derived
  verification can be enabled later without a client contract change.
- **Authorization** by explicit allow-list, deny by default. New `GATEWAYCONSUMER` and
  `GATEWAYPERMISSION` tables grant a consumer a specific verb on a specific resource in a specific
  environment. No wildcards.
- **`$batch` is rejected.** It bundles arbitrary sub-requests that the allow-list never inspects and
  is therefore a complete authorization bypass.
- **Idempotency** required on POST via an `Idempotency-Key` header, backed by a new
  `GATEWAYIDEMPOTENCY` table plus a purge timer. Prevents duplicate ERP records on client retry.
- **Server-side `@odata.nextLink` and `Location` rewriting** so responses point back at the gateway
  and standard OData clients page transparently.
- **Dashboard screens** for managing consumers and permissions, so business admins and support staff
  can grant access without database access.
- **`ENVIRONMENTTABLE` widened**: `ENVIRONMENT` `nvarchar(10)` → `nvarchar(30)`,
  `VALUE` `varchar(1000)` → `nvarchar(4000)`.
- **Gateway credentials are a separate Entra app registration**, stored as
  `SYSTEM = 'D365FSC-GW'` in `ENVIRONMENTTABLE`. Distinct from the integration app registration, so
  D365 attributes gateway-originated records to a different service principal.

Not in scope: `DELETE` (modelled but not grantable), Key Vault secret references, per-consumer rate
limiting, request payload auditing.

## Capabilities

### New Capabilities
- `d365fsc-gateway`: HTTP pass-through to D365 F&SC — routing, environment and connection
  resolution, request forwarding, response rewriting, error shape, hosting profile.
- `gateway-access-control`: consumer identity, the deny-by-default allow-list model, grant
  provenance, and the authorization bypasses that must be blocked.
- `gateway-idempotency`: `Idempotency-Key` semantics for POST — replay, conflict detection,
  concurrent-request handling, retention.
- `gateway-consumer-console`: dashboard screens for managing gateway consumers and their
  permission grants.

### Modified Capabilities
- `environment-management`: environment names may be up to 30 characters and values up to 4000
  characters; a connection profile may serve the gateway rather than the integration pipeline.
- `deployment-automation`: `Deploy-Eim.ps1` gains a `gateway` component targeting a Flex Consumption
  Linux function app, which uses a different `az functionapp create` shape than the existing
  plan-based function app.

## Impact

**New**
- `ARM.Integration.D365FSC.Gateway/` — function app project (net10.0, isolated worker).
- Tables `GATEWAYCONSUMER`, `GATEWAYPERMISSION`, `GATEWAYIDEMPOTENCY` + migration scripts.
- `GatewayConsumerController` / `GatewayPermissionController` + repositories in
  `ARM.Integration.Dashboard.API`.
- `ARM.Integration.Dashboard.Frontend/src/features/gateway/` — consumer list, consumer form,
  permission matrix.

**Modified**
- `ENVIRONMENTTABLE` column widths (migration touches the shared `eimdb`, which serves dev, dev1,
  bat and test simultaneously — a single migration affects every environment at once).
- `Script/Deploy-Eim.ps1` — new `gateway` component.

**Unchanged by design**
- `D365FSCClient`, `D365AuthDelegatingHandler`, `CreateStandaloneClient` and every orchestration.
  The gateway reuses the auth handler but adds its own client-caching factory rather than modifying
  the shared helper, which is on the hot path of the live capture pipeline.

**Accepted risks**
- D365 client secrets remain in plaintext in `ENVIRONMENTTABLE.VALUE`, and `environment-management`
  deliberately allows any validated dashboard user to reveal them. This change adds a second,
  internet-reachable credential to that table without changing that policy. Key Vault references are
  designed for (`SecretRef` is additive to the existing JSON) but deferred.
- No role separation on permission grants: a support user may grant write access in any environment,
  including production. Mitigated by grant provenance and alerting, not by an approval gate.
- The gateway service principal cannot be read-only in D365, because write scope is in scope. The
  allow-list is therefore the primary control rather than defence in depth.
- No IP allow-listing is possible — the consumer has no fixed egress address — so the function key
  is the sole network-layer gate.
