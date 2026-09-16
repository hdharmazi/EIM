## Why

The dashboard accepts any string as a consumer's function key. It hashes whatever it is given, and the
consumer immediately reports as verified — so a typo, a stale key, or a key belonging to a different
gateway app all look like success. The mistake only surfaces later, as a `403 key does not match
consumer` on the consumer's first real request, with nothing at registration time to suggest anything
was wrong.

This was found the obvious way: an administrator pasted a random string and the dashboard accepted it.

The failure is closed, not open — Azure rejects an unissued key with 401 before the gateway ever runs,
so a bad registration cannot grant access. But it silently *revokes* access for that consumer, and the
word "verified" actively encourages trusting it.

## What Changes

- The dashboard probes a submitted key against the gateway before saving it, and refuses to store a key
  the gateway will not accept.
- The gateway application that accepted the key and the time it was verified are recorded, so a
  registration says what was checked rather than merely that a hash exists.
- A consumer shows the date its key was verified, so a point-in-time check is not mistaken for a live
  one.
- An unreachable gateway is reported as *unknown*, distinctly from *rejected*. A gateway outage must not
  tell an administrator that a good key is bad.
- A loopback gateway records the key without probing, because a local Functions host does not enforce
  keys and would accept anything. Such a registration is shown as unchecked, never as verified.
- The operations guide stops calling these "function-level keys". They are host keys, and the
  difference has a consequence worth recording.

Not in scope: re-probing keys on a schedule, and any change to how the gateway itself authorizes a
request at runtime.

Depends on `share-gateway-across-nonprod`. That change collapses the per-environment gateway apps into
one, which removes the need for this change to work out which of several applications a key belongs to.

## Capabilities

### New Capabilities

_None._ This tightens behaviour that `gateway-consumer-console` already owns.

### Modified Capabilities

- `gateway-consumer-console`: key registration gains verification against a live gateway, records the
  gateway app that accepted the key and the time it was verified, and distinguishes an unreachable
  gateway from a rejected key.
- `gateway-access-control`: adds the standing constraint that every HTTP function in a gateway app is
  reachable by every consumer's host key, so no HTTP function may be added to a gateway app without
  consumer authorization.

## Impact

- `ARM.Integration.Dashboard.API` — key registration endpoint gains an outbound HTTPS call; new
  configuration listing the gateway apps it may probe.
- `ARM.Integration.Dashboard.Frontend` — the key dialog reports verified, rejected and unreachable as
  three different outcomes; the consumer list shows the gateway app and verification date.
- `GATEWAYCONSUMER` — new columns for the verified gateway app and verification timestamp. Additive and
  nullable; existing rows keep working unverified-as-to-app.
- `Script/config/deploy.*.json` — `deploy.bat.json` has no `gateway` block today, and BAT is the only
  environment hosting the dashboard. The dashboard needs the gateway URLs regardless of whether a
  gateway is deployed alongside it.
- `Script/Docs/d365fsc-gateway.md` — host key terminology, the onboarding order, and the new standing
  constraint.
- **Sequencing**: the gateway capabilities are still deltas inside `add-d365fsc-passthrough-gateway`
  and have not been archived into `openspec/specs/`. That change should be archived before this one is
  applied, or the two sets of deltas will describe the same requirements in two places.

- **No change to the runtime authorization path.** The gateway continues to compare a SHA-256 hash and
  is not aware that verification happened. This is deliberate: verification is an administrative
  convenience, and making the request path depend on it would turn a dashboard outage into a gateway
  outage.
