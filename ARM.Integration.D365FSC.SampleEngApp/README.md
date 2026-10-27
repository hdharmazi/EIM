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
| `Gateway:BaseUrl` | `Gateway__BaseUrl` | `https://azf-eim-gateway-wtc-bat.azurewebsites.net` |
| `Gateway:Environment` | `Gateway__Environment` | `BAT` |
| `Gateway:ConsumerId` | `Gateway__ConsumerId` | `eng-apps` |
| `Gateway:FunctionKey` | `Gateway__FunctionKey` | *(supplied separately — do not commit)* |

Environment variables override `appsettings.json`.

## Running it

### Option 1 — `appsettings.Development.json` (recommended locally)

Create `appsettings.Development.json` next to `appsettings.json`. It is git-ignored and overrides
`appsettings.json`:

```json
{
  "Gateway": {
    "FunctionKey": "<your function key>"
  }
}
```

`dotnet run` uses the `Development` profile in `Properties/launchSettings.json`, which sets
`DOTNET_ENVIRONMENT=Development`, so the file is loaded automatically:

```powershell
dotnet run --project ARM.Integration.D365FSC.SampleEngApp
```

Visual Studio / VS Code pick up the same profile. To run the built executable directly, set the
environment yourself:

```powershell
$env:DOTNET_ENVIRONMENT = "Development"
.\ARM.Integration.D365FSC.SampleEngApp\bin\Debug\net10.0\ARM.Integration.D365FSC.SampleEngApp.exe
```

### Option 2 — environment variable

```powershell
$env:Gateway__FunctionKey = "<your function key>"
dotnet run --project ARM.Integration.D365FSC.SampleEngApp --no-launch-profile
```

```bash
export Gateway__FunctionKey="<your function key>"
dotnet run --project ARM.Integration.D365FSC.SampleEngApp --no-launch-profile
```

Settings load in this order, later wins: `appsettings.json` → `appsettings.Development.json`
(only when `DOTNET_ENVIRONMENT=Development`) → environment variables.

## Smoke test without code

```bash
curl -i -G "https://azf-eim-gateway-wtc-bat.azurewebsites.net/api/v1/d365fsc/BAT/data/WTC_EngineeringAppStatuses" \
  -H "x-functions-key: <your function key>" \
  -H "X-Consumer-Id: eng-apps" \
  -H "Accept: application/json" \
  --data-urlencode 'cross-company=true' \
  --data-urlencode "\$filter=ShippingDateConfirmed ge 2026-07-28 and ShippingDateConfirmed le 2026-07-28 and dataAreaId eq 'wtc'"
```

## Converting the existing `var line = ...` query (CRD 3874)

The print-list / label run currently builds `List<LineTable>` with LINQ-to-SQL against the replicated
database:

```csharp
var line = (from sl in ctx.SALESLINES
            join stb in ctx.SALESTABLES
              on new { prop1 = sl.SALESID, prop2 = sl.SALESSTATUS }
              equals new { prop1 = stb.SALESID, prop2 = stb.SALESSTATUS }
            where sl.ITEMROUTEID.StartsWith("0SLV")
               && sl.SHIPPINGDATECONFIRMED >= printList.dateRunStart
               && sl.SHIPPINGDATECONFIRMED <= printList.dateRunEnd
               && sl.DATAAREAID.Equals("WTC")
            join prodT in ctx.PRODTABLEs
              on new { prop1 = sl.ITEMID, prop2 = "WTC" }
              equals new { prop1 = prodT.ITEMID, prop2 = prodT.DATAAREAID } into ProdTable
            from pt in ProdTable.DefaultIfEmpty()
            join td in ctx.WTC_TRAILERDATA on sl.ITEMID equals td.ITEMID into trailData
            from trailerD in trailData.DefaultIfEmpty()
            select new LineTable {
                SID = sl.SALESID, salesLine = sl, salesTable = stb,
                prodTable = pt, trailData = trailerD }).ToList();
```

D365 now performs those joins inside the `WTC_EngineeringAppStatuses` entity. The replacement is:

```csharp
var line = (await gatewayClient.GetEngineeringAppStatusesAsync(
        DateOnly.FromDateTime(printList.dateRunStart),
        DateOnly.FromDateTime(printList.dateRunEnd),
        dataAreaId: "wtc",
        itemRoutePrefix: "0SLV"))          // optional; pass null for every route
    .Select(LineTable.FromStatus)
    .ToList();
```

That call sends this request and follows `@odata.nextLink` until every page has been read:

```
GET /api/v1/d365fsc/BAT/data/WTC_EngineeringAppStatuses?cross-company=true
    &$filter=startswith(ItemRouteId,'0SLV')
         and ShippingDateConfirmed ge 2026-07-28
         and ShippingDateConfirmed le 2026-07-28
         and dataAreaId eq 'wtc'
```

### Steps

1. Copy `D365FscGatewayClient.cs`, `GatewayOptions.cs` and the `Models/` folder into your app (or
   reference this project). Register the client with `services.AddHttpClient<D365FscGatewayClient>()`
   and bind the `Gateway` configuration section, as `Program.cs` does.
2. Replace the LINQ query with the call above. Remove the `DataContext` (`ctx`) and the
   replicated-database connection string once nothing else uses them.
3. `LineTable.cs` keeps the old property names (`SID`, `salesLine`, `salesTable`, `prodTable`,
   `trailData`, plus upper-case column names such as `salesLine.ITEMID`), so most downstream code
   compiles unchanged. If your generated LINQ-to-SQL classes named a column differently, rename the
   property in `LineTable.cs` or adjust the caller.
4. Review the behaviour differences below and fix any code they affect.

### Behaviour differences from the replicated database

| Area | Before (SQL replica) | After (API) |
| --- | --- | --- |
| Enum fields (`SALESSTATUS`, `PRODSTATUS`, `DRAWINGAPPROVED`, `DRAWINGREQUIRED`) | Integer, e.g. `1` | Label string, e.g. `"Backorder"`. Change `== 1` comparisons to compare against the label. |
| Sales responsible worker | Worker `RecId` | Full name in `salesTable.WORKERRESPONSIBLENAME` |
| Unset dates | `1900-01-01` | Still `1900-01-01` (`LineTable.NoDate`); D365 sends `1900-01-01T12:00:00Z` and the mapper converts it |
| No production order / trailer data | `prodTable` / `trailData` is `null` | Still `null`. Detected by an empty `ProdId` / empty trailer fields |
| Columns | Every column (`sl.*`, `stb.*`, ...) | Only the fields the entity exposes. A column that is missing needs a change to the D365 entity; request it rather than working around it. |
| Company | `DATAAREAID.Equals("WTC")` | `dataAreaId eq 'wtc'` with `cross-company=true` (D365 is case-insensitive here) |
| Latency | Single SQL round-trip | One HTTP call per page. Keep the date range as narrow as the print run needs. |
| Data freshness | Replica lag | Live D365 data |

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
  URL, so you can request it as-is with the same headers. `GetEngineeringAppStatusesAsync` does this
  for you and returns all rows.
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
| `Program.cs` | Demo: configures the host, replaces the old `var line = ...` query, prints the rows |
| `D365FscGatewayClient.cs` | Typed `HttpClient` — headers, query encoding, error handling |
| `GatewayOptions.cs` | Bound configuration |
| `Models/WtcEngineeringAppStatus.cs` | The `WTC_EngineeringAppStatuses` entity (one flat row) |
| `Models/LineTable.cs` | Re-nests a flat row into the old `LineTable` shape |
| `Models/ODataCollectionResponse.cs` | OData envelope (`value`, `@odata.nextLink`) |
| `Models/GatewayError.cs` | Gateway error body |
| `Models/GatewayException.cs` | Raised on any non-2xx response |
