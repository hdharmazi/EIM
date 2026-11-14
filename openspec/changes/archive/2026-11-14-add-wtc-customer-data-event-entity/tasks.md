Ticket #4403. Branch `feature/4403/wtc-customer-data-event-entity` in this repo and in `C:\Custom`.

## 1. D365 entity (WTC model, `C:\Custom`): Luigi authors, D365 developer builds and syncs

- [x] 1.1 Create `C:\Custom\Metadata\WTC\WTC\AxDataEntityView\WTC_CustCustomerEntity.xml`, using `WTC_CustPostalAddressV2Entity.xml` as the XML shape template.
  - Data sources: `CustTable` (root), `DirPartyTable` (InnerJoin on `Party` = `RecId`) and `WTC_CustTableExt` (OuterJoin on `AccountNum`).
  - Fields, key, company context, public names and `IsReadOnly = Yes`, exactly per design D1.
  - The computed method `employeeResponsibleWorker` copies the CustomersV3 body with the entity name swapped.
  - No address, location or contact data sources.
- [x] 1.2 Add a label for the entity (`WTC Customer`) in the WTC label file used by the other WTC entities, or use a literal label as `WTC_CustPostalAddressV2Entity` does.
- [x] 1.3 Add the new object to the WTC model project file (`.rnrproj`) in `C:\Custom`, if objects are listed there.

## 2. Integration: Luigi

- [x] 2.1 `Wilson.Integration.Automation/Outbound/DataEventProcessor.cs`:
  - Remove the `mserp_custcustomerv3entity` entries from `EntityRoutes` and `TrackedFieldsByEntity`.
  - Add the `mserp_wtc_custcustomerentity` route (→ `Customer_To_Capture`, `D365ToCaptureQueue`) and its tracked fields, exactly per design D2 (it uses `mserp_onholdstatus`).
- [x] 2.2 `ARM.Integration.EIM/Utilities/BusinessKeyResolver.cs`: replace the `mserp_custcustomerv3entity` key with `["mserp_wtc_custcustomerentity"] = "mserp_customeraccount"`, and update the doc-comment example.
- [x] 2.3 `Wilson.Integration.Orchestrations/Orchestrations/Customer_To_Capture.cs`:
  - Map `DotNumber` and `TaxStatus` from `mserp_dotnumber` and `mserp_taxstatus`.
  - Remove the placeholders, the commented-out lines and the `DepositRequired` assignment.
  - Update the class summary to name the WTC entity.
  - Do not change `CaptureCustomerMessageContract` or `Customer_To_D365FSC`.
- [x] 2.4 Add `script/SQL/Migrations/016_replace_customer_route.sql` (design D4):
  - An insert-only MERGE of the new route for BAT, DEV, DEV2, TEST and PRD.
  - A DELETE of every `D365FSC`/`mserp_custcustomerv3entity` row.
  - A parity check with `THROW 50016`.
  - A header comment in the 014/015 style.
- [x] 2.5 `ARM.Integration.Dashboard.Frontend/src/features/routes/api.ts`: replace `mserp_custcustomerv3entity: "customer"` with `mserp_wtc_custcustomerentity: "customer"`.
- [x] 2.6 `ARM.Integration.Dashboard.API/Services/SimulationService.cs`:
  - The D365 customer Create/Update/Delete scenarios send `mserp_wtc_custcustomerentity`.
  - Create sends `mserp_onholdstatus`, `mserp_dotnumber` and `mserp_taxstatus` instead of `mserp_blocked` and `mserp_depositrequired`.
- [x] 2.7 Search the repo (excluding `openspec/changes/archive`, `bin`, `obj` and `node_modules`) for any remaining `mserp_custcustomerv3entity` reference in code, tests or docs, and update it. Archived OpenSpec changes are left untouched.

## 3. Tests: Luigi

- [x] 3.1 Delete `Wilson.Integration.Tests/TestData/SourceEntities/mserp_custcustomerv3entity.json`.
- [x] 3.2 Add `mserp_wtc_custcustomerentity.json` in the same folder, sanitized, in the same envelope shape, with `mserp_dotnumber`, `mserp_taxstatus` and the new entity's logical name and id attribute. `BusinessKeyRegistrationTests` must pass.
- [x] 3.3 Add routing and tracked-field tests in `Wilson.Integration.Tests`:
  - `EntityRoutes` maps the new entity to `Customer_To_Capture` and does not contain `mserp_custcustomerv3entity`.
  - `HasTrackedChange` is true for `[mserp_onholdstatus]` and false for `[mserp_wtc_consignment]`.
- [x] 3.4 Add `Customer_To_Capture` mapping tests:
  - Create gives `Action = "New"`, SourceId equal to the customer account, and `DOTNumber`/`TaxStatus` from the event.
  - The serialized `Data` has no `DepositRequired` property.
  - With empty DOT and tax values, no `"X1234"` or `"30CHARSTATUS"` appears.
- [x] 3.5 If any test asserts the migration list or the route seeds (for example the repository layout or `DbMigrator` tests), extend it to cover 016.
- [x] 3.6 Run, all green:
  - `dotnet build ARM.Integration.Wilson.slnx`
  - `dotnet test` on `ARM.Integration.EIM.Tests`, `ARM.Integration.D365FSC.Gateway.Tests` and `Wilson.Integration.Tests`
  - `npm --prefix ARM.Integration.Dashboard.Frontend run lint`

## 4. Deploy and verify (manual: user and D365 developer, not Luigi)

- [ ] 4.1 Build the WTC model and DB sync in BAT. Refresh the Dataverse entity list and enable the virtual entity `mserp_wtc_custcustomerentity`.
- [ ] 4.2 Deploy the integration through `pipelines/eim.yml` (migration 016 runs). Check that ROUTETABLE has the five new rows and no `mserp_custcustomerv3entity` rows.
- [ ] 4.3 Activate the Create/Update/Delete data events for `WTCCustomer` on the existing endpoint, and deactivate the CustomersV3 data events.
- [ ] 4.4 Smoke test in BAT:
  - Edit the primary address street of a test customer: no customer message appears in eimdb.
  - Change the on-hold status: one `Update` reaches Capture.
  - Change the employee responsible: an `Update` appears, which verifies the computed field.
  - Create a customer: one `New`, with real DOT number and tax status and no `DepositRequired`.
- [ ] 4.5 Repeat 4.1 to 4.4 in TEST, and in PRD when it is stood up.
