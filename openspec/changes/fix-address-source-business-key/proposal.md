## Why

`BusinessKeyResolver.EntityKeyFields` keys the customer address entity as
`mserp_custpostaladdressv2entity`. The live entity is `mserp_wtc_custpostaladdressv2entity`. The
lookup has therefore never matched, `GetBusinessKey` has always returned null for addresses, and
`QueueDataEventsHandler` has always fallen through to its fallback:

```csharp
SourceId = businessKey ?? context.OperationId
```

So every customer address message — the highest-volume route in the system, around 77,699 rows per
period — is persisted and forwarded to Capture identified by a per-operation GUID instead of by
`AddressLocationId`.

This contradicts what the codebase says the field is for. `MessageRequest.DedupeKey` documents the
distinction explicitly: *"Not the business record key: every change to one record shares a SourceId"*.
`ReleaseSweep` carries `SourceId` *"so a release can be reported per business record"* — a line that
produces a meaningless log entry for addresses. An operator searching the message queue by address
location id finds nothing.

The defect is invisible by construction. Three of the four routed entities happen to be spelled
correctly; the fourth is not; and nothing cross-checks the handler's entity map against the
resolver's. A missing entry degrades silently to a fallback that looks plausible.

## What Changes

- **BREAKING**: correct the key to `mserp_wtc_custpostaladdressv2entity`, so address messages carry
  `AddressLocationId` as their source identifier. The `SourceId` Capture receives on the
  `CustomerAddress` envelope changes from a GUID to the address location id.
- Add a cross-check asserting that every entity routed by `QueueDataEventsHandler` has a
  `BusinessKeyResolver` entry, so an absent or misspelled key fails a test instead of degrading
  silently.
- Verify the remaining three entity keys resolve against live payloads rather than assuming the
  spelling is right because the lookup does not throw.

Existing rows are not backfilled. The current data is disposable and may be truncated, so the
historical GUID identifiers are not worth rewriting.

## Capabilities

### New Capabilities
- `source-business-key`: resolving the business record key for a routed source entity, and the
  guarantee that a routed entity always has one rather than silently falling back to a
  delivery-scoped identifier.

### Modified Capabilities
<!-- None. No existing requirement states what SourceId holds for a D365-sourced message. -->

## Impact

**Code**
- `ARM.Integration.EIM/Utilities/BusinessKeyResolver.cs` — one corrected dictionary key.
- `ARM.Integration.EIM.Tests` — cross-check between `QueueDataEventsHandler.EntityRoutes` and
  `BusinessKeyResolver.EntityKeyFields`.

**Live contract — this is the whole risk**

`SourceId` is not merely persisted; it is serialized into the `CaptureMessageEnvelope` sent to
Capture. After this change Capture receives `000146087` where it previously received
`3a4feed5-c6b5-f111-aaac-000d3a18831d`, on every address create and update. Sent messages cannot be
recalled. This must be confirmed with the Capture team before deploying anywhere shared — the same
class of deploy gate as the `Status = "Success"` response-envelope change.

**Not affected**
- Duplicate detection. Dedupe moved to `DEDUPEKEY` in migration 008 and `SqlDuplicateProbe` matches
  on that column alone. `SourceId` plays no part.
- Column width. `MESSAGEQUEUETABLE.SOURCEID` is `NVARCHAR(150)` since migration 009; an address
  location id is nine characters.
- Hold, release, and retry. All key on status and route columns, not `SourceId`.

**Relationship to `add-customer-address-delete-route`**

That change registers the new soft-delete entity with `mserp_addresslocationid` from the outset,
because a new route has no legacy consumer to break. It is therefore the low-risk proving ground for
a key-shaped `SourceId`: if Capture accepts it there, this change becomes substantially safer.
Sequencing that change first is recommended but not required.
