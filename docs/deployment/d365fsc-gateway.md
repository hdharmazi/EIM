# D365 F&SC pass-through gateway

An HTTP front door that lets a third-party application call D365 Finance & Supply Chain without
holding Entra credentials of its own. The gateway authenticates to D365 as a dedicated service
principal, and decides what each caller may reach from data held in the EIM database.

Two audiences here: the **integration guide** is what you hand to a consumer, and **operations**
is for whoever administers it.

---

## Integration guide

### Base address

```
https://<gateway-host>/api/v1/d365fsc/{environment}/...
```

`{environment}` is case-insensitive — `DEV`, `Dev` and `dev` are the same environment. There is no
default; it must always be supplied.

One gateway serves every non-production environment. The environment in the path selects the D365
profile, so the host name does not change when you target a different one — only the path does.

The non-production gateway is `https://azf-eim-gateway-wtc-bat.azurewebsites.net`. It is a new app, so
keys issued on any earlier gateway app do not work there; each consumer gets a new key (see
[Moving a consumer to the shared gateway](#moving-a-consumer-to-the-shared-gateway)).

### Headers

| Header | When | Notes |
|---|---|---|
| `x-functions-key` | Always | Your key. **Header only** — a key in the query string is rejected with 400, because query strings are recorded in logs along the way. |
| `X-Consumer-Id` | Always | Your consumer id, e.g. `acme-erp`. Not a secret. |
| `Idempotency-Key` | Every `POST` | See [Idempotency](#idempotency). |
| `Content-Type` | With a body | **Required whenever you send a body.** Omit it and the request is refused with `content_type_required`. Without that check D365 receives the body as `application/octet-stream` and answers with a deserialiser stack trace whose real cause is buried at the very end. |
| `If-Match` | Optional on `PATCH` | Passed through for optimistic concurrency. |
| `Prefer` | Optional | Passed through — use `odata.maxpagesize=N` to control page size. |

Every response carries `X-Correlation-Id`. Quote it in any support request; it locates the exact
call in telemetry.

### Data entities

```http
GET /api/v1/d365fsc/DEV/data/CustomersV3?$filter=CustomerAccount eq 'US-001'&cross-company=true
x-functions-key: <key>
X-Consumer-Id: acme-erp
```

```http
POST /api/v1/d365fsc/DEV/data/CustomersV3
Content-Type: application/json
Idempotency-Key: acme-order-12345

{ "dataAreaId": "usmf", "CustomerAccount": "US-999", "OrganizationName": "Acme Ltd" }
```

```http
PATCH /api/v1/d365fsc/DEV/data/CustomersV3(dataAreaId='usmf',CustomerAccount='US-999')?cross-company=true
If-Match: *
Content-Type: application/json

{ "OrganizationName": "Acme Limited" }
```

⚠️ **`cross-company=true` is yours to send.** The gateway forwards your query string verbatim and
adds nothing. Omit it and D365 scopes results to the service principal's default company only — the
query appears to work and quietly returns a subset. This is the single most common surprise.

### Bound actions

```http
POST /api/v1/d365fsc/DEV/data/SalesOrderHeadersV2(dataAreaId='usmf',SalesOrderNumber='SO-0001')/Microsoft.Dynamics.DataEntities.Confirm
Idempotency-Key: acme-confirm-SO-0001
Content-Type: application/json

{ }
```

An action is a separate permission from reading its entity. Being able to read a sales order does
not imply being able to confirm one.

### Custom services

```http
POST /api/v1/d365fsc/DEV/services/WTCServices/CustomerService/CreateCustomer
Idempotency-Key: acme-create-US-999
Content-Type: application/json

{ "_customerAccount": "US-999", "_name": "Acme Ltd", "_dataAreaId": "usmf" }
```

### Paging

`@odata.nextLink` already points at the gateway, so follow it as you normally would. Standard OData
client libraries page automatically with no extra code.

```jsonc
{
  "value": [ /* … */ ],
  "@odata.nextLink": "https://<gateway-host>/api/v1/d365fsc/DEV/data/CustomersV3?$skiptoken=…"
}
```

Absence of `@odata.nextLink` means you have the last page.

### Idempotency

Required on every `POST`; accepted but not required on `PATCH`.

It exists because a lost response is indistinguishable from a request that never arrived. Retrying
is the right thing to do — and without a key it is also what creates a duplicate record in the ERP.

**Generate the key once per logical operation, not once per HTTP attempt.** A fresh value on each
retry is exactly equivalent to sending none at all, and is the usual way this gets implemented
wrongly.

The easiest correct approach is to derive it from your own data — `acme-order-12345` — so it is
naturally stable across retries and process restarts with nothing to persist. A UUID works too, but
you must store it alongside the pending operation.

| Situation | Response |
|---|---|
| First attempt | Forwarded normally |
| Retry, same key and body | The original status and body, replayed. D365 is not called again. |
| Same key, different body | `409` — replaying would return the wrong record |
| Same key, still in flight | `409` — retry shortly |
| Key older than the retention window | Treated as a new request |

### Responses and errors

Upstream status codes and bodies are passed through. Gateway-generated errors look like:

```json
{ "error": "forbidden", "correlationId": "9f2c…" }
```

| Status | Meaning |
|---|---|
| `400` | Malformed path, missing `X-Consumer-Id`, key in the query string, a body with no `Content-Type`, or a `POST` with no `Idempotency-Key` |
| `401` | Missing or invalid function key (rejected by the platform) |
| `403` | Not granted. Deliberately gives no detail about what exists. |
| `404` | Environment not configured, or an unrecognised route |
| `405` | Verb not supported on that route |
| `409` | Idempotency conflict |
| `413` | Request body over the limit |
| `429` / `5xx` | Passed through from D365 |

A `403` is worth checking with your administrator before assuming a bug — it usually means a grant
is missing rather than that anything is broken.

### Not supported

- **`$batch`** — rejected. A batch bundles sub-requests the permission model never inspects, so one
  grant would silently confer everything.
- **`$metadata`** — rejected.
- **`DELETE`** — not grantable in this release.

---

## Operations

### Issuing a key

Each consumer gets its **own host key**, named for them.

```bash
az functionapp keys set --name <gateway-app> --resource-group <rg> \
    --key-type functionKeys --key-name acme-erp --key-value <generated>
```

Despite the `functionKeys` type name, this creates a **host key** — what the portal lists under "Host
keys (all functions)". That is the right choice here: a consumer generally needs both `D365Data` and
`D365Service`, and a host key covers every HTTP function in the app. Per-function keys, created with
`az functionapp function keys set`, would mean issuing a separate key on each function and contriving
them to share a value, since a consumer can only send one.

The consequence is worth stating: a consumer's key reaches **every** HTTP function in the gateway app.
Today every one of them authorizes against that consumer's grants, so there is no extra exposure — but
that is a constraint on what may be added to this app, not an accident.

**Never issue the master key.** It can call the admin API, which can enumerate and mint further
keys.

Then create the consumer and its grants in the dashboard under **Gateway Consumers**. A new consumer
is disabled until enabled deliberately, and holds no permissions until they are granted.

**Gateway address** on the consumer form is the gateway that consumer calls. Leave it blank to use the
gateway this dashboard is configured with, which is the usual case; set it to point a consumer at a
different function app without a redeploy.

Finally, use **Set key** on the consumer row and paste the key you just generated. The dialog shows
which gateway the key will be checked against, and that gateway's base address — the same address the
consumer calls. A consumer with its own address is checked there; one without it is checked against
the configured gateway, and where more than one is configured you choose which applies. The dashboard
will not guess.

The address is constrained rather than free. It must be `https`, must carry no credentials, query
string or fragment, and its host must sit under one of `Gateway:AllowedHostSuffixes` — by default
`azurewebsites.net`. Two reasons: the dashboard server makes the verification request itself, so an
unrestricted address would let this screen probe anything the server can reach; and verification only
means something if the submitter cannot nominate a host that never returns 401 and collect a green
tick that proves nothing.

The dashboard checks the key against that gateway before saving it, then hashes it and keeps only the
hash — the value cannot be read back, so take your copy before you close the dialog.

Three outcomes, and the difference matters:

| Shown | Means | Do |
|---|---|---|
| **Verified**, with a date and the application name | The gateway accepted the key | Nothing |
| **Rejected** | The gateway returned 401, so Azure never issued that key for that application | Check it was copied whole, and that it came from the right app |
| **Could not be checked** | The gateway did not answer | Try again; nothing was stored |

A rejection and a failed check are deliberately not the same message. Treating an outage as a bad key
sends you looking for a fault in a key that is perfectly good.

Until a key is registered the consumer shows as *Unverified*, identity is taken on trust from the
`X-Consumer-Id` header, and the API will refuse to enable a second consumer in that state.

**Verified is a point in time, not a guarantee.** It records that the gateway accepted the key on
that date. If the key is deleted in Azure afterwards, the badge does not change — which is why the
date is shown rather than a bare tick.

A consumer registered before verification existed, or one registered against a local gateway, shows
as **Key set, unchecked**. That is not the same as verified and is displayed differently on purpose:
a key is recorded and will be enforced, but nothing ever confirmed Azure issued it.

### Rotation

Additive, so there is no downtime and no deployment:

1. Create a second key, `acme-erp-2`.
2. Give it to the consumer and let them switch.
3. Delete `acme-erp`.

Agree a cadence now — 90 days is a reasonable default. Nobody invents one during an incident.

Registering a key is what binds it to a consumer, so a verified consumer can hold only one
registered key at a time. **Set key** during step 2 switches the binding immediately; requests
presenting `acme-erp` then fail identity verification even though the platform still accepts the
key. For a verified consumer, either register the new key at the moment they switch, or accept a
short window of 403s and plan it.

### Moving a consumer to the shared gateway

Keys do not cross applications. A key issued on one gateway app returns 401 from any other gateway
app, so a consumer cannot be pointed at a different app without a new key. No key is copied between
apps.

This is why `eng-apps` and `eng-apps-dev` exist as separate consumer rows: one logical consumer split
in two so it could hold a key for each per-environment gateway. Once a consumer is on the shared
gateway that reason disappears, and the rows can be collapsed — though that changes the `X-Consumer-Id`
the caller sends, so it is a coordinated change rather than a tidy-up.

To move one consumer:

1. Issue a host key for them on `azf-eim-gateway-wtc-bat`.
2. Register it in the dashboard with **Set key**.
3. Give them the new base address in the same step.

Steps 2 and 3 are one operation, not two. Registering the new key replaces the stored hash
immediately, so between those steps the consumer's old key is still accepted by the old app but no
longer matches the consumer — a 403 with a confusing message. Update `GATEWAYCONSUMER.GATEWAYURL` as
each consumer confirms.

### Hosting

The gateway runs on the App Service plan `asp-eim-wtc-bat` (P0v3, Linux) with Always On, alongside
the bat and test Automation apps. It is deployed by the `gateway` component of `Deploy-Wilson.ps1`
and the `Deploy_bat` pipeline stage ([deployment.md](deployment.md)). It reaches `eimdb` with its
managed identity. Always On keeps the host warm, so there are no cold starts and `IdempotencyPurge`
fires on schedule.

### Revoking access

| Urgency | Action | Effect |
|---|---|---|
| **Immediate** | Delete the key in Azure | Enforced by the platform, instantly, across every instance. No database, no dashboard, no deploy. |
| Orderly | Set the consumer to disabled in the dashboard | Takes effect on the next request |

Use key deletion for anything urgent. `ENABLED` is for offboarding and temporary suspension.

### Monitoring

Authorization denials are the closest thing to intrusion detection this design has — there is no IP
allow-list, so the key is the only network-layer gate.

```kusto
// Denial rate — alert on a sharp rise
traces
| where message startswith "Gateway denied"
| summarize denials = count() by bin(timestamp, 5m), tostring(customDimensions.ConsumerId)
```

```kusto
// Production grant changes: not gated by approval, so they should be noticed the same day
traces
| where message startswith "GatewayGrantCreated" or message startswith "GatewayGrantDeleted"
| project timestamp, customDimensions.Actor, customDimensions.ConsumerId,
          customDimensions.Environment, customDimensions.Resource, customDimensions.Verb
```

```kusto
// Requests still running on unverified identity
traces | where message contains "UNVERIFIED identity"
```

### Standing constraints

- **No write verb may be granted while the gateway borrows the integration service principal.** The
  `D365FSC-GW` profiles currently carry the same client id as the `D365FSC` ones, so D365 sees
  gateway traffic as pipeline traffic. Reads are unaffected. Writes are not: D365 stamps the creating
  principal onto the record, and no later change can correct it. Register a dedicated app before the
  first POST grant.
- **One enabled consumer until key verification is switched on.** While `KEYHASH` is null, identity
  comes from the `X-Consumer-Id` header and nothing proves it. With a single consumer there is
  nothing to escalate to; with two, spoofing the header would select someone else's grants. The API
  refuses to enable a second consumer in that state.
- **Keep the gateway service principal's D365 security role as tight as the granted operations
  allow.** It cannot be read-only now that writes are in scope, so it is a backstop rather than a
  guarantee — but it still caps the worst outcome.
- **The plan size is the rate limiter.** The gateway and the capture pipeline share a D365 tenant, so
  an unbounded burst here can throttle the tenant and degrade order processing. On Flex Consumption
  `maximumInstanceCount` bounded this; on the shared single-instance P0v3 plan, throughput is bounded by
  one vCPU shared with the other apps, which is tighter. Scaling the plan out or up loosens it, so do
  that deliberately. The bound is shared between every non-production environment — a load test in one
  environment can throttle another.
- **A consumer's host key reaches every HTTP function in the gateway app.** No HTTP function may be
  added to this app unless it authorizes the caller against their grants, or is harmless to every
  consumer holding a key.
- **D365 credentials are stored in plaintext** in `ENVIRONMENTTABLE.VALUE`, and Environment Settings
  allows any dashboard admin to reveal them. Accepted for now; Key Vault references are designed for
  and deferred.
