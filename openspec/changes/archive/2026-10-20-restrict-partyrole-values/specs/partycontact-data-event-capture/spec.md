## MODIFIED Requirements

### Requirement: PartyContact data events carry the party role
D365 `DirPartyContactV3Entity` SHALL expose a read-only computed field `WTC_PartyRole`, surfaced in Dataverse data event payloads as `mserp_wtc_partyrole`. Its value SHALL be one of `Customer`, `Contact`, or blank. It SHALL be `Customer` when a `CustTable` record exists whose `Party` equals the contact's owning party, otherwise `Contact` when a `ContactPerson` record exists with that `Party`, and blank otherwise. The field SHALL NOT add rows to the entity or participate in entity writes.

#### Scenario: Customer party contact created
- **WHEN** a user creates an electronic address on a party that is a customer
- **THEN** the `OnExternalCreated` data event for `mserp_dirpartycontactv3entity` contains `mserp_wtc_partyrole = "Customer"`

#### Scenario: Contact person party contact created
- **WHEN** a user creates an electronic address on a party that is a contact person
- **THEN** the `OnExternalCreated` data event contains `mserp_wtc_partyrole = "Contact"`

#### Scenario: Party is both customer and contact person
- **WHEN** a user creates an electronic address on a person party that is both a customer and a contact person
- **THEN** the data event contains `mserp_wtc_partyrole = "Customer"`

#### Scenario: Customer party in multiple legal entities
- **WHEN** a customer party has `CustTable` records in more than one legal entity
- **THEN** the entity returns exactly one row per electronic address with `WTC_PartyRole = "Customer"`

#### Scenario: Entity write unaffected
- **WHEN** Capture creates a PartyContact through `PartyContactsV3` OData
- **THEN** the record is created as before and `WTC_PartyRole` is not required or written

### Requirement: Integration forwards customer and contact PartyContact data events
`QueueDataEventsHandler` SHALL forward an `mserp_dirpartycontactv3entity` data event to the `PartyContact_To_Capture` orchestration only when `mserp_wtc_partyrole` is non-blank. `PartyContact_To_Capture` SHALL set `CustomerPartyNumber` to `mserp_partynumber` when the role is `Customer` and `ContactPartyNumber` to `mserp_partynumber` when the role is `Contact`, and SHALL set the other field to an empty string. Role comparison SHALL be case-insensitive and SHALL use the target value regardless of the update's changed-field set.

#### Scenario: Customer role
- **WHEN** a PartyContact data event has `mserp_partynumber = "000029968"` and `mserp_wtc_partyrole = "Customer"`
- **THEN** the Capture message has `CustomerPartyNumber = "000029968"` and `ContactPartyNumber = ""`

#### Scenario: Contact role
- **WHEN** a PartyContact data event has `mserp_partynumber = "000177803"` and `mserp_wtc_partyrole = "Contact"`
- **THEN** the Capture message has `ContactPartyNumber = "000177803"` and `CustomerPartyNumber = ""`

#### Scenario: Update without role change
- **WHEN** an `OnExternalUpdated` PartyContact event changes only `mserp_locator` and the target has `mserp_partynumber = "000029968"` and `mserp_wtc_partyrole = "Customer"`
- **THEN** the Capture message has `CustomerPartyNumber = "000029968"` and `ContactPartyNumber = ""`
