## Why

Creating a customer party contact in D365 publishes an `mserp_dirpartycontactv3entity` data event to Service Bus, but no `PartyContact_To_Capture` message is ever created in EIM. `QueueDataEventsHandler` requires at least one of `mserp_wtc_custaccount` / `mserp_wtc_contactperson` to be populated, and D365 commit `fae9ea87` (2026-09-03, "Removing custtable and contact table") removed the `CustTable`/`ContactPerson` outer joins and the `WTC_CustAccount`/`WTC_ContactPerson` mapped fields from `DirPartyContactV3Entity`. Since that deployment every PartyContact data event is completed as "skipped" at Information level, so contact changes silently stop flowing to Capture.

## What Changes

- **D365 (WTC model, `c:\Custom\Metadata`)**: add a read-only computed field `WTC_PartyRole` on `DirPartyContactV3Entity` returning `Customer`, `Contact`, `Both`, or blank, using `EXISTS` checks on `CustTable.Party` / `ContactPerson.Party` against the entity's `DirPartyLocation.Party`. Remove the `CustTable`/`ContactPerson` outer joins and the mapped `WTC_CustAccount`/`WTC_ContactPerson` fields; the staging table carries `WTC_PartyRole` instead.
- **Dataverse**: refresh the `mserp_dirpartycontactv3entity` virtual entity so `mserp_wtc_partyrole` is present in data event payloads (verified in DEV).
- **Integration (`QueueDataEventsHandler`)**: filter PartyContact events on a non-blank `mserp_wtc_partyrole`. Log skips with entity, event, business key, and party number: Information when the role is blank (non-customer/contact party), Warning when the column is absent (schema drift).
- **Integration (`PartyContact_To_Capture`)**: derive `CustomerPartyNumber` / `ContactPartyNumber` from `mserp_wtc_partyrole`, force-read so update events keep the classification, and pin the mapping with unit tests.
- Operational follow-up: identify and replay PartyContact data events skipped since the D365 deployment of `fae9ea87`.

## Capabilities

### New Capabilities
- `partycontact-data-event-capture`: D365 → Capture flow for `DirPartyContactV3Entity` data events — which payload fields identify a customer vs. contact party contact, when an event is filtered out, and how filtered events are surfaced.

### Modified Capabilities
<!-- None: partycontact-capture-response covers the Capture → D365 response direction only and is unchanged. -->

## Impact

- D365 metadata (branch `bug/4883/issueinpartycontact`): `DirPartyContactV3Entity.WTC.xml`, `WTC_DirPartyContactV3Entity_Extension.xml` (`wtcPartyRole` computed-column method), `DirPartyContactV3Staging.WTC.xml`. Requires a D365 build, DB sync, and deployment.
- Dataverse: virtual entity metadata refresh for `mserp_dirpartycontactv3entity`; data event subscription unchanged.
- Integration code: [ARM.Integration.EIM.Customer/QueueDataEventsHandler.cs](../../../ARM.Integration.EIM.Customer/QueueDataEventsHandler.cs), [Wilson.Integration.Orchestrations/Orchestrations/PartyContact_To_Capture.cs](../../../Wilson.Integration.Orchestrations/Orchestrations/PartyContact_To_Capture.cs), [ARM.Integration.EIM.Tests/PartyContactToCaptureTests.cs](../../../ARM.Integration.EIM.Tests/PartyContactToCaptureTests.cs).
- The D365 and integration changes must ship together: the integration no longer reads `mserp_wtc_custaccount` / `mserp_wtc_contactperson`.
- No change to EIM routes, Service Bus queues, or the Capture message contract.
