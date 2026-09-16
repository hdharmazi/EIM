## Context

A consumer authenticates with an Azure Functions **host key** on a gateway app. Azure validates the key
and returns 401 before the worker starts; the gateway then compares `SHA256(x-functions-key)` against
`GATEWAYCONSUMER.KEYHASH` to decide *which* consumer is calling. The hash exists because Azure passes
the key's **value** to the app but never its **name** — so the name an operator sees in the portal
(`eng-apps`) is invisible to the gateway.

Registration today is a bare hash-and-store. Nothing checks that the submitted string is a key at all.

Three facts shape the design:

1. **One gateway serves every non-production environment.** `share-gateway-across-nonprod` replaces the
   per-environment apps with `azf-d365fsc-gateway-wtc-nonprod`, so there is a single address to probe.
   This change was originally written against three gateway apps and carried machinery to work out
   which one a key belonged to; that machinery is gone.
2. **Keys do not cross applications.** Verified 2026-09-16: a key issued on the dev gateway returns 401
   from the test gateway. So a probe proves the key belongs to the app that answered, and a consumer
   moved to a different app needs a new key.
3. **Both gateway routes are `AuthorizationLevel.Function`.** The platform enforces the key, so a probe
   can distinguish "Azure rejected this" from "Azure accepted this" without the gateway's cooperation.

## Goals / Non-Goals

**Goals:**

- Refuse a key the gateway will not accept, at the moment it is registered.
- Record what was verified and when, rather than asking an administrator to assert it.
- Keep a failure to reach the gateway clearly distinct from the gateway rejecting the key.
- Leave the runtime authorization path untouched.

**Non-Goals:**

- Re-verifying keys on a schedule. A key deleted in Azure after registration will still read as
  verified-on-a-date. Detecting that is a monitoring problem, not a registration problem.
- Proving the key belongs to the *intended* consumer. A probe proves Azure issued the key for that app;
  it cannot prove the operator pasted the right consumer's key. Grants remain the control that limits
  the damage.
- Supporting one consumer across multiple gateway apps. That needs multiple hashes per consumer, and
  the reason to want it disappears once there is one gateway.

## Decisions

### Probe with the existing data route, not a new health endpoint

```
GET {gatewayUrl}/api/v1/d365fsc/PROBE/data/probe
x-functions-key: <submitted key>
(no X-Consumer-Id)
```

- **401** — Azure did not issue this key for this app.
- **anything else** — the platform accepted the key and the worker ran.

Verified against the deployed gateways on 2026-09-16:

| Request | Result |
|---|---|
| bogus key → dev gateway | `401`, empty body |
| genuine dev key, no `X-Consumer-Id` → dev gateway | `400 {"error":"bad_request"}` |
| genuine **dev** key → **test** gateway | `401`, empty body |

The third row is the one that matters. Keys do not cross applications, so iterating the configured
gateways genuinely identifies which one issued a key rather than merely detecting that a key is
well-formed. Without that property the whole design would be decoration.

Omitting `X-Consumer-Id` is what makes this safe. `GatewayAuthorizer` rejects a missing consumer id
before it looks up the consumer, before it evaluates grants, and before it resolves the environment's
D365 credentials. So a probe reads no secret, touches no D365 tenant, writes no idempotency row, and
cannot be made to do any of those by choosing a different path.

**Alternative considered: a dedicated keyed `ping` function.** Rejected — it is more code, it needs
deploying to every gateway before the dashboard can rely on it, and because these are *host* keys it
would be reachable by every consumer anyway. The existing route already answers the only question being
asked.

**Alternative considered: the Functions admin API.** Rejected outright. Listing an app's keys requires
`_master`, and the operations guide correctly forbids putting the master key anywhere near this system.
The probe deliberately needs no privilege beyond the key already in the operator's hand.

### Probe the one shared gateway

`share-gateway-across-nonprod` collapses the per-environment gateway apps into a single
`azf-d365fsc-gateway-wtc-nonprod`, so there is one address to probe and the answer is simply yes or no.

An earlier draft of this design iterated every configured gateway and treated whichever one accepted
the key as the answer, turning validation and identification into one operation. That existed only to
manage several gateway apps. With one app it is machinery without a purpose, and it has been removed
rather than kept "in case".

The application name is still recorded on success, so the stored result says what was checked rather
than merely that something was.

### Three outcomes, not two

| Outcome | Condition | Result |
|---|---|---|
| Verified | the gateway returned non-401 | store hash, app and timestamp |
| Rejected | the gateway returned 401 | refuse, say it was rejected |
| Unknown | the gateway was unreachable | refuse, and say the check could not run |

Collapsing *unknown* into *rejected* would tell an administrator their good key is bad during a gateway
outage, and they would go looking for a problem in the key. Both refuse the write — the difference is
entirely in what the operator is told, which is the point.

### Verification is administrative, and the gateway never learns about it

The new columns are written by the dashboard and read by nothing at request time. `GatewayAuthorizer`
continues to compare hashes and is unaware verification happened.

This keeps a dashboard or probe defect from becoming a gateway outage, and keeps the runtime path as
narrow as it is today. It also means the stored app name can go stale — accepted, because nothing
depends on it being right.

### Schema

Two additive nullable columns on `GATEWAYCONSUMER`: the gateway app that accepted the key, and when.
Nullable because existing rows were registered before verification existed, and their key may well be
valid. Backfilling would require the key values, which are deliberately unrecoverable.

A null app therefore means "not known", never "no app".

### The gateway application is identified by app name, not hostname

The stored and displayed identifier is the function app name — `azf-d365fsc-gateway-wtc-test` — while
the base URL stays in configuration. The app name is what the operator sees in the portal's App Keys
blade and what `az functionapp keys` takes, so it is the value that answers "where do I go to revoke
this key". A hostname carries the same information with `.azurewebsites.net` noise, and would have to
change if a custom domain were ever added.

### Localhost records a key but cannot verify it

The local Functions host does not enforce `AuthorizationLevel.Function`. Verified 2026-09-16: a request
to a locally running gateway carrying the key `total-nonsense` was not rejected with 401 — it reached
the worker. Probing a loopback gateway would therefore report every string as verified, which is the
exact defect this change exists to remove, reintroduced in the environment where it is least likely to
be noticed.

The hash comparison in `GatewayAuthorizer` does still run locally, so a locally registered key is a
real shared secret between the caller and the local gateway — it is simply not an Azure-issued one.
Registration is therefore allowed against a loopback gateway, with the probe skipped and the result
recorded as unchecked.

Unchecked is not verified, and must not be displayed as though it were. A developer working against a
local gateway is exactly the person most likely to trust the badge and carry that assumption into a
production registration.

## Risks / Trade-offs

- **The raw key is transmitted to the gateway.** → Over HTTPS, to an app in the same subscription, in a
  request that is refused before it does anything. Worth stating plainly rather than leaving implicit:
  this design sends the key to an endpoint the consumer itself would use.
- **Cold start looks like unreachable.** Flex Consumption with `alwaysReady http=1` should answer
  quickly, but a scaled-to-zero app will not. → Timeout must be generous, and a timeout is *unknown*,
  never *rejected*.
- **Probe traffic is indistinguishable from an attack in telemetry.** A burst of 400s against
  `/data/probe` from the dashboard's egress could read as probing by an intruder. → Confirm during
  implementation whether the missing-consumer-id path feeds the `Gateway denied` query used for
  alerting; it returns a plain `Deny` rather than the logging `Denied(...)`, but that must be verified,
  not assumed.
- **New network dependency between two deployables.** The dashboard cannot register a key if it cannot
  reach the gateway. → Accepted: refusing is correct, and key registration is not on any hot path.
- **Verification is point-in-time.** A key deleted in Azure tomorrow still shows verified. → Show the
  date rather than a bare badge, so the claim is visibly about a moment.

## Migration Plan

1. Apply the additive column migration. Existing rows keep working; both new columns are null.
2. Add gateway URLs to the dashboard's configuration, including `deploy.bat.json`, which has no
   `gateway` block today.
3. Deploy the API, then the frontend.
4. Existing `eng-apps` and `eng-apps-dev` registrations are left alone by decision. They continue to
   work — the runtime path only reads the hash — and will show a recorded key against an unknown
   gateway application until someone chooses to re-register them.

Rollback is the API and frontend; the columns are additive and can be left in place.

## Open Questions

- Should a consumer whose key is registered but whose app is unknown be visually distinguished from one
  verified under this change? They are not equally trustworthy, and showing both as "Verified" repeats
  the mistake this change exists to fix.
- `deploy.bat.json` is deferred by decision, so BAT will report unable-to-check for every registration
  until its gateway URLs are supplied some other way. Whether that is acceptable depends on whether
  anyone registers a key in BAT before it is resolved.
