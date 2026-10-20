## Why

When a customer postal address is deleted in D365 F&SC, Capture is never told. F&SC does not emit
`OnExternalDeleted` for addresses; it surfaces the deletion as an `OnExternalUpdated` event on a
*separate* entity, `mserp_wtc_custpostaladdressdeletedv2entity`, carrying
`mserp_wtc_isdeleted = 200000001`. That entity is absent from the handler's entity map, so every
address deletion is logged as an "unsupported entity" and silently completed. Capture's address
book accumulates addresses F&SC considers gone.

Routing the new entity naively would be worse than the status quo: action is derived from
`MessageName`, which on this payload reads `OnExternalUpdated`, so Capture would receive an
*upsert* of a dead address — the exact opposite of the intent, with green logs.

## What Changes

- Register `mserp_wtc_custpostaladdressdeletedv2entity` as a routable D365 → Capture source entity.
- Add a `CustomerAddressDelete_To_Capture` orchestration that emits `Action = "Delete"` as a
  **constant**, never derived from `MessageName`. The entity is delete-only, so the forced action is
  immune to F&SC later switching to `OnExternalCreated` or `OnExternalDeleted` on it.
- Reuse the existing address field mapping and `CaptureCustomerAddressMessageContract` unchanged.
  The envelope's `Entity` remains `"CustomerAddress"` — Capture sees one entity with three verbs.
- Guard on `mserp_wtc_isdeleted == 200000001` inside the orchestration. A non-delete value is
  skipped and logged at Warning, so the guard doubles as a sensor for whether F&SC ever reuses the
  entity for something else.
- Resolve `SourceId` from the business key `mserp_addresslocationid` rather than falling back to
  `OperationId`. Because the field is now load-bearing, a blank or missing `mserp_addresslocationid`
  is rejected rather than allowed to fall back silently — a delete with no key is not actionable.
- Seed the route enabled in all four environments.
- `CustomerAddress_To_Capture` is **not modified**. `mserp_wtc_custpostaladdressv2entity` never
  emits a delete, so the two routes are disjoint in the verb dimension and nothing in the existing
  New/Update path becomes conditional.

Not breaking. The new envelope shape is identical to what Capture already receives for addresses;
only the `Action` value is new.

## Capabilities

### New Capabilities
- `customer-address-delete-forwarding`: Detecting customer address deletions in D365 F&SC — which
  arrive as updates on a dedicated soft-delete entity rather than as delete events — and forwarding
  them to Capture as a `Delete` action on the `CustomerAddress` entity.

### Modified Capabilities
<!-- None. The route registry, hold/release, dedupe, and orchestration-extensibility capabilities
     all absorb this change through their existing requirements: a new route row and a new
     registered orchestration type is exactly the extension path they already specify. -->

## Impact

**Code**
- `ARM.Integration.EIM.Customer/QueueDataEventsHandler.cs` — one `EntityRoutes` entry. Omitting it
  is a silent drop, not an error.
- `ARM.Integration.EIM/Utilities/BusinessKeyResolver.cs` — one `EntityKeyFields` entry mapping the
  new entity to `mserp_addresslocationid`.
- `Wilson.Integration.Orchestrations/Orchestrations/CustomerAddressDelete_To_Capture.cs` — new.
- `Wilson.Integration.Orchestrations/Orchestrations/OrchestrationRegistry.cs` — one entry.
  `RouteRegistryValidator` fails startup if this is missed, so the failure is loud.
- `ARM.Integration.EIM/Orchestration/D365EventForwardOrchestration.cs` — a hook allowing a subclass
  to override the action derived from `MessageName`.

**Data**
- New migration seeding four `ROUTETABLE` rows (BAT, DEV, DEV2, TEST), enabled, `ONPAUSE = Hold`.
  `006_seed_routetable.sql` is journaled and already applied, so its parity tripwire
  (`@routes INT = 9`) does not re-run and needs no edit.
- `MESSAGEQUEUETABLE.SOURCEENTITY` must accommodate 42 characters —
  `mserp_wtc_custpostaladdressdeletedv2entity` is two longer than its sibling, and
  `003_backfill_truncated_sourceentity.sql` exists because this column truncated these names before.

**Dashboard**
- `ARM.Integration.Dashboard.Frontend/src/features/routes/api.ts` — map the new entity to the
  `address` stem, or pausing one address route stops warning about the other.

**Explicitly out of scope**
- `BusinessKeyResolver.EntityKeyFields` contains `mserp_custpostaladdressv2entity`, missing the
  `wtc_` prefix, so the *existing* address route's `SourceId` has always fallen back to
  `OperationId`. Correcting it would change the `SourceId` Capture receives for every New/Update
  address message, on a live contract that cannot be rolled back once sent. It is tracked separately
  as `fix-address-source-business-key`, which carries its own deploy gate.

  This change deliberately does the opposite for the *new* route: it uses the key from the outset,
  because a new route has no legacy consumer expectation to break. That makes the delete route a
  canary — if Capture handles a key-shaped `SourceId` here, the later correction to the New/Update
  route carries far less risk.
