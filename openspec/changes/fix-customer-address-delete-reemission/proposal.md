**Ticket:** #5236. This change shares the ticket with `fix-customer-address-update-action`. Related: #4611 (D365 soft-delete entity) and #5425 (delete route).

## Why

Editing a customer in D365 F&SC (for example its name) sends `CustomerAddress` **`Delete`** messages to
Capture for addresses that were deleted long before. BAT messages **78833–78850** are 18
`CustomerAddressDelete_To_Capture` messages for two addresses of customer `100300` (`000311920` and
`000311720`), all raised in the same second as the customer name update 78851, and all forwarded to
Capture (`STATUS = 2`, Completed). The same thing happened at 13:54 (78808–78825, 18 messages) and
15:09–15:10 (78827/78828/78830/78831, around an address edit). Since the route was enabled in BAT, the
only genuine deletion is 78800. The other 40 are re-notifications of addresses that were already deleted.

The root cause is the D365 soft-delete entity `WTC_CustPostalAddressDeletedV2Entity`. Its root data
source is `LogisticsLocation`, and it joins `LogisticsPostalAddress` with the range
`ValidTo < 2154-12-31`, which matches **every expired version** of the address. A location that was edited
before it was deleted therefore has several entity rows that share one Dataverse record id. The live
entity `WTC_CustPostalAddressV2Entity` uses `ValidTo = 2154-12-31` and has exactly one row per location.
F&SC raises update data events when *any* table under the entity changes. On a customer save, Dataverse
re-reads the record, finds the address version flipped, and raises `OnExternalUpdated` with
`ChangedFields = [addressstreet, addressvalidfrom, addressvalidto, formatteddeliveryaddress]`. In the
same 19:32 window the live address entity raised **no** event for the same customer's active address,
which shows that an unchanged single row is not re-notified. The integration forwards every event on the
soft-delete entity as `Delete` and has no changed-field filter for it.

## What Changes

- **D365 F&SC (WTC model, repo `C:\Custom`), the root-cause fix**:
  - `LogisticsLocation.WTC` gets a new field `WTC_DeletedPostalAddress`
    (`LogisticsPostalAddressRecId`). It records which address version was deactivated.
  - `WTC_LogisticsPostalAddressTable_Extension.deactivate` sets this field in the same update that sets
    `WTC_IsDeleted`. Standard `deactivate` expires the current version in place (`Correction`), so this is
    `this.RecId`.
  - `WTC_CustPostalAddressDeletedV2Entity` joins `LogisticsPostalAddress` on that RecId as well as the
    location. This gives exactly one row per deleted location, so there is no version flip and no
    re-notification. Fields, key, and public names are unchanged.
  - A one-off runnable class `WTC_CustPostalAddressDeletedBackfill` fills the new field for locations
    that were deleted before the release, using their latest version. Without it they would drop out of
    the entity.
- **Integration (`Wilson.Integration.Automation`), the safety net**: `DataEventProcessor` gives the
  soft-delete entity `mserp_wtc_custpostaladdressdeletedv2entity` a tracked-field set containing only
  `mserp_wtc_isdeleted`. An `OnExternalUpdated` on that entity is accepted only when the deleted flag is
  among its changed fields, i.e. when the address enters the deleted state. Anything else is completed
  before the database insert. This protects BAT now, before the D365 package lands. It also covers the
  one-off re-notification that the backfill causes, and any other future multi-row join on that entity.
- The tracked-field check is extracted into a testable `internal static` helper, its skip log names the
  entity and changed fields, and `D365FSCEventParser.GetChangedFields` no longer throws when the
  `ChangedFields` parameter is absent.

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `customer-address-delete-forwarding`: the soft-delete source exposes one record per deleted address.
  An update-shaped event yields `Delete` only when it marks the transition into the deleted state, and
  re-notifications of an already-deleted address are not forwarded.

## Impact

- D365 metadata (`C:\Custom`, existing branch `bug/5236/customer-address-update-action`):
  - `Metadata/WTC/WTC/AxTableExtension/LogisticsLocation.WTC.xml` (new field)
  - `Metadata/WTC/WTC/AxClass/WTC_LogisticsPostalAddressTable_Extension.xml` (set the field)
  - `Metadata/WTC/WTC/AxDataEntityView/WTC_CustPostalAddressDeletedV2Entity.xml` (relation)
  - new `Metadata/WTC/WTC/AxClass/WTC_CustPostalAddressDeletedBackfill.xml`, plus a menu item if the
    team's convention needs one to run it

  The release needs a WTC build, a DB sync, a package deployment, and a backfill run per environment.
  No Dataverse virtual-entity refresh is needed because the exposed fields are unchanged.
- Integration code: `Wilson.Integration.Automation/Outbound/DataEventProcessor.cs`,
  `ARM.Integration.EIM/Utilities/D365FSCEventParser.cs`, tests and one fixture in `Wilson.Integration.Tests`.
- No SQL migration, route seed, `OrchestrationRegistry` change, new exception type, Dashboard change, or
  deployment config change in this repo.
- The two parts are independent and can ship in either order. The recommended order is integration
  first, then D365 plus backfill (see design, Migration Plan).

## Non-goals

- Changing how D365 versions addresses, or the live entity `WTC_CustPostalAddressV2Entity`. That entity is
  covered by #5236 (`fix-customer-address-update-action`), which touches a different entity file.
- Undoing or compensating the 40 duplicate `Delete` messages already delivered to Capture in BAT. Ask
  the Capture team; see open questions.
- Changing the reprocess path in the Dashboard API. Reprocessing a stored historical re-notification
  still forwards `Delete`.
- Changing any other entity's tracked fields, or dedupe by `SourceId`, or decisions based on EIM
  message history.
