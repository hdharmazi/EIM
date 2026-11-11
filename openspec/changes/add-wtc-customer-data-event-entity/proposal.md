## Why

Ticket **#4403**. Editing a customer's primary address in D365 F&SC (street, city, zip and so on)
makes the CustomersV3 data event (`mserp_custcustomerv3entity`) fire **OnExternalCreated** for an
existing customer. The integration forwards it to Capture as `Action = "New"`.

Evidence from eimdb (non-production):

- 78923 (AR-082797 primary address, new version) was followed one second later by 78922
  (AR-082797 customer `OnExternalCreated`). 78924 and 78925 show the same pair for 100300.
- In the last 14 days, 8 of 9 primary-address creates came with a customer Create. The customer
  payload's `mserp_addressvalidfrom` equals the new address version's timestamp.
- A description-only edit (78921) changes `LogisticsLocation` only and emits no customer Create.

Root cause, in standard Microsoft code (10.0.2645.81), so it is working as designed:

- `CustCustomerV3Entity` outer-joins `LogisticsPostalAddressBaseEntity` for the primary, default
  delivery and default invoice addresses.
- Inside that entity, `LogisticsPostalAddress` is inner-joined (no JoinMode) and is valid-time-state.
  `BusinessEventsRegistrationBase.getCoreTableValue` therefore registers `LogisticsPostalAddress` as
  a **core table** of CustomersV3.
- An address edit ends the old `LogisticsPostalAddress` version and inserts a new one.
  `BusinessEventsDataEventChangeNotifier` treats an insert on a core table as an entity **Create**.

We cannot change the standard entity's data sources. Capture only needs about ten customer fields,
none of which are address fields. So the fix is a lean WTC customer entity with no address,
contact or location data sources, used only for customer data events.

## What Changes

- **D365 (WTC model, `C:\Custom`)**: add the data entity `WTC_CustCustomerEntity` (Dataverse
  `mserp_wtc_custcustomerentity`). Its data sources are `CustTable`, inner-joined `DirPartyTable` and
  outer-joined `WTC_CustTableExt`. It exposes the fields Capture needs, with the same field names as
  CustomersV3 (`CustomerAccount`, `OrganizationName`, `NameAlias`, `PartyNumber`, `CustomerGroupId`,
  `EmployeeResponsibleNumber`, `OnHoldStatus`) plus `DOTNumber` and `TaxStatus`.
- **Integration**: **replace** CustomersV3 with the new entity. Route `mserp_wtc_custcustomerentity`
  to the existing `Customer_To_Capture` orchestration, key it by `mserp_customeraccount`, and track
  the correct fields (`mserp_onholdstatus`, not `mserp_blocked`). Remove the
  `mserp_custcustomerv3entity` route, tracked fields, business key and test fixture. The project is
  still in implementation, so no coexistence period is kept.
- **Capture payload (D365 → Capture `Customer`)**: map the real `DOTNumber` and `TaxStatus` from
  `WTC_CustTableExt`, remove the hard-coded placeholders, and **drop `DepositRequired`** from the
  outbound message.
- **SQL**: migration `016_replace_customer_route.sql` seeds the new route for BAT, DEV, DEV2, TEST
  and PRD, and deletes the `mserp_custcustomerv3entity` routes.
- **Dashboard**: the routes entity-stem map and the simulation's D365 customer scenarios use the new
  entity.
- **D365 subscription (manual)**: activate the new entity's Create/Update/Delete data events and
  deactivate the CustomersV3 ones. No stop-gap filtering is added.

## Capabilities

### New Capabilities

- `customer-data-event-forwarding`: how D365 customer data events are recognised, filtered, keyed
  and forwarded to Capture, from the lean WTC entity only.

### Modified Capabilities

None. The route registry behaviour is unchanged; this change only adds a row.

## Impact

- **Code**: `Wilson.Integration.Automation/Outbound/DataEventProcessor.cs`,
  `ARM.Integration.EIM/Utilities/BusinessKeyResolver.cs`,
  `Wilson.Integration.Orchestrations/Orchestrations/Customer_To_Capture.cs`,
  `ARM.Integration.Dashboard.API/Services/SimulationService.cs`,
  `ARM.Integration.Dashboard.Frontend/src/features/routes/api.ts`.
- **Tests**: remove `Wilson.Integration.Tests/TestData/SourceEntities/mserp_custcustomerv3entity.json`
  and add the new entity's fixture.
- **SQL**: new `script/SQL/Migrations/016_replace_customer_route.sql`. The orchestration type is
  already registered, so `RouteRegistryValidator` accepts it in any order.
- **D365 (cross-repo)**: new `AxDataEntityView` in `C:\Custom\Metadata\WTC\WTC`. It needs a build,
  a DB sync, the Dataverse virtual entity refreshed and enabled, and a data event subscription in
  each environment (BAT, TEST, PRD).
- **Capture**: same entity (`Customer`). It gets real `DOTNumber` and `TaxStatus` values, no
  `DepositRequired`, and no more false `New` messages on address edits.
- **Reprocessing**: an old eimdb message for `mserp_custcustomerv3entity` can no longer be
  reprocessed from the Dashboard, because its route is gone (`RouteNotFound`). This is accepted
  during implementation.
- **No change** to `OrchestrationRegistry`, `DefaultFailureClassifier`, the Capture → D365 customer
  flow (`Customer_To_D365FSC` and its `DepositRequired` response field), Gateway or Dashboard API
  routes.

## Non-goals

- No stop-gap on CustomersV3, such as filtering a Create when `mserp_addressvalidfrom` is recent.
  The new entity replaces CustomersV3.
- No coexistence or rollback path for CustomersV3. The project is in implementation, with no
  production traffic yet.
- No change to the standard `CustCustomerV3Entity` or its WTC extension. A
  `dataEventsShouldSkipDataSource` override on a standard entity is not pursued.
- No change to the customer address routes (`mserp_wtc_custpostaladdressv2entity`, the soft-delete
  entity) or their actions.
- No change to the `CaptureCustomerMessageContract` class. `DepositRequired` stays on it for the
  Capture → D365 "New" response. The D365 → Capture mapping simply leaves it null, so it is not
  serialized (`WhenWritingNull`).
