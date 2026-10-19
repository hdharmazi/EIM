## 1. Confirm diagnosis

- [x] 1.1 In Application Insights, query `QueueDataEventsHandler` traces for "skipped: none of the required fields" with entity `mserp_dirpartycontactv3entity` since the `fae9ea87` deployment to confirm the drop and estimate volume (TEST: last processed 9/18, 22 skipped 9/28–10/16; BAT: 0 processed, 45 skipped 9/23–10/12; counts include legitimately filtered vendor/other parties)
- [x] 1.2 Capture one real `mserp_dirpartycontactv3entity` payload and confirm its shape matches the payload built in `PartyContactToCaptureTests` (EIM `eimdb` ID 78617: same `Target.Attributes`/`ChangedFields` shape; empty strings are kept as `""`; `ChangedFields = ["mserp_locator"]` confirms the update-path bug)

## 2. D365 `WTC_PartyRole` computed field (WTC model, branch `bug/4883/issueinpartycontact`)

- [x] 2.1 Add static computed-column method `wtcPartyRole` to `WTC_DirPartyContactV3Entity_Extension` returning `Customer` / `Contact` / `Both` / blank via `EXISTS` on `CUSTTABLE` / `CONTACTPERSON` keyed on `DirPartyLocation.Party` and partition
- [x] 2.2 Replace the `CustTable`/`ContactPerson` outer joins and mapped `WTC_CustAccount`/`WTC_ContactPerson` in `DirPartyContactV3Entity.WTC.xml` with computed `WTC_PartyRole` (`String20`, not editable); replace the staging fields with `WTC_PartyRole`
- [x] 2.3 Build the WTC model and run DB sync in DEV
- [x] 2.4 Verify in SQL that `DIRPARTYCONTACTV3ENTITY` has no duplicate `PARTYNUMBER`/`ELECTRONICADDRESSID` rows and `WTC_PARTYROLE` is correct for a customer, a contact person, and a multi-company customer (DEV: 94,081 rows, 0 duplicates, role matches base tables for all rows — 88,076 Customer, 7 Contact, 5,998 blank; no multi-company customers exist in DEV)
- [x] 2.5 Regression-check Capture → D365: create and update a PartyContact via `PartyContactsV3` OData and confirm behaviour matches `partycontact-capture-response`

## 3. Dataverse virtual entity

- [x] 3.1 Refresh `mserp_dirpartycontactv3entity` virtual entity metadata in DEV and confirm the `mserp_wtc_partyrole` column exists
- [x] 3.2 Create an email on a test customer in D365 and confirm the data event payload contains `mserp_wtc_partyrole = "Customer"`

## 4. Integration

- [x] 4.1 In `QueueDataEventsHandler`, log `EntityRequiredAnyFields` skips with `PrimaryEntityName`, `MessageName`, business key, and `mserp_partynumber`
- [x] 4.2 Add `PartyContact_To_Capture` unit tests for Customer, Contact, Both, blank, and update-without-role-change payloads, asserting `CustomerPartyNumber` / `ContactPartyNumber`
- [x] 4.3 Run `dotnet test ARM.Integration.EIM.Tests` and confirm all tests pass
- [x] 4.4 Force-read the classification field in `PartyContact_To_Capture` so update events keep the party classification
- [x] 4.5 Switch the PartyContact filter to `mserp_wtc_partyrole`; log blank role at Information and absent column at Warning
- [x] 4.6 Map `CustomerPartyNumber` for role `Customer`/`Both` and `ContactPartyNumber` for `Contact`/`Both` in `PartyContact_To_Capture`
- [x] 4.7 Build `ARM.Integration.EIM.Customer`

## 5. Rollout and recovery

- [x] 5.1 Deploy the D365 package, refresh the virtual entity, and deploy the integration together in TEST, BAT, then PROD (repeat 3.1–3.2 per environment)
- [x] 5.2 Deploy the integration change and verify end-to-end: new customer and contact-person party contacts produce `PartyContact_To_Capture` EIM messages delivered to Capture
- [x] 5.3 Agree and execute the backfill approach for PartyContact changes dropped since `fae9ea87` (see design Open Questions)
