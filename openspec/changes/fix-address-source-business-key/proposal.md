**Ticket:** #5425

## Why

`SourceId` is EIM's business identifier for a message. It is what operators search for in the
dashboard, what `ReleaseSweep` reports per record (*"so a release can be reported per business
record"*), and what distinguishes the record from the delivery: `MessageRequest.DedupeKey` documents
that every change to one record shares a `SourceId`. For a customer address, that business
identifier is the address location id. The address delete route already uses it, and so does every
other routed entity.

Address creations and updates do not. `BusinessKeyResolver.EntityKeyFields` keys the entity as
`mserp_custpostaladdressv2entity`; the live entity is `mserp_wtc_custpostaladdressv2entity`. The
lookup has never matched, so `QueueDataEventsHandler` has always fallen through to:

```csharp
SourceId = businessKey ?? context.OperationId
```

Every address create/update message is therefore identified inside EIM by a per-operation GUID.
That covers the highest-volume route in the system: 77,699 of 78,090 message queue rows at the time
of migration 003. An operator searching the message queue by address location id finds nothing, and
cannot find a record's create, updates, and delete together.

The defect is invisible by construction. Four of the five routed entities are spelled correctly,
nothing cross-checks the handler's entity map against the resolver's, and the fallback is silent. A
missing entry degrades to an identifier that looks plausible.

## What Changes

- Correct the key to `mserp_wtc_custpostaladdressv2entity`, so address create and update messages
  carry `AddressLocationId` as their `SourceId`. This matches the address delete route
  (`mserp_wtc_custpostaladdressdeletedv2entity`) and the other routed entities.
- Keep the fallback to the operation id when a business key cannot be resolved, but **log a
  warning** naming the entity, the missing key field, and the fallback id used, so the case is
  visible in Application Insights instead of silent.
- Treat a blank or whitespace key value as missing. Today a blank value yields an empty `SourceId`,
  not the fallback.
- Make `GetBusinessKey` tolerate an event with no `Target` attributes. Without this, the correction
  would turn a `Target`-less address event into a handler `NullReferenceException`.
- Add an automated cross-check that every entity in `QueueDataEventsHandler.EntityRoutes` has a
  `BusinessKeyResolver` entry and vice versa, plus per-entity payload fixtures proving each key
  attribute resolves.

## Non-goals

- Backfilling historical `SOURCEID` values. The current data is disposable and may be truncated.
- Removing the operation-id fallback, or discarding messages whose key is missing (for example by an
  `EntityRequiredFields` entry for `mserp_wtc_custpostaladdressv2entity`). The user chose
  fallback-plus-warning.
- Any change to the Capture contract. The envelope's `SourceId` value changes for address
  creates/updates, but Capture does not read, store, or match on it (confirmed by the user
  2026-10-21).
- Any change to `DEDUPEKEY`, duplicate detection, hold, release, retry, or failure classification.
- Any change to the Capture → D365 address path (`CustomerAddress_To_D365FSC`). It builds D365 keys
  from envelope `Data` via `EntityKey`, not from `SourceId`.
- Moving `BusinessKeyResolver` out of the engine assembly, or merging it with `EntityRoutes`.
- Splitting message queue search into separate business-key and operation-id fields.
- Changing the simulation console's address scenarios.

## Capabilities

### New Capabilities
- `source-business-key`: resolving the business record key used as `SourceId` for a routed source
  entity, guaranteeing every routed entity has one, and making any fallback to a delivery-scoped
  identifier visible.

### Modified Capabilities
<!-- None. `customer-address-delete-forwarding` already requires the delete entity's source
     identifier to be the address location id and rejects a blank one pre-insert; unchanged.
     `message-ingest-dedupe` already states that the delivery key is not the business record key;
     unchanged. -->

## Impact

**Code**
- `ARM.Integration.EIM/Utilities/BusinessKeyResolver.cs`: corrected dictionary key;
  `EntityKeyFields` made `internal`; null-tolerant and blank-as-missing `GetBusinessKey`; new
  public `GetKeyField` so the handler can name the field in its warning.
- `ARM.Integration.EIM/ARM.Integration.EIM.csproj`: `InternalsVisibleTo` for
  `ARM.Integration.EIM.Tests` (the pattern `ARM.Integration.EIM.Customer` already uses).
- `ARM.Integration.EIM.Customer/QueueDataEventsHandler.cs`: `SourceId` resolution extracted into a
  testable method that logs the fallback warning; `EntityRoutes` comment corrected.
- `ARM.Integration.EIM.Tests`: registration cross-check, per-entity fixtures (the address fixture is
  built from the sanitized live sample in `samples/CustomerAddress.OnExternalCreated.json`), address
  `SourceId` tests, and fallback-warning tests.

**No schema change.** `MESSAGEQUEUETABLE.SOURCEID` is `NVARCHAR(150)` since migration 009, and
`SPINSERTMESSAGEQUEUE.@SOURCEID` matches; an address location id is nine characters.

**Operations**
- Address messages become searchable by location id in the dashboard from deployment onward.
- A new Warning log appears whenever any routed entity falls back to the operation id. The expected
  rate is near zero: the live create sample carries `mserp_addresslocationid`.
- Customer, contact, and party contact messages with a blank key value move from an empty
  `SourceId` to the operation id plus a warning. Messages with a populated key are unchanged.

**Not affected**
- Capture: it does not consume `SourceId`.
- Duplicate detection: `SqlDuplicateProbe` matches on `DEDUPEKEY` alone (migration 008).
- Hold, release, and retry: these key on status and route columns.
- D365 entity key validation (`EntityKey`, `MissingEntityKeyException`): Capture → D365 path only.
