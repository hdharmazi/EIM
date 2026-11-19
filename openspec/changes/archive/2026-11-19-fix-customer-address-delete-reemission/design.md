## Context

### Observed behaviour (BAT, eimdb, 2026-10-26)

Every message on `mserp_wtc_custpostaladdressdeletedv2entity` since the BAT delete route was enabled:

| IDs | Count | Trigger (same second) | PreImage `mserp_wtc_isdeleted` | `ChangedFields` | Genuine? |
|---|---|---|---|---|---|
| 78800 | 1 | address `000128172` deleted | *(PreImage null)* | every non-empty field, **including `mserp_wtc_isdeleted`** | yes |
| 78808–78825 | 18 | customer `100300` update (78826) | `200000001` (Yes) | `addressstreet, addressvalidfrom, addressvalidto, formatteddeliveryaddress` | no |
| 78827/28/30/31 | 4 | address edit on `100300` (78829/78832) | Yes | same four | no |
| 78833–78850 | 18 | customer `100300` name update (78851) | Yes | same four | no |

All 41 have `MessageName = OnExternalUpdated` and `STATUS = 2` (Completed), so all were forwarded to Capture
as `Action = "Delete"`. The 40 noise events concern two locations, `000311920` and `000311720`. Both were
edited before deletion, so each has two expired `LogisticsPostalAddress` versions. Target and PreImage
always show the two different versions. For example, 78833 has Target street `222 Main street No. 2` with
`ValidFrom` 1792601588000, and PreImage street `222 Main street` with `ValidFrom` 1792601469000.
Both carry the same Dataverse id `00010be9-0000-0000-ec97-035001000000`.

App Insights (`appins-eim-automation-wtc-bat`) for 19:32:00–19:33:30 shows exactly 19 data events: 18 on
the soft-delete entity and 1 on `mserp_custcustomerv3entity`. There were **none** on
`mserp_wtc_custpostaladdressv2entity`. That entity joins the same `DirPartyLocation → DirPartyTable →
CustTable` graph, and customer `100300` has an active address (`000312031`) on it. So a customer save
does not re-notify an entity row whose values are unchanged. The noise exists only because the
soft-delete record's values change from one read to the next.

Of all 106 stored D365 `OnExternalUpdated` events across the five routed entities, 106 carry a
`ChangedFields` parameter.

### Why D365 re-emits a deleted address

Microsoft's data events documentation says: *"Modify data events are triggered by update operations on the
underlying tables of an entity."* This covers joined tables, not only the root.

`C:\Custom\Metadata\WTC\WTC\AxDataEntityView\WTC_CustPostalAddressDeletedV2Entity.xml`:

- The root data source is `LogisticsLocation` with range `WTC_IsDeleted = 1`. The Dataverse id is derived
  from the root, and the entity key is `CustomerAccount + AddressLocationId`.
- The embedded `LogisticsPostalAddress` (relation `LogisticsLocation.RecId == Location`) has range
  `ValidTo < 2154-12-31 23:59:59`, so it matches **every expired version**.
- By contrast, `WTC_CustPostalAddressV2Entity` uses `ValidTo = 2154-12-31 23:59:59`, which is exactly one
  current version per location.

A deleted location with N versions is therefore N entity rows behind one Dataverse record. When an
underlying table changes for that customer, F&SC notifies Dataverse. Dataverse re-reads the record, gets
one of the N rows, compares it with its last image, and raises `OnExternalUpdated` with the four version
fields as `ChangedFields`.

How a deletion is recorded today (`WTC_LogisticsPostalAddressTable_Extension.deactivate`, CoC on
`LogisticsPostalAddress`): standard `deactivate`
(`XppSource/Directory/AxTable_LogisticsPostalAddress.xpp`, line 109) expires the **current version in
place**: `validTimeStateUpdateMode(ValidTimeStateUpdate::Correction)`, `ValidTo = now − 2 s`, `update()`.
Its `RecId` doesn't change. The WTC extension then sets `LogisticsLocation.WTC_IsDeleted = Yes`.
`WTC_IsDeleted` is never cleared anywhere in the WTC model.

### Current integration flow

```
Service Bus d365fsc-dataevent
  → QueueDataEventsHandler → DataEventProcessor.ProcessAsync          (Wilson.Integration.Automation/Outbound)
      EntityRoutes[mserp_wtc_custpostaladdressdeletedv2entity] = CustomerAddressDelete_To_Capture
      TrackedFieldsByEntity: no entry for this entity  ← every OnExternalUpdated passes
      EntityRequiredFields: mserp_addresslocationid
  → EIMController.InsertAndProcessAsync + IngestGate (DedupeKey = OperationId: each re-emission is a new operation)
  → CustomerAddressDelete_To_Capture: GetSkipReason checks Target mserp_wtc_isdeleted == Yes (always true) → ForcedAction "Delete"
  → Service Bus d365fsc-to-capture
```

## Goals / Non-Goals

**Goals:**
- D365 exposes one stable row per deleted address, so related customer edits stop producing events on the
  soft-delete entity. This is the root-cause fix.
- An address deletion is forwarded to Capture exactly once, when it happens.
- The integration never forwards a re-notification of an already-deleted address, whatever D365 sends.
  This is the safety net, and it is effective before the D365 package lands.

**Non-Goals:** see the proposal.

## Decisions: D365 F&SC (WTC model)

### D1. Record which version was deleted: `LogisticsLocation.WTC_DeletedPostalAddress`

In `Metadata/WTC/WTC/AxTableExtension/LogisticsLocation.WTC.xml`, add an `AxTableFieldInt64` field
`WTC_DeletedPostalAddress` with `ExtendedDataType = LogisticsPostalAddressRecId` (a standard EDT, used by
`SalesLine.DeliveryPostalAddress`) and a label. No index is needed, because the entity joins from the
location row to the address by `RecId`, which is the clustered key.

In `WTC_LogisticsPostalAddressTable_Extension.deactivate`, in the block that already sets `WTC_IsDeleted`:

```xpp
logisticsLocation.WTC_IsDeleted            = NoYes::Yes;
logisticsLocation.WTC_DeletedPostalAddress = this.RecId;   // version just expired in place by next deactivate()
logisticsLocation.update();
```

The block keeps its `WTC_IsDeleted != Yes` guard, so the first deactivation wins, and both fields are set
in one `update()` and one transaction. As a result, the location enters the entity once with one row, which
produces exactly one data event, as today for 78800.

### D2. Join the soft-delete entity on that version only

In `WTC_CustPostalAddressDeletedV2Entity`, the `LogisticsPostalAddress` data source's relation is
**replaced**. The existing `RecId → Location` relation (`QueryDataSourceRelation1`) is removed and this
relation takes its place:

```xml
<AxQuerySimpleDataSourceRelation>
  <Name>QueryDataSourceRelation2</Name>
  <Field>WTC_DeletedPostalAddress</Field>
  <JoinDataSource>LogisticsLocation</JoinDataSource>
  <RelatedField>RecId</RelatedField>
</AxQuerySimpleDataSourceRelation>
```

*Why replace and not add (build feedback 2026-10-26):* with both relations, the entity fails to compile:
`…/QueryDataSourceRelation2/Direction: Both Incoming and Outgoing are inferred from relations between
LogisticsLocation and LogisticsPostalAddress`. Relation 1 says the address references the location, and
relation 2 says the location references the address. An entity's data-source relations must agree on
direction, because the direction sets the write order.

Relation 1 is redundant once relation 2 exists. The pointer is always a version of the same location:
`deactivate` takes it from `this`, whose `Location` is the flagged location, and the backfill filters on
`Location == location.RecId`. The join also gets cheaper, because it is now on the address's clustered
`RecId`. The reversed direction only changes the write order of this entity's copied insert/update
methods, and nothing writes to the soft-delete entity.

The `ValidTo < 2154-12-31` range stays as a guard, so a pointer to a still-active version never surfaces.
Field list, key, public names, and the Dataverse id (root is still `LogisticsLocation`) are unchanged, so
no virtual-entity refresh is needed and the integration payload is identical.

*Alternatives rejected*

- **Latest version via an aggregate `AxView`** (`GROUP BY Location, MAX(ValidTo)`) joined into the entity:
  no new field and no backfill, but it puts a view inside a change-tracked entity. Microsoft documents
  that data events don't fire for view-*rooted* entities, and the behaviour for a joined view is
  unverified. It also adds an aggregate to every read of the entity.
- **Range expression comparing two `LogisticsPostalAddress` aliases** (`NotExists` a newer version):
  entity-view relations are equality-only, and cross-data-source range expressions in entity views are
  fragile to build and sync.
- **Root the entity on `LogisticsPostalAddress`** (one record per version): each version becomes its own
  Dataverse record. A deletion would then raise one "delete" per version, which multiplies the problem
  instead of removing it.
- **Set `ChangeTrackingEnabled = No` on the non-root data sources** (`DirPartyLocation`, `DirPartyTable`,
  `CustTable`, country): this is cheap and probably stops the customer-edit re-checks. However, the entity
  still has N rows per deleted location under one id and key, so any re-read can still flip versions.
  It is also not documented that data events honour the per-data-source flag. The user chose the pointer
  fix on 2026-10-26.
- **Integration-only fix**: it stops the duplicate `Delete`s, but D365, Dataverse, and Service Bus would
  still carry about 9 useless events per deleted-and-edited address on every customer save, and every
  future consumer of the entity would inherit the noise.

### D3. Backfill locations deleted before the release

A location with `WTC_IsDeleted = Yes` and `WTC_DeletedPostalAddress = 0` would drop out of the entity once
D2 is deployed, because the inner join fails. Removing rows through a view change raises no data event, so
Capture is unaffected. However, the entity would no longer list those deletions, which consumers may still
read through OData.

Add a runnable class `WTC_CustPostalAddressDeletedBackfill` (`main`, net-new `WTC_` class) that runs once
per environment after deployment:

```xpp
ttsbegin;
while select forUpdate location
    where location.WTC_IsDeleted == NoYes::Yes
       && location.WTC_DeletedPostalAddress == 0
{
    select firstonly validTimeState(DateTimeUtil::minValue(), DateTimeUtil::maxValue()) RecId from postal
        order by ValidTo desc
        where postal.Location == location.RecId;
    if (postal.RecId)
    {
        location.WTC_DeletedPostalAddress = postal.RecId;
        location.update();
    }
}
ttscommit;
```

It is idempotent because it only touches rows still at `0`. It writes an info line with the count. Volume
is small, because the flag only exists since #4611.

Each backfilled location is updated on the entity's root table, so expect **at most one** final
`OnExternalUpdated` per already-deleted location. Dataverse's last image may be the older version, so the
event shows the version fields. That event doesn't list `mserp_wtc_isdeleted`, so the integration safety
net (D5) drops it. For that reason the integration ships first (Migration Plan).

### D4. Verification queries (DEV, after DB sync)

Against the entity view `WTC_CUSTPOSTALADDRESSDELETEDV2ENTITY`, every `(CUSTOMERACCOUNT, ADDRESSLOCATIONID)`
must have `COUNT(*) = 1`. Run this before and after the backfill. This also reveals the other possible
multiplier, a location linked to more than one `DirPartyLocation`. If any duplicates remain for that
reason, record them on the ticket. They are covered by the integration safety net and are out of scope here.

## Decisions: integration (safety net)

### D5. Track only `mserp_wtc_isdeleted` on the soft-delete entity

Add to `DataEventProcessor.TrackedFieldsByEntity`:

```csharp
// Soft-delete rows only ever have the flag set (WTC_IsDeleted is one-way). The flag is among the
// changed fields only when the location first enters the entity, i.e. the deletion itself.
// Later updates are D365 re-notifications (address version flips, backfill touches) and must
// not be forwarded as further deletes.
["mserp_wtc_custpostaladdressdeletedv2entity"] = new(StringComparer.OrdinalIgnoreCase)
{
    "mserp_wtc_isdeleted",
},
```

The existing rule then applies: `MessageName == "OnExternalUpdated"` and no tracked field among
`ChangedFields` means the message is completed and not inserted. 78800 passes and the 40 noise events are
dropped. Events with any other message name are not filtered and are still forwarded as `Delete`.

Why keep this when D365 is fixed: BAT is live now and the D365 release cadence is slower; the backfill
(D3) causes one re-notification per old deletion; and the integration shouldn't rely on the entity's join
shape staying single-row (for example, the `DirPartyLocation` multiplier in D4).

*Alternatives rejected*: a PreImage rule (equivalent, but needs new engine parsing); skipping in
`CustomerAddressDelete_To_Capture.GetSkipReason` (inserts and shows about 9 *Skipped* rows per customer
save); dedupe by `SourceId` (changes the `message-ingest-dedupe` contract and depends on shared-`eimdb`
history).

### D6. Extract the check into a testable helper

```csharp
/// <summary>
/// True when the event should continue: it is not an OnExternalUpdated, its entity has no tracked
/// fields, or at least one tracked field is among its ChangedFields.
/// </summary>
internal static bool HasTrackedChange(EventContext context, out IReadOnlyList<string> changedFields)
```

`ProcessAsync` calls it in place of the inline block, in the same position. The skip log becomes
`Message {id} has no tracked field changes, skipping. Entity={entity}, Event={eventName}, ChangedFields=[{changedFields}]`
at `Information`.

### D7. Missing `ChangedFields` parameter

`D365FSCEventParser.GetChangedFields` dereferences `FirstOrDefault(...)` without a null check.
`InputParameter` is a class, so a missing parameter throws `NullReferenceException` before the `?? []` in
`ProcessAsync` runs, and Service Bus then retries and dead-letters the message. Make it return an empty
list when the parameter is absent or JSON null. `HasTrackedChange` then reports no tracked change, and the
skip is logged at **Warning** when the parameter itself was absent. Trade-off: a genuine deletion without
`ChangedFields` (0 of 106 observed) would be dropped with a warning rather than dead-lettered.
`GetChangeAttributes` is not touched. This applies to every entity in `TrackedFieldsByEntity`
(`custcustomerv3`, `smmcontactpersonv2`, `wtc_custpostaladdressv2entity` and the soft-delete entity).
For each of them, an `OnExternalUpdated` without a usable `ChangedFields` array used to throw, retry
and dead-letter. It is now completed with a warning instead.

The parser is also used by the orchestration step (`D365EventForwardOrchestration.ParseAndMap`), for
every entity, including those without a tracked set such as `mserp_dirpartycontactv3entity`. There, an
`OnExternalUpdated` without `ChangedFields` used to throw a `NullReferenceException`. It is now completed
as "not tracked fields", and the same applies when the dashboard reprocesses such a message. This is
intended: a missing list is an anomaly to log, not a reason to retry (found in review, Bowser round 1).

Interaction with the existing anomaly check (*Only events flagged as deleted are forwarded*): that check
only sees events that pass this ingress filter. A soft-delete update whose `ChangedFields` lacks
`mserp_wtc_isdeleted` is now withheld at Information as a routine skip, even if its flag is not yes. The
anomaly warning still fires whenever the flag itself changes.

### D8. Failure behaviour, idempotency, ordering

- A dropped re-emission is completed on Service Bus. No message row, no retry, no dead-letter.
- No new exception types, so `DefaultFailureClassifier` is unchanged.
- A genuine deletion follows the same path as today (insert, `IngestGate`, `Delete`). `DedupeKey` and
  `SourceId` are unchanged.
- Ordering: the deletion is the first event for a location on this entity, and only re-notifications
  follow it.
- D365 side: D1 adds no transaction, because the field is set in the existing `update()`. The backfill is
  idempotent and safe to rerun.
- Reprocessing from the dashboard bypasses `DataEventProcessor`, so stored historical re-emissions still
  forward `Delete` if reprocessed. This is out of scope.

## Payload shapes

The payload is unchanged by the D365 part. Accepted (genuine deletion, 78800, abbreviated):

```json
{ "MessageName": "OnExternalUpdated", "PrimaryEntityName": "mserp_wtc_custpostaladdressdeletedv2entity",
  "InputParameters": [
    { "key": "Target", "value": { "Attributes": [ …, { "key": "mserp_wtc_isdeleted", "value": { "Value": 200000001 } } ] } },
    { "key": "PreImage", "value": null },
    { "key": "ChangedFields", "value": [ "mserp_customeraccount", …, "mserp_wtc_isdeleted" ] } ] }
```

Dropped (re-emission, 78833, abbreviated):

```json
{ "MessageName": "OnExternalUpdated", "PrimaryEntityName": "mserp_wtc_custpostaladdressdeletedv2entity",
  "InputParameters": [
    { "key": "Target",   "value": { "Attributes": [ { "key": "mserp_addressstreet", "value": "222 Main street No. 2" }, …, { "key": "mserp_wtc_isdeleted", "value": { "Value": 200000001 } } ] } },
    { "key": "PreImage", "value": { "Attributes": [ { "key": "mserp_addressstreet", "value": "222 Main street" }, …, { "key": "mserp_wtc_isdeleted", "value": { "Value": 200000001 } } ] } },
    { "key": "ChangedFields", "value": [ "mserp_addressstreet", "mserp_addressvalidfrom", "mserp_addressvalidto", "mserp_formatteddeliveryaddress" ] } ] }
```

## Risks / Trade-offs

- [The `deactivate` path that doesn't expire the address (standard skips the update when it can't
  resolve an entity-location table)] → WTC still sets the flag and the pointer, but the pointed version is
  active, so the `ValidTo <` range keeps it out of the entity. That is correct, because the address is not
  actually expired.
- [A location linked to several `DirPartyLocation` rows still has several rows] → Detected by D4. The
  integration safety net covers it.
- [A genuine deletion without `mserp_wtc_isdeleted` in `ChangedFields`] → Not possible: the root range
  `WTC_IsDeleted = 1` means the record can't exist in Dataverse before the flag is set. Verified by
  deploy task 4.4 with a multi-version address.
- [Backfill not run in an environment] → Old deletions disappear from the entity. Capture is unaffected
  because no event is raised. Verified by task 4.3.
- [Capture already holds the effects of 40 duplicate deletes in BAT] → Open question 2.
- [Conflict with #5236 (`fix-customer-address-update-action`)] → It edits `WTC_CustPostalAddressV2Entity.xml`,
  and this change edits `WTC_CustPostalAddressDeletedV2Entity.xml`, `LogisticsLocation.WTC.xml`, and the
  `deactivate` extension, so the files don't overlap. Its spec delta modifies a different requirement of
  `customer-address-delete-forwarding`. The D365 work shares the `C:\Custom` branch
  `bug/5236/customer-address-update-action` (open question 1).
- [The backfill makes a hidden deleted location reappear in the entity as a new row] → After D2 is
  deployed, a flagged location whose pointer is still `0` has no entity row. The backfill's `update()`
  makes the row appear, which is the same shape as the genuine delete 78800 (PreImage null, every field in
  `ChangedFields`, including `mserp_wtc_isdeleted`). The integration filter would pass it and forward one
  `Delete` per already-deleted location. In TEST and PRD these would be held behind the disabled delete
  route and released when it is enabled. D3 assumes Dataverse still holds the old image, but this is
  unconfirmed. **Open question 5; decide before task 4.5** (found in review, Bowser round 1).

## Migration Plan

1. Integration: merge, then `eim` deploys BAT and TEST. Duplicates stop at the integration immediately.
2. D365: build the WTC model, DB sync, and deploy the package (DEV/UT, then BAT/TEST, then PRD). No
   Dataverse refresh is needed.
3. Per environment, right after the package: run `WTC_CustPostalAddressDeletedBackfill` once, then run the
   D4 query.
4. Verify (tasks 4.x).

Rollback: the D365 package can be reverted. The new field is harmless, but the entity would go back to
multiple rows, which the integration filter covers. For the integration, redeploy the previous package.

## Open Questions

1. ~~Ticket number.~~ **Resolved:** #5236. D365 work goes on the existing `bug/5236/customer-address-update-action` branch.
2. **Duplicate deletes already delivered to Capture in BAT** (40 messages for `000311920`/`000311720`).
   Confirm with the Capture team that a repeated `Delete` is a no-op.
3. ~~Keep the integration safety net?~~ **Resolved 2026-10-26:** yes, plan B (pointer field plus backfill)
   together with the integration filter.
4. **Backfill invocation.** Assumed a runnable class run from the browser URL (`?mi=SysClassRunner&cls=…`),
   without a menu item or security. Change this if the D365 team requires a menu item or a data-upgrade
   script.
5. **Backfill event shape.** Can the backfill produce real duplicate `Delete`s (see Risks)? Options:
   (a) **two packages**: ship the field, `deactivate` and the backfill first, then the relation change
   (D2) in a second package, so rows stay visible throughout; (b) add the user who runs the backfill to
   `ExcludedUsers` in `DataEventProcessor` for the duration of the run; (c) first check in DEV what event a
   backfilled location raises. Recommended: (c), then (a) if a creation-shaped event appears.
