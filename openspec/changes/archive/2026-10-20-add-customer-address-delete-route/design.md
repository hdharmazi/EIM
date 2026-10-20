## Context

D365 F&SC does not emit a delete event for customer postal addresses. Deletions surface as an
`OnExternalUpdated` event on a dedicated soft-delete entity,
`mserp_wtc_custpostaladdressdeletedv2entity`, carrying `mserp_wtc_isdeleted = 200000001`
(an `OptionSetValue`; `200000000 = No`, `200000001 = Yes` — the same option set the existing
`ResolveYesNo` helper already decodes).

The two entities are disjoint in the verb dimension, confirmed with the F&SC side:

```
  create ──┐
  update ──┴──▶  mserp_wtc_custpostaladdressv2entity         ──▶ New / Update
  delete ─────▶  mserp_wtc_custpostaladdressdeletedv2entity  ──▶ Delete
```

Because `v2entity` never emits a delete, there is no interleaving between the two routes, no
ordering race, and nothing in the existing New/Update path has to become conditional.

Current behaviour: `QueueDataEventsHandler.EntityRoutes` has no entry for the delete entity, so the
message is logged as an unsupported entity and completed. Every address deletion is silently
dropped.

The payload was traced end to end against the existing mapping. All eleven fields
`CustomerAddress_To_Capture.MapFields` reads are present on the delete entity, including
`mserp_isprimary` as an `OptionSetValue`, and `context.OperationCreatedOn` already produces the
legacy `/Date(...)/` timestamp Capture expects. **The mapping requires no changes.** The entire
change is routing plus a forced action.

## Goals / Non-Goals

**Goals:**
- Forward F&SC address deletions to Capture as `Action = "Delete"` on `Entity = "CustomerAddress"`.
- Make the delete action a constant that cannot be corrupted by `MessageName`.
- Keep `CustomerAddress_To_Capture` byte-for-byte unchanged.
- Identify the deleted address by its business key, `mserp_addresslocationid`.
- Give the delete route its own route row, so it can be paused, measured, and held independently of
  the New/Update route that carries ~77,699 rows per period.

**Non-Goals:**
- Correcting the missing `wtc_` prefix on the *existing* address entry in
  `BusinessKeyResolver.EntityKeyFields`. That is breaking and is tracked separately as
  `fix-address-source-business-key`. See Risks.
- Generalising soft-delete detection to Contact, PartyContact, or Customer. If F&SC applies the same
  `*deletedv2entity` convention to those entities later, the shape here will need revisiting; that
  is not a reason to build the abstraction on a single instance now.
- Any change to `CaptureCustomerAddressMessageContract` or the envelope shape.

## Decisions

### 1. A separate orchestration, not an entity-aware branch in the existing one

`CustomerAddressDelete_To_Capture` is a new type registered against a new route row.

*Alternatives considered:*

| Option | Why not |
|---|---|
| Teach `CustomerAddress_To_Capture` both entity names | Couples a class carrying 77k rows/period to a second source entity. Any edit to the delete path is an edit to the highest-traffic path in the system. Operators lose the ability to pause deletes without pausing upserts. |
| A `FORCEDACTION` column on `ROUTETABLE` | Data-driven and operator-visible, but action resolution becomes invisible in code, `RouteRegistryValidator` cannot check it, and it is a schema change to solve a one-entity problem. |

Separation also buys a rehearsal path: the route can be seeded disabled in a single environment,
the held rows inspected in the dashboard, and then enabled — without touching the upsert route.

### 2. The action is a constant, not derived from `MessageName`

`D365EventForwardOrchestration.ParseAndMap` derives the action from `context.MessageName`. On this
payload that field reads `OnExternalUpdated`, so the default derivation yields `"Update"` — Capture
would receive an *upsert of a dead address*, with no error anywhere. This is the most dangerous
failure mode in the change because it is semantically wrong and operationally silent.

Add to `D365EventForwardOrchestration`:

```csharp
protected virtual string? ForcedAction => null;
```

applied immediately after the `MessageName` switch and before `MapFields` is invoked.
`CustomerAddressDelete_To_Capture` returns `"Delete"`.

The value of a forced action over inspecting the entity name inside the switch: it is **immune to
`MessageName` entirely**. Whether F&SC sends `OnExternalCreated` on first flag, `OnExternalUpdated`
as observed, or switches to `OnExternalDeleted` later, all three land on `"Delete"`. No further
investigation of F&SC's messaging behaviour on this entity is required for correctness.

A second effect is load-bearing: the change-tracking closure only suppresses unchanged fields when
`actionString == "Update"`. Forcing `"Delete"` makes that branch dead, so every mapped field flows
and `ChangedFields` becomes irrelevant. The delete payload stops depending on a list F&SC controls.

### 3. Direct subclass of `CustomerAddress_To_Capture`

```csharp
class CustomerAddressDelete_To_Capture : CustomerAddress_To_Capture
{
    protected override string? ForcedAction => "Delete";
}
```

`EntityName` stays `"CustomerAddress"`, `MapFields`, `ResolveYesNo`, and `CreateEnvelope` are
inherited verbatim.

*Alternative considered:* a shared abstract base with the two orchestrations as peers. That models
the relationship more honestly — a delete orchestration is not "a kind of" upsert orchestration —
but it is a three-level hierarchy to justify one overridden string. Since the mapping is driven by
the *Capture* contract rather than by either D365 entity, a change to it should propagate to both,
which is exactly what inheritance gives. Promote to a shared base when F&SC's two entities diverge
in attribute set, or when a second entity needs the same delete treatment.

The assumption — *both D365 entities expose the same attribute set* — was verified against a live
payload and is the one thing that would force the promotion. Worth a one-line comment on the class.

### 4. `SourceId` is the business key, resolved in the handler

Register in `BusinessKeyResolver.EntityKeyFields`:

```
["mserp_wtc_custpostaladdressdeletedv2entity"] = "mserp_addresslocationid"
```

One registration serves both consumers: `QueueDataEventsHandler` sets
`MessageRequest.SourceId` from it before the DB insert, and `BaseForwardOrchestration.GetSourceId`
reads it back for the Capture envelope.

This deliberately diverges from the existing address route, which emits an `OperationId` GUID
because of the missing `wtc_` prefix. A new route has no legacy consumer expectation, so it is the
safe place to establish key-shaped `SourceId` — and it de-risks the later correction of the
New/Update route by proving Capture accepts the key first.

### 5. Two guards, at two layers, for two different reasons

The layer is forced by ordering, not preference:

```
  QueueDataEventsHandler
    ├─ EntityRequiredFields["…deletedv2entity"] = "mserp_addresslocationid"
    │     ▲ MUST be here — BusinessKeyResolver runs in the handler, so a blank
    │       key has already silently degraded SourceId to OperationId by the
    │       time any orchestration code runs. A delete with no key is not
    │       actionable by Capture.
    │
    └─ EIMController.InsertAndProcessAsync
          └─ CustomerAddressDelete_To_Capture
               └─ mserp_wtc_isdeleted == 200000001
                     ▲ belongs here — ResolveYesNo already decodes this option
                       set, and a row plus step log makes the anomaly visible
                       in the dashboard rather than vanishing pre-insert.
```

`EntityRequiredFields` is `Dictionary<string, string>` — one field per entity — which is exactly
what is needed. No shape change.

The `isdeleted` guard is not doubt about the entity's contract; it is a **sensor**. The error costs
are asymmetric: a wrongly emitted `Delete` removes a live address and needs a human, whereas a
missed delete leaves a stale row and is replayable. A cheap tripwire on the destructive side turns
"can this flag ever be `No`?" from a question for a meeting into a Warning in telemetry.

### 6. `ParseAndMap` gains a `skipReason`

The guard needs to abort the forward *and* be distinguishable from an ordinary no-op.
`BaseForwardOrchestration` already has one abort channel — `hasTrackedChanges = false`, which
completes the message with the result `"not tracked fields"` at Information. Reusing it would file
an anomaly under a reassuring label, which is the failure mode this change exists to avoid.

Widen the tuple to
`(string action, bool hasTrackedChanges, TDest mappedData, string? sourceTimestamp, string? skipReason)`.
A non-null `skipReason` means *do not forward, and log at Warning via `stepLog.LogWarning`*, carrying
the observed value. `hasTrackedChanges = false` keeps its existing Information-level meaning.

Two skip channels is mild redundancy, accepted because the severity difference is the entire point:
"expected no-op" and "unexpected shape" should not read the same in the dashboard. Only two types
implement `ParseAndMap` — `D365EventForwardOrchestration` and `SamplePoll_To_Target` — so the
widening is a two-file mechanical change and every existing caller returns `null`.

### 7. Seeded enabled, with no tracked-field entry

Seeded `ENABLED = 1, ONPAUSE = 'Hold'` in BAT, DEV, DEV2, TEST, matching every other route.

No entry is added to `TrackedFieldsByEntity`. That gate only fires on `OnExternalUpdated`, which is
precisely what these deletes are, so registering it would create a way to silently drop deletes on
a delete-only entity while buying nothing.

## Risks / Trade-offs

**A route row seeded before the code is deployed takes down the running host.**
`RouteRegistryValidator` throws at startup when any route names an unregistered orchestration, so a
`CustomerAddressDelete_To_Capture` row present while the old host restarts is an outage, not a
degraded route. → Deploy the Functions host **before** running the migration in each environment.
Migrations are run manually via `ARM.Integration.DbMigrator`, not from the pipeline, so ordering is
under human control — and must be stated in the runbook rather than assumed.

**A typo in the entity name fails in two very different ways.**
```
  ROUTETABLE typo, dictionary correct  →  RouteNotFoundException → dead letter   (loud)
  dictionary typo                      →  "unsupported entity" → complete        (SILENT)
```
→ Copy the name from a captured live payload; never retype it. A test asserting the handler's
`EntityRoutes` key matches the migration's `SOURCEENTITY` value closes the gap permanently.

**The name is 42 characters and this column has truncated before.**
`003_backfill_truncated_sourceentity.sql` exists because `SOURCEENTITY` silently shortened these
names, producing rows the release sweep could not match. The new name is two characters longer than
its sibling. → Verify the current column width before seeding; assert a round-trip in test.

**Divergent `SourceId` semantics between the two address routes.**
Deletes carry `000146089`; New/Update carry an `OperationId` GUID. Capture sees the same logical
record identified two ways depending on the verb. → Accepted deliberately: the delete route is the
low-risk place to prove the key works, and it converges when `fix-address-source-business-key`
ships. Confirm with the Capture team that a key-shaped `SourceId` is accepted on the delete message
before enabling in a shared environment — sent messages cannot be recalled.

**Inherited mapping drift.** A field added to `CustomerAddress_To_Capture.MapFields` for an
attribute that exists only on `v2entity` would map to null on deletes, silently. → Low likelihood
while the mapping is driven by the Capture contract; the mitigation is the promotion to a shared
base described in Decision 3, not pre-emptive structure.

**Pausing one address route and not the other.** Deletes flowing while upserts are held is
tolerable; upserts flowing while deletes are held leaves Capture holding addresses F&SC considers
gone. → The frontend's `dependentRoutes` warning already covers this, provided the new entity is
added to the `entityStem` map with the `address` stem. Omitting that entry does not break routing —
it silently removes an operator safeguard, which makes it easy to miss in review.

## Migration Plan

Per environment, in order:

1. Deploy the Functions host carrying `CustomerAddressDelete_To_Capture` and its registry entry.
   Startup validation passes because no route names it yet.
2. Run `ARM.Integration.DbMigrator` to apply the new seed migration. `006_seed_routetable.sql` is
   journaled in `dbo.SchemaVersions` and already applied, so its parity tripwire
   (`@routes INT = 9`) does not re-run and requires no edit. On a fresh database 006 still runs
   first and still sees exactly nine routes.
3. Confirm four rows landed — one per environment — via the new migration's own parity check.
   Silent under-seeding is how a delete dead-letters in BAT only.
4. Trigger a real address deletion in DEV2 and verify the Capture envelope: `Action = "Delete"`,
   `Entity = "CustomerAddress"`, `SourceId` equal to the address location id.

**Rollback:** set `ENABLED = 0` on the four route rows. The route resolver's cache is invalidated on
operator change, so this takes effect without a redeploy and without waiting for the TTL. Messages
already sent to Capture cannot be recalled, which is why step 4 precedes enabling anywhere shared.

## Open Questions

- Does Capture accept a key-shaped `SourceId` on the delete message, or does it expect the GUID
  shape it receives today for addresses? Blocks enabling in any shared environment.
- Does Capture require the full address body on a `Delete`, or only
  `DataAreaId` + `CustomerAccount` + `AddressLocationId`? Sending everything costs nothing and the
  payload carries it, but fields Capture ignores are fields that can drift unnoticed.
- Is `mserp_wtc_isdeleted` ever `200000000` on this entity — an undelete or a reversed soft delete?
  The Decision 5 guard is designed to answer this from telemetry rather than block on it. If it does
  occur, the meaning of a flag flipping back to `No` becomes a new design question.
- Will F&SC apply the same `*deletedv2entity` convention to Contact, PartyContact, or Customer? An
  affirmative answer converts Decision 3 from a subclass into a shared base or a declarative rule.
