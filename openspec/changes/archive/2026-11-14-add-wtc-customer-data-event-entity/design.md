## Context

Customer data events come from the standard CustomersV3 entity (`CustCustomerV3Entity`, Dataverse
`mserp_custcustomerv3entity`). `DataEventProcessor` routes them to `Customer_To_Capture`, which
derives the Capture action from the message name: `OnExternalCreated` → `New`.

The D365 data event framework decides the event type per **entity** row
(`ApplicationFoundation`, 10.0.2645.81):

1. `BusinessEventsDataEventRegistrationImplementation.getQueryPattern()` is
   `FilterOnEntityRecordIds`. `BusinessEventsRegistrationBase.createQueriesForEntityUsingEntity`
   registers **every** data source of the entity, including nested ones.
2. `getCoreTableValue(qbds)` marks a table as core when its own join mode is InnerJoin or ExistsJoin.
   `LogisticsPostalAddressBaseEntity` (outer-joined three times by CustomersV3) inner-joins
   `LogisticsPostalAddress` (no JoinMode), so `LogisticsPostalAddress` is a core table of CustomersV3.
3. `BusinessEventsDataEventChangeNotifier.processNotification`: an insert on a core table becomes
   an entity **Create**, and an insert or delete on a non-core table becomes an **Update**.
4. `BusinessEventsDataEventChangeProcessor` de-duplicates by entity RecId and emits Delete, then
   Create, then Update.

Editing a postal address's street, city or zip expires the old valid-time-state row and inserts a new
`LogisticsPostalAddress` row, so the customer emits **Create**. A description edit touches only
`LogisticsLocation`, so it emits nothing. The default delivery and invoice address joins likely cause
the same false Create.

## Goals / Non-Goals

**Goals:**

- Customer data events reflect customer changes only: a Create only for a new customer, and an
  Update only when a Capture-mapped field changes.
- Reuse the existing Capture `Customer` message and orchestration, with no new message type.
- Replace CustomersV3 outright. The project is in implementation, so no coexistence is needed.

**Non-Goals:** see `proposal.md`. No stop-gap, no change to the standard entity, and no change to the
address routes.

## Decisions

### D1. A lean WTC entity with no address data sources

`WTC_CustCustomerEntity` in the WTC model (`C:\Custom\Metadata\WTC\WTC\AxDataEntityView`):

| Data source | Table | Join | Relation | Core? |
|---|---|---|---|---|
| `CustTable` | CustTable | root | none | yes (root) |
| `DirPartyTable` | DirPartyTable | **InnerJoin** | `CustTable.Party == DirPartyTable.RecId` | yes: created with the customer |
| `WTC_CustTableExt` | WTC_CustTableExt | **OuterJoin** | `CustTable.AccountNum == WTC_CustTableExt.AccountNum` | no: insert/delete → Update |

There are no `LogisticsPostalAddressBaseEntity`, `LogisticsLocation`, `DirPartyLocation`,
`LogisticsElectronicAddress` or contact-person data sources. Address edits therefore cannot reach the
entity's registered tables.

`DirPartyTable` updates also happen when the primary address *location* is switched
(`PrimaryAddressLocation`). Those are Updates on non-tracked fields, which `DataEventProcessor`
drops.

Fields (names chosen so the Dataverse attributes match CustomersV3 exactly):

| Entity field | Source | Dataverse attribute |
|---|---|---|
| `CustomerAccount` (key, mandatory) | `CustTable.AccountNum` | `mserp_customeraccount` |
| `OrganizationName` | `DirPartyTable.Name` | `mserp_organizationname` |
| `NameAlias` | `DirPartyTable.NameAlias` | `mserp_namealias` |
| `PartyNumber` | `DirPartyTable.PartyNumber` | `mserp_partynumber` |
| `CustomerGroupId` | `CustTable.CustGroup` | `mserp_customergroupid` |
| `OnHoldStatus` | `CustTable.Blocked` | `mserp_onholdstatus` |
| `MainContactWorker` (private) | `CustTable.MainContactWorker` | n/a |
| `EmployeeResponsibleNumber` | computed: `smmUtility::workerPersonnelNumberQuery(tablestr(WTC_CustCustomerEntity), dataEntityDataSourceStr(WTC_CustCustomerEntity, CustTable), fieldstr(CustTable, MainContactWorker))`, the same method body as CustomersV3 | `mserp_employeeresponsiblenumber` |
| `DOTNumber` | `WTC_CustTableExt.DOTNumber` | `mserp_dotnumber` |
| `TaxStatus` | `WTC_CustTableExt.TaxStatus` | `mserp_taxstatus` |
| `dataAreaId` (system) | `CustTable.DataAreaId` | `mserp_dataareaid` |

The entity has these properties:

- `PrimaryKey = EntityKey (CustomerAccount)` and `PrimaryCompanyContext = DataAreaId`.
- `IsPublic = Yes`, with `PublicEntityName = WTCCustomer` and `PublicCollectionName = WTCCustomers`.
- `IsReadOnly = Yes`, because it is an event source and not a write path.
- `DataManagementEnabled = No` and `ConfigurationKey = LedgerBasic`.

No security privilege is added; the existing WTC event entities ship without one. If the virtual
entity cannot be enabled because the entity is read-only, set `IsReadOnly = No` and keep it unused
for writes.

**Writes stay on CustomersV3.** Capture → D365 (`Customer_To_D365FSC`) keeps writing through the
standard `CustomersV3` entity. Its `mapEntityToDataSource` converts `EmployeeResponsibleNumber` to
`CustTable.MainContactWorker` and runs `processChangesForApproval`. `WTC_CustCustomerEntity`
deliberately has no `mapEntityToDataSource`: its computed `EmployeeResponsibleNumber` cannot be
written back. Do not make this entity a write path without adding that logic. Writing WTC-only
fields (DOT number, tax status) from Capture is a separate future change.

**Alternatives rejected:**

- **Stop-gap filter on CustomersV3**, for example dropping a Create whose `mserp_addressvalidfrom`
  is within N seconds. It is heuristic and would drop a genuine create made with an address. The user
  also declined it, because the entity is working as designed.
- **`dataEventsShouldSkipDataSource` on CustomersV3 through CoC.** The framework calls
  `tableHasStaticMethod`, which does not see extension methods. It is also behind the flight
  `BusinessEventsDataEventsShouldSkipDatasource_KillSwitch`. Fragile and unverified.
- **Map Create → Update in `Customer_To_Capture`** when the customer already exists in Capture. It
  needs a Capture lookup, and it still forwards address-only noise as customer updates.
- **A new orchestration type, such as `WtcCustomer_To_Capture`.** It is not needed because the
  attribute names are identical. Reusing `Customer_To_Capture` avoids a registry change and any
  deploy-order coupling with the route migration.

### D2. Integration routing: replace CustomersV3 with the new entity

`DataEventProcessor`:

- **Remove** the `mserp_custcustomerv3entity` entries from `EntityRoutes` and
  `TrackedFieldsByEntity`. CustomersV3 events that still arrive are then completed as
  "unsupported entity" before DB insert. This is the harmless fallback if a subscription is left on.
- **Add** `EntityRoutes["mserp_wtc_custcustomerentity"] = ("Customer_To_Capture", "D365ToCaptureQueue")`.
- **Add** `TrackedFieldsByEntity["mserp_wtc_custcustomerentity"]`: `mserp_organizationname`,
  `mserp_namealias`, `mserp_dataareaid`, `mserp_customeraccount`, `mserp_customergroupid`,
  `mserp_employeeresponsiblenumber`, **`mserp_onholdstatus`**, `mserp_dotnumber`, `mserp_taxstatus`
  and `mserp_partynumber`.

  This list fixes the old one, which had `mserp_blocked` (never sent, so on-hold changes were
  silently dropped) and `mserp_depositrequired` (no source).

`BusinessKeyResolver.EntityKeyFields`:

- Replace `mserp_custcustomerv3entity` with `mserp_wtc_custcustomerentity` → `mserp_customeraccount`.
- Update the doc-comment example entity name.

`BusinessKeyRegistrationTests` enforces route/key/fixture parity in both directions. Therefore:

- Delete `Wilson.Integration.Tests/TestData/SourceEntities/mserp_custcustomerv3entity.json`.
- Add `mserp_wtc_custcustomerentity.json`.

### D3. Capture payload mapping

`Customer_To_Capture.MapFields` now serves only the new entity:

- `DotNumber = getField("mserp_dotnumber", true)` and `TaxStatus = getField("mserp_taxstatus", true)`.
- **Remove** the placeholders (`"X1234"`, `"30CHARSTATUS"`) and the commented-out lines.
- **Do not set `DepositRequired`.** It stays null and is omitted from the JSON
  (`JsonIgnore(WhenWritingNull)`).

The `CaptureCustomerMessageContract.DepositRequired` property is **kept**.
`Customer_To_D365FSC.BuildResponseData` still fills it in the Capture → D365 "New" response, and that
flow is out of scope.

An empty DOT number or tax status in D365 follows the existing `getField` behaviour, the same as
other optional fields such as `NameAlias`. Luigi confirms in a test what an empty value produces, and
does not change `D365EventForwardOrchestration`.

### D4. Route migration 016

`script/SQL/Migrations/016_replace_customer_route.sql`, following the 014 and 015 pattern:

1. Insert-only `MERGE` into `dbo.ROUTETABLE` for
   `ENVIRONMENT IN ('BAT','DEV','DEV2','TEST','PRD')`:
   - `SOURCESYSTEM = 'D365FSC'`, `SOURCEENTITY = 'mserp_wtc_custcustomerentity'`
   - `ORCHESTRATIONTYPE = 'Customer_To_Capture'`, `ENABLED = 1`, `ONPAUSE = 'Hold'`
   - `MODIFIEDBY = 'Migrations.016_replace_customer_route.sql'`
2. `DELETE FROM dbo.ROUTETABLE WHERE SOURCESYSTEM = 'D365FSC' AND SOURCEENTITY = 'mserp_custcustomerv3entity'`
   for all environments.
3. Parity check, `THROW 50016`:
   - fails if any of the five new rows is missing;
   - fails if a row still `MODIFIEDBY` this script differs in type, ENABLED or ONPAUSE;
   - fails if any `mserp_custcustomerv3entity` row remains.

The migration is safe on both databases, non-production `eimdb` and production `sqldb-eim-wtc-prd`
(001 to 015 run first there):

- `Customer_To_Capture` stays registered, so `RouteRegistryValidator` accepts the result before or
  after the code deploys.
- If the migration runs before the code, CustomersV3 events that reach the old host get a
  `RouteNotFound` dead-letter until the new host is deployed. Run the migration in the same pipeline
  run as the deploy, which is the normal `eim.yml` order. That is acceptable in the implementation
  phase.
- Migrations 006 and 015 are already journaled and do not re-run, so their parity checks are
  unaffected.

### D5. Dashboard

- `ARM.Integration.Dashboard.Frontend/src/features/routes/api.ts`: replace
  `mserp_custcustomerv3entity: "customer"` with `mserp_wtc_custcustomerentity: "customer"`.
- `ARM.Integration.Dashboard.API/Services/SimulationService.cs`:
  - The three D365 customer scenarios (Create/Update/Delete) send `mserp_wtc_custcustomerentity`.
  - Create sends `mserp_onholdstatus`, `mserp_dotnumber` and `mserp_taxstatus` instead of
    `mserp_blocked` and `mserp_depositrequired`.

## Failure behaviour

- **Missing key**: `mserp_customeraccount` is mandatory on the entity. If it is absent anyway,
  `ResolveSourceId` falls back to the operation id with a warning (existing behaviour).
  `DefaultFailureClassifier` is unchanged.
- **Untracked updates**, such as a primary-location switch or `WTC_CustTableExt` financial-letter
  fields, are completed before DB insert.
- **Stray CustomersV3 events** (subscription not yet deactivated): completed as unsupported entity
  before DB insert, with no message row.
- **Ordering, retries and idempotency**: unchanged, inherited from `Customer_To_Capture` and the EIM
  engine.

## Risks / Trade-offs

- [Computed `EmployeeResponsibleNumber`] A change of `MainContactWorker` must appear in
  `ChangedFields` as `mserp_employeeresponsiblenumber`. This works the same way as CustomersV3 today.
  It is verified manually in BAT; if missing, track `mserp_maincontactworker` in a follow-up.
- [Read-only entity and virtual entity] Read-only entities can normally be enabled as virtual
  entities. The fallback is noted in D1.
- [`DirPartyTable` hierarchy] The entity reads only base-table fields (`Name`, `NameAlias`,
  `PartyNumber`), so no derived data sources are needed.
- [Payload change for Capture] Capture gets real (possibly empty) `DOTNumber` and `TaxStatus`
  values instead of placeholders, and no `DepositRequired`. The user confirmed this.
- [Cross-repo] Luigi can author the X++ XML but cannot build or DB-sync. The D365 developer
  verifies.
- [No rollback route] CustomersV3 routing is removed. To go back, revert this change and add a
  migration that restores the rows. This is acceptable in the implementation phase.

## Migration Plan

Per environment (BAT → TEST, and PRD when it is stood up):

1. Build and deploy the WTC model, DB sync, then refresh the entity list and enable the Dataverse
   virtual entity `mserp_wtc_custcustomerentity`.
2. Deploy the integration through `eim.yml` (code + migration 016 in one run).
3. In Business events → Data events, activate Create/Update/Delete for `WTCCustomer` on the existing
   endpoint, and deactivate the CustomersV3 data events.
4. Smoke test:
   - Edit a primary address street on a test customer: no customer message appears in eimdb.
   - Change the on-hold status: a single `Update` appears.
   - Create a customer: a single `New` appears.

## Decisions confirmed by the user (2026-11-11)

1. Real `DOTNumber` and `TaxStatus` go to Capture. `DepositRequired` is removed from the
   D365 → Capture payload.
2. Entity name `WTC_CustCustomerEntity` (Dataverse `mserp_wtc_custcustomerentity`, public
   `WTCCustomer`).
3. The X++ tasks stay in this change; there is no mirror change in `C:\Custom\openspec`.
4. CustomersV3 routing is removed in this change. There is no cutover period (implementation
   phase).
