# customer-data-event-forwarding Specification

## Purpose
TBD - created by archiving change add-wtc-customer-data-event-entity. Update Purpose after archive.
## Requirements
### Requirement: Customer data events are routed from the lean WTC customer entity

The system SHALL route D365 data events whose primary entity is `mserp_wtc_custcustomerentity` to the
`Customer_To_Capture` orchestration on the D365-to-Capture queue. It SHALL resolve their business
key from `mserp_customeraccount`.

#### Scenario: A WTC customer event is routed

- **WHEN** a D365 data event arrives whose primary entity is `mserp_wtc_custcustomerentity`
- **THEN** the system routes it to `Customer_To_Capture` using the `D365ToCaptureQueue` setting, rather than discarding it as an unsupported entity

#### Scenario: The business key is the customer account

- **WHEN** an `mserp_wtc_custcustomerentity` event carries `mserp_customeraccount = "AR-082797"`
- **THEN** the message SourceId is `AR-082797`

#### Scenario: A genuine customer create is forwarded as New

- **WHEN** an `OnExternalCreated` event arrives for `mserp_wtc_custcustomerentity`
- **THEN** Capture receives a `Customer` message with `Action = "New"`

### Requirement: WTC customer updates are forwarded only for Capture-mapped fields

For `mserp_wtc_custcustomerentity`, the system SHALL forward an `OnExternalUpdated` event only when
its changed fields include at least one of `mserp_organizationname`, `mserp_namealias`,
`mserp_dataareaid`, `mserp_customeraccount`, `mserp_customergroupid`,
`mserp_employeeresponsiblenumber`, `mserp_onholdstatus`, `mserp_dotnumber`, `mserp_taxstatus` or
`mserp_partynumber`. It SHALL complete other updates before DB insert.

#### Scenario: On-hold status change is forwarded

- **WHEN** an update on `mserp_wtc_custcustomerentity` has ChangedFields `[mserp_onholdstatus]`
- **THEN** the event has a tracked change and is processed

#### Scenario: An untracked change is skipped

- **WHEN** an update on `mserp_wtc_custcustomerentity` has ChangedFields containing only fields outside the tracked list (for example `mserp_wtc_consignment`)
- **THEN** the event has no tracked change and is completed without a message row

### Requirement: The WTC customer entity carries no address data

The D365 entity `WTC_CustCustomerEntity` SHALL NOT include postal address, location, contact or
electronic address data sources. Editing a customer's postal address SHALL NOT emit a customer data
event.

#### Scenario: Primary address street edit emits no customer create

- **WHEN** a user edits the street of a customer's primary postal address in D365 after cutover
- **THEN** eimdb receives the address event for `mserp_wtc_custpostaladdressv2entity` and no `mserp_wtc_custcustomerentity` message for that customer

### Requirement: DOT number and tax status are mapped from the event, and DepositRequired is not sent

`Customer_To_Capture` SHALL map Capture `DOTNumber` and `TaxStatus` from `mserp_dotnumber` and
`mserp_taxstatus`. It SHALL NOT emit hard-coded placeholder values. It SHALL NOT include
`DepositRequired` in the D365 → Capture customer message.

#### Scenario: WTC customer event with DOT number and tax status

- **WHEN** an `mserp_wtc_custcustomerentity` event carries `mserp_dotnumber = "DOT987"` and `mserp_taxstatus = "Exempt"`
- **THEN** the Capture message `Data` has `DOTNumber = "DOT987"` and `TaxStatus = "Exempt"`, and has no `DepositRequired` property

#### Scenario: No placeholders

- **WHEN** an `mserp_wtc_custcustomerentity` event carries no `mserp_dotnumber` or `mserp_taxstatus` value
- **THEN** the Capture message does not contain `"X1234"` or `"30CHARSTATUS"`

### Requirement: CustomersV3 customer events are no longer routed

The system SHALL NOT route data events whose primary entity is `mserp_custcustomerv3entity`. It SHALL
complete them as an unsupported entity before DB insert. The CustomersV3 tracked fields and business
key registration SHALL be removed.

#### Scenario: A stray CustomersV3 event is skipped

- **WHEN** a D365 data event arrives for `mserp_custcustomerv3entity`
- **THEN** it is not in the entity routes, no message row is created, and the Service Bus message is completed

#### Scenario: Business key registration matches the routes

- **WHEN** the business key registrations are compared with the entity routes
- **THEN** `mserp_wtc_custcustomerentity` is in both and `mserp_custcustomerv3entity` is in neither

### Requirement: Migration 016 replaces the customer route in every environment

Migration `016_replace_customer_route.sql` SHALL insert, if absent, a `D365FSC` route for
`mserp_wtc_custcustomerentity` to `Customer_To_Capture` for `BAT`, `DEV`, `DEV2`, `TEST` and `PRD`,
with `ENABLED = 1` and `ONPAUSE = 'Hold'`. It SHALL delete every `D365FSC` route for
`mserp_custcustomerv3entity`. It SHALL fail when any new row is missing or any CustomersV3 row
remains afterwards.

#### Scenario: Database with migrations 001 to 015 applied

- **WHEN** migration 016 runs
- **THEN** ROUTETABLE contains the five `mserp_wtc_custcustomerentity` rows, enabled with `ONPAUSE = 'Hold'`, and no `mserp_custcustomerv3entity` rows

#### Scenario: Operator-modified row is preserved

- **WHEN** an operator has disabled the PRD `mserp_wtc_custcustomerentity` row in the Routes console and migration 016 is re-run
- **THEN** the row is left as the operator set it and the migration succeeds

