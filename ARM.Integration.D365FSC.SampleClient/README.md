# D365 F&O Gateway — C# sample client

A minimal, runnable console app showing how to read data from Dynamics 365 Finance & Operations
through the WTC integration gateway. It performs a single `GET` against the
`WTC_EngineeringAppStatuses` data entity and deserialises the OData response into typed models.

## What the gateway gives you

You never talk to D365 directly and you never hold D365 credentials. You call the gateway with two
headers; it authenticates to D365 on your behalf and returns the raw OData JSON.

```
GET https://<gateway-host>/api/v1/d365fsc/<ENVIRONMENT>/data/<Entity>?<odata-query>
x-functions-key: <your function key>
X-Consumer-Id:   <your consumer id>
Accept:          application/json
```

## Configuration

`appsettings.json` holds everything except the secret:

| Setting | Environment variable | Example |
| --- | --- | --- |
| `Gateway:BaseUrl` | `Gateway__BaseUrl` | `https://azf-d365fsc-gateway-wtc-nonprod.azurewebsites.net` |
| `Gateway:Environment` | `Gateway__Environment` | `DEV2` |
| `Gateway:ConsumerId` | `Gateway__ConsumerId` | `eng-apps` |
| `Gateway:FunctionKey` | `Gateway__FunctionKey` | *(supplied separately — do not commit)* |

Environment variables override `appsettings.json`.

## Running it

```powershell
$env:Gateway__FunctionKey = "<your function key>"
dotnet run --project ARM.Integration.D365FSC.SampleClient
```

```bash
export Gateway__FunctionKey="<your function key>"
dotnet run --project ARM.Integration.D365FSC.SampleClient
```

## Smoke test without code

```bash
curl -i -G "https://azf-d365fsc-gateway-wtc-nonprod.azurewebsites.net/api/v1/d365fsc/DEV2/data/WTC_EngineeringAppStatuses" \
  -H "x-functions-key: <your function key>" \
  -H "X-Consumer-Id: eng-apps" \
  -H "Accept: application/json" \
  --data-urlencode 'cross-company=true' \
  --data-urlencode "\$filter=ShippingDateConfirmed ge 2026-07-28 and ShippingDateConfirmed le 2026-07-28 and dataAreaId eq 'wtc'"
```

## Things worth knowing

- **Encode your own query string.** The gateway forwards the query byte-for-byte and never
  re-encodes it. Percent-encode values such as `$filter` yourself — see `D365FscGatewayClient`.
  (This is deliberate: it is what lets `$skiptoken` survive paging intact.)
- **`cross-company=true` is not added for you.** Omit it and D365 scopes results to a single company.
- **Never put the key in the query string.** A `?code=` parameter is rejected with `400 bad_request`.
- **Unset dates come back as `1900-01-01T12:00:00Z`**, not `null`. Use
  `WtcEngineeringAppStatus.IsUnset(...)`.
- **Every response carries `X-Correlation-Id`.** Quote it in support tickets; it also appears in
  gateway error bodies.
- **Paging.** When more rows exist the response has `@odata.nextLink`, already rewritten to a gateway
  URL, so you can request it as-is with the same headers.
- **Not supported:** `$batch`, `$metadata`, and `DELETE`.
- **Limits.** Request bodies max 4 MiB; the gateway aborts after 3m45s. It does not retry — a `429`
  and its `Retry-After` are relayed to you to handle.
- `Idempotency-Key` is required on `POST` and honoured on `PATCH`. It is ignored on `GET`, so this
  sample does not send one.

## Response status codes

| Status | `error` | Meaning |
| --- | --- | --- |
| `200` | — | Success; body is raw D365 OData JSON |
| `400` | `bad_request` | Missing `X-Consumer-Id`, `code` in the query string, or a malformed path |
| `400` | `content_type_required` | Body sent without a `Content-Type` header |
| `400` | `idempotency_key_required` | `POST` without `Idempotency-Key` |
| `401` | *(no body)* | Missing or invalid `x-functions-key` |
| `403` | `forbidden` | Unknown/disabled/expired consumer, or no grant for this entity and verb |
| `404` | — | Environment not configured, or unknown route |
| `405` | — | Verb not allowed on this route |
| `409` | `idempotency_key_reused` | Same key replayed with a different request |
| `409` | `request_in_progress` | An identical request is still running |
| `413` | `payload_too_large` | Request body over 4 MiB |
| `429` | — | Relayed from D365; honour `Retry-After` |

Gateway errors use the shape `{"error":"forbidden","correlationId":"9f2c..."}`.

## Project layout

| File | Purpose |
| --- | --- |
| `Program.cs` | Demo: configures the host, runs one query, prints the rows |
| `D365FscGatewayClient.cs` | Typed `HttpClient` — headers, query encoding, error handling |
| `GatewayOptions.cs` | Bound configuration |
| `Models/WtcEngineeringAppStatus.cs` | The `WTC_EngineeringAppStatuses` entity |
| `Models/ODataCollectionResponse.cs` | OData envelope (`value`, `@odata.nextLink`) |
| `Models/GatewayError.cs` | Gateway error body |
| `Models/GatewayException.cs` | Raised on any non-2xx response |
