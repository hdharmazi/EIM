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

### Headers

| Header | When | Notes |
|---|---|---|
| `x-functions-key` | Always | Your key. **Header only** — a key in the query string is rejected with 400, because query strings are recorded in logs along the way. |
| `X-Consumer-Id` | Always | Your consumer id, e.g. `acme-erp`. Not a secret. |
| `Idempotency-Key` | Every `POST` | See [Idempotency](#idempotency). |
| `Content-Type` | With a body | `application/json` |
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
| `400` | Malformed path, missing `X-Consumer-Id`, key in the query string, or a `POST` with no `Idempotency-Key` |
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

Each consumer gets its **own function-level key**, named for them.

```bash
az functionapp keys set --name <gateway-app> --resource-group <rg> \
    --key-type functionKeys --key-name acme-erp --key-value <generated>
```

**Never issue the master key.** It can call the admin API, which can enumerate and mint further
keys.

Then create the consumer and its grants in the dashboard under **Gateway Consumers**. A new consumer
is disabled until enabled deliberately, and holds no permissions until they are granted.

### Rotation

Additive, so there is no downtime and no deployment:

1. Create a second key, `acme-erp-2`.
2. Give it to the consumer and let them cut over.
3. Delete `acme-erp`.

Agree a cadence now — 90 days is a reasonable default. Nobody invents one during an incident.

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

- **One enabled consumer until key verification is switched on.** While `KEYHASH` is null, identity
  comes from the `X-Consumer-Id` header and nothing proves it. With a single consumer there is
  nothing to escalate to; with two, spoofing the header would select someone else's grants. The API
  refuses to enable a second consumer in that state.
- **Keep the gateway service principal's D365 security role as tight as the granted operations
  allow.** It cannot be read-only now that writes are in scope, so it is a backstop rather than a
  guarantee — but it still caps the worst outcome.
- **`maximumInstanceCount` is the only rate limiter.** The gateway and the capture pipeline share a
  D365 tenant, so an unbounded burst here can throttle the tenant and degrade order processing.
  Raise it deliberately.
- **D365 credentials are stored in plaintext** in `ENVIRONMENTTABLE.VALUE`, and Environment Settings
  allows any dashboard admin to reveal them. Accepted for now; Key Vault references are designed for
  and deferred.
