## Context

`QueueDataEventsHandler` resolves a business key before inserting, then falls back:

```csharp
var businessKey = BusinessKeyResolver.GetBusinessKey(context.PrimaryEntityName, targetAttrs);
...
SourceId = businessKey ?? context.OperationId,
```

`GetBusinessKey` returns null in two indistinguishable cases: the entity is not registered, or it is
registered but the attribute is absent. Both degrade to `OperationId`. The fallback is what makes the
typo survivable, and therefore what makes it invisible.

Current state of the four routed entities:

| Routed by handler | Key in resolver | Resolves? |
|---|---|---|
| `mserp_custcustomerv3entity` | `mserp_customeraccount` | yes |
| `mserp_smmcontactpersonv2entity` | `mserp_contactpersonid` | yes |
| `mserp_wtc_custpostaladdressv2entity` | *(absent — resolver holds `mserp_custpostaladdressv2entity`)* | **no** |
| `mserp_dirpartycontactv3entity` | `mserp_electronicaddressid` | yes |

`mserp_electronicaddressid` was verified against `PartyContact_To_Capture.MapFields`, which reads it
with `force: true`, so it is a real attribute on that entity.

Three correct and one wrong, with no mechanism that could tell the difference, is the shape of a
defect that recurs. The correction is one character; the change is mostly about the contract it
alters and the check that stops it happening again.

## Goals / Non-Goals

**Goals:**
- Address messages carry `AddressLocationId` as their source identifier, matching the documented
  meaning of the field and matching the other three routes.
- A routed entity without a resolvable business key fails a test rather than degrading to a
  delivery-scoped fallback.

**Non-Goals:**
- Backfilling historical rows. The current data is disposable and may be truncated, so rewriting
  ~77,699 rows per period would buy nothing — and it could not retract anything already sent to
  Capture, which is where the only durable consequence lives.
- Removing the `?? context.OperationId` fallback. A fallback is still correct for an entity with no
  natural business key; the change makes its use deliberate rather than accidental.
- Any change to `DEDUPEKEY`, duplicate detection, or the hold, release and retry paths.

## Decisions

### 1. Correct the key rather than removing the fallback

The one-character fix restores the intended behaviour. Removing the fallback and failing hard on an
unresolvable key was considered and rejected: it converts a silent degradation into a dropped
message for any future entity that legitimately has no business key, which is a worse failure than
the one being fixed.

### 2. Cross-check the handler's routes against the resolver

A test asserting that every key in `QueueDataEventsHandler.EntityRoutes` appears in
`BusinessKeyResolver.EntityKeyFields` is what would have caught this on the day it was written.

The two dictionaries are edited in different files for different reasons and have no compile-time
relationship, so they drift independently — the same drift `RouteRegistryValidator` exists to prevent
between routes and orchestration registrations. This is that idea applied one layer down.

*Alternative considered:* deriving one dictionary from the other. Rejected — they carry genuinely
different information (destination queue versus key attribute) and an entity may legitimately lack a
business key. A test asserting correspondence is honest about the relationship; merging them is not.

### 3. Attribute presence is verified against payloads, not assumed

A registered key whose attribute never appears in the payload is indistinguishable at runtime from
an unregistered entity — both return null, both fall back. The cross-check catches absent
registrations; only a fixture-backed assertion catches a registration naming an attribute that does
not exist. Both are needed, which is why the three currently-working entities are re-verified rather
than trusted.

### 4. No backfill

The current data is disposable and may be truncated outright, so there is nothing to migrate. Even
if it were retained, a backfill could not retract what Capture has already received — it would buy
only local search consistency at the cost of a large chunked update.

This is worth stating explicitly because the instinct on seeing a corrected key is to repair the
rows it was wrong for. Here the rows do not matter; only the forward contract does.

## Risks / Trade-offs

**Capture may key on the GUID.** This is the one risk that matters. Capture has received a
per-operation GUID in `SourceId` for every address message ever sent. If it stores, correlates, or
deduplicates on that value, changing the shape mid-stream could orphan its correlation or create
duplicate records on its side. → Confirm with the Capture team before deploying to any shared
environment. Sequencing `add-customer-address-delete-route` first proves the key shape on a route
with no legacy consumer.

**Sent messages cannot be recalled.** There is no rollback for a message already on the queue.
Reverting the code stops future messages but does not undo delivered ones. → Treat this as a deploy
gate, not a deployment step; verify in a non-shared environment first.

**Mixed search results if the old rows are kept.** An operator searching by address location id
finds only post-change rows; searching by operation id finds only pre-change rows, with nothing
indicating why. → Only applies if the existing data is retained; truncating removes the condition
entirely.

**The cross-check may fail immediately on another entity.** If verification finds a second
mis-registered key, the blast radius widens beyond addresses. → Run the verification before
committing to the deploy gate, so the conversation with the Capture team covers every affected
entity at once rather than in sequence.

## Migration Plan

1. Add the cross-check and the attribute-presence assertions. If either fails for an entity other
   than the address one, stop and reassess scope before proceeding.
2. Correct the key.
3. Confirm with the Capture team that a key-shaped `SourceId` is accepted on the `CustomerAddress`
   envelope. Blocks every subsequent step.
4. Deploy to DEV2. Trigger an address create and an address update, and confirm both the persisted
   `SOURCEID` and the forwarded envelope carry the address location id.
5. Promote only after Capture confirms the change was absorbed cleanly.

**Rollback:** redeploy the previous host. Future messages revert to GUIDs; messages already sent
cannot be recalled, which is why step 3 gates the rest.

## Open Questions

- Does Capture read `SourceId` at all on the `CustomerAddress` envelope, and if so, does it store or
  correlate on it? Determines whether this is a routine correction or a coordinated change.
- Should the message queue search treat a business key and an operation id as distinct search
  fields, given both currently occupy `SOURCEID`? Out of scope here, but the mixed population makes
  the question visible.
- Do the other three entity keys resolve against live payloads in every environment, or only in the
  ones spot-checked? The cross-check proves registration, not population.
