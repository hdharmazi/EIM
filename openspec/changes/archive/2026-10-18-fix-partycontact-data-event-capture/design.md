## Context

D365 → Capture PartyContact sync works as follows:

1. D365 publishes a Dataverse virtual-entity data event (`OnExternalCreated` / `OnExternalUpdated` / `OnExternalDeleted`) for `mserp_dirpartycontactv3entity` to the `D365FscDataEventQueue` Service Bus queue.
2. `QueueDataEventsHandler` (ARM.Integration.EIM.Customer) routes it via `EntityRoutes` to `PartyContact_To_Capture`, but first applies `EntityRequiredAnyFields`: at least one of `mserp_wtc_contactperson` / `mserp_wtc_custaccount` must be non-blank, otherwise the message is completed and only an Information log is written.
3. `PartyContact_To_Capture.MapFields` uses the same two fields to decide whether `mserp_partynumber` is a `CustomerPartyNumber` or a `ContactPartyNumber`.

Those fields came from `DirPartyContactV3Entity.WTC.xml`, which outer-joined `CustTable` and `ContactPerson` on `DirPartyTable.RecId` and mapped `WTC_CustAccount = CustTable.AccountNum` and `WTC_ContactPerson = ContactPerson.ContactPersonId`. D365 commit `fae9ea87` (2026-09-03, "Removing custtable and contact table") removed both joins and both fields while working on the Capture → D365 response fix (`WTC_CustomerPartyNumber` / `WTC_ContactPartyNumber` unmapped fields). The staging table extension `DirPartyContactV3Staging.WTC.xml` still has `WTC_ContactPerson` / `WTC_CustAccount`.

Result: data events still fire (the user sees them), but the discriminator columns are absent or null, so 100% of PartyContact events are dropped by step 2.

The original join approach (`7944a110`) was also reported to show incorrect values. `DirPartyContactV3Entity` is shared across companies while `CustTable` / `ContactPerson` are per-company, so the outer joins produced one entity row per matching customer/contact record. Key lookups (`select firstonly`, OData read-back, data event read) then returned an arbitrary duplicate.

A second defect was found in `PartyContact_To_Capture`: the discriminators were read with `force: false`, so on `OnExternalUpdated` they were null unless they appeared in `ChangedFields`. They never appear there, being derived, so every update was sent to Capture with both party numbers blank.

The unmapped `WTC_CustomerPartyNumber` / `WTC_ContactPartyNumber` fields cannot replace them: they are virtual, populated only from inbound OData writes, and are blank when the data event framework reads the entity row.

## Goals / Non-Goals

**Goals:**
- Give PartyContact data events a reliable customer/contact classification without joined datasources.
- Keep routing, the business key, and the Capture message shape unchanged.
- Make a future loss of the classification column visible instead of silent.

**Non-Goals:**
- Changing the Capture → D365 PartyContact flow or `partycontact-capture-response` behaviour (cleanup of `WTC_CustomerPartyNumber` / `WTC_ContactPartyNumber` is a separate change).
- Adding a D365 OData lookup in the integration.
- Changing routes, queues, or the `mserp_electronicaddressid` business key.

## Decisions

### 1. One computed `WTC_PartyRole` field instead of joins or per-table fields
Add `WTC_PartyRole` (`String20`, `AllowEdit = No`) to `DirPartyContactV3Entity.WTC.xml` with `ComputedFieldMethod = WTC_DirPartyContactV3Entity_Extension::wtcPartyRole`. The method returns:

```sql
CASE
  WHEN EXISTS (SELECT 1 FROM CUSTTABLE C WHERE C.PARTY = <DirPartyLocation.PARTY> AND C.PARTITION = <DirPartyLocation.PARTITION>)
   AND EXISTS (SELECT 1 FROM CONTACTPERSON P WHERE P.PARTY = <...> AND P.PARTITION = <...>) THEN 'Both'
  WHEN EXISTS (... CUSTTABLE ...) THEN 'Customer'
  WHEN EXISTS (... CONTACTPERSON ...) THEN 'Contact'
  ELSE ''
END
```

Field references are built with `SysComputedColumn::returnField(...)` against `dataEntityDataSourceStr(DirPartyContactV3Entity, DirPartyLocation)`. The outer joins and mapped `WTC_CustAccount` / `WTC_ContactPerson` fields are removed; the staging table carries `WTC_PartyRole` only.

- *Why*: `EXISTS` yields exactly one value per entity row, so cross-company customers and multi-customer contact persons can no longer duplicate rows. The integration only needs the classification, not the account or contact ID.
- *Alternative — restore the outer joins*: rejected; produces duplicate rows and the wrong values seen with `7944a110`.
- *Alternative — `TOP 1` computed `WTC_CustAccount` / `WTC_ContactPerson`*: works, but returns an arbitrary ID that nothing consumes; a role label is clearer.
- *Alternative — `DirPartyTable.InstanceRelationType`*: rejected; it distinguishes organization vs. person, not customer vs. contact, and its table-ID value is environment-specific.
- *Alternative — integration looks up the party in D365 OData*: kept as fallback only; adds a D365 round trip per event.

### 2. Filter on `mserp_wtc_partyrole` and log by cause
`EntityRequiredAnyFields` for `mserp_dirpartycontactv3entity` becomes `{ mserp_wtc_partyrole }`. Skips log entity, event, business key, and party number. The level is Information when the attribute is present but blank (vendor, worker, or other party: normal, high volume) and Warning when the attribute is absent (schema drift, as happened after `fae9ea87`).

- *Alternative — Warning for every skip*: rejected; non-customer/contact parties would flood the warnings and hide real drift.
- *Alternative — dead-letter instead of complete*: rejected for the same volume reason.

### 3. Map from the role, force-read, and pin with unit tests
`PartyContact_To_Capture` reads `mserp_wtc_partyrole` with `force: true` (a computed field never appears in `ChangedFields`) and sets `CustomerPartyNumber` for `Customer`/`Both` and `ContactPartyNumber` for `Contact`/`Both`. Tests in `PartyContactToCaptureTests` cover Customer, Contact, Both, blank, and update-without-role-change payloads.

### 4. Verify in SQL before and after deployment
- No duplicates: `SELECT PARTYNUMBER, ELECTRONICADDRESSID, COUNT(*) FROM DIRPARTYCONTACTV3ENTITY GROUP BY PARTYNUMBER, ELECTRONICADDRESSID HAVING COUNT(*) > 1` returns no rows.
- Correct role: `SELECT PARTYNUMBER, WTC_PARTYROLE FROM DIRPARTYCONTACTV3ENTITY WHERE PARTYNUMBER IN (<known customer>, <known contact>)`.

## Risks / Trade-offs

- [`EXISTS` subquery cost on large scans] → Both subqueries hit existing `Party` indexes on `CUSTTABLE` / `CONTACTPERSON`; data events read single rows by key.
- [Dataverse virtual entity not refreshed after deployment] → `mserp_wtc_partyrole` is absent and events are filtered; the Warning log makes this visible. Refresh is an explicit deployment task with a verification step.
- [Dataverse omits empty-string attributes] → A blank role could arrive as absent and log at Warning for non-customer/contact parties; monitor Warning volume after rollout and relax to Information if it is noisy.
- [D365 and integration deployed out of order] → The integration no longer reads the old columns, so deploy both together per environment.
- [Events dropped since `fae9ea87` was deployed are not stored] → The handler completes filtered messages without persisting the payload, so they cannot be replayed from EIM; recovery requires re-triggering in D365 or a Capture-side reconciliation (see Open Questions).

## Migration Plan

1. D365: build the WTC model on `bug/4883/issueinpartycontact`, DB sync, deploy DEV → TEST → BAT → PROD with the normal package.
2. Dataverse: in each environment, refresh `mserp_dirpartycontactv3entity` virtual entity metadata; confirm `mserp_wtc_partyrole` exists.
3. Integration: deploy together with step 1–2 in each environment.
4. Verify: create a phone/email on a test customer and a test contact person; confirm EIM rows with Type `PartyContact_To_Capture` and correct `CustomerPartyNumber` / `ContactPartyNumber`.
5. Rollback: roll back both the D365 package and the integration; either alone leaves PartyContact events filtered.

## Open Questions

- How should PartyContact changes made between the `fae9ea87` deployment and this fix be backfilled to Capture — touch-update affected records in D365, or a one-off export from D365?
