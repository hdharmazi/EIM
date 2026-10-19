## MODIFIED Requirements

### Requirement: PartyContact success responses preserve customer association
Capture-to-D365 PartyContact success responses SHALL set Capture `ContactPartyNumber` and `CustomerPartyNumber` from the values submitted in the Capture `PartyContact` message. The response SHALL NOT derive either field from the D365 result `PartyNumber`, and a party-number field that was not submitted SHALL be returned as an empty string.

#### Scenario: Contact-only create response
- **WHEN** Capture submits a `PartyContact` create with `ContactPartyNumber = "000181101"` and blank `CustomerPartyNumber`
- **AND** D365 returns a PartyContact result with `PartyNumber = "000181101"`
- **THEN** the successful Capture response contains `Data.ContactPartyNumber = "000181101"`
- **AND** the successful Capture response contains `Data.CustomerPartyNumber = ""`

#### Scenario: Customer-only create response
- **WHEN** Capture submits a `PartyContact` create with `CustomerPartyNumber = "000029968"` and no `ContactPartyNumber`
- **AND** D365 returns a PartyContact result with `PartyNumber = "000029968"`
- **THEN** the successful Capture response contains `Data.ContactPartyNumber = ""`
- **AND** the successful Capture response contains `Data.CustomerPartyNumber = "000029968"`

#### Scenario: Contact of a customer create response
- **WHEN** Capture submits a `PartyContact` create with `ContactPartyNumber = "000177803"` and `CustomerPartyNumber = "000000706"`
- **AND** D365 returns a PartyContact result with `PartyNumber = "000177803"`
- **THEN** the successful Capture response contains `Data.ContactPartyNumber = "000177803"` and `Data.CustomerPartyNumber = "000000706"`

### Requirement: PartyContact success responses use D365 result fields for D365-owned values
For `New` PartyContact actions with a D365 result entity, the response data SHALL use D365 result values for D365-owned electronic address fields, and SHALL use the submitted Capture values for the party-number fields.

#### Scenario: New response includes D365 electronic address fields
- **WHEN** D365 returns a PartyContact result with `ElectronicAddressId`, `Locator`, `Type`, and `LocationId`
- **THEN** the successful Capture response contains those fields from the D365 result
- **AND** the Capture party-number fields are populated from the submitted Capture message

#### Scenario: Non-create responses continue to echo request identity fields
- **WHEN** a PartyContact `Update` or `Delete` action completes without a D365 result entity
- **THEN** the response data contains the source `ContactPartyNumber`, source `CustomerPartyNumber`, and source `ElectronicAddressId`

## REMOVED Requirements

### Requirement: PartyContact D365 updates carry WTC party context
**Reason**: D365 `DirPartyContactV3Entity` no longer exposes `WTC_ContactPartyNumber` / `WTC_CustomerPartyNumber`; posting them fails with "property does not exist on type PartyContactV3".
**Migration**: The owning party is carried only by the standard `PartyNumber` key; see "PartyContact D365 requests send only standard party fields".

## ADDED Requirements

### Requirement: PartyContact D365 requests send only standard party fields
PartyContact Capture-to-D365 create and update requests SHALL identify the owning party only through D365 `PartyNumber`, resolved from Capture `ContactPartyNumber` when supplied and otherwise from `CustomerPartyNumber`. Requests SHALL NOT contain `WTC_ContactPartyNumber` or `WTC_CustomerPartyNumber`. Updates SHALL keep using `PartyNumber` plus `ElectronicAddressId` as the OData key.

#### Scenario: Contact-only create
- **WHEN** Capture submits a `PartyContact` create with `ContactPartyNumber = "000181101"`, blank `CustomerPartyNumber`, `Locator = "cchurch@mail.com"`, and `Type = "Email"`
- **THEN** the D365 create payload contains `PartyNumber = "000181101"`
- **AND** the D365 create payload contains no `WTC_ContactPartyNumber` or `WTC_CustomerPartyNumber` property

#### Scenario: Contact of a customer create uses the contact party
- **WHEN** Capture submits a `PartyContact` create with `ContactPartyNumber = "000177803"` and `CustomerPartyNumber = "000029968"`
- **THEN** the D365 create payload contains `PartyNumber = "000177803"`

#### Scenario: Customer-only update
- **WHEN** Capture submits a `PartyContact` update with `CustomerPartyNumber = "000029968"`, no `ContactPartyNumber`, and `ElectronicAddressId = "000188147"`
- **THEN** the D365 OData key uses `PartyNumber = "000029968"` and `ElectronicAddressId = "000188147"`
- **AND** the D365 update payload contains no `WTC_ContactPartyNumber` or `WTC_CustomerPartyNumber` property
