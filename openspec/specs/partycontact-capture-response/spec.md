# partycontact-capture-response Specification

## Purpose

Define how Capture-to-D365 PartyContact operations map party-number fields, D365 custom relationship fields, and simulation payloads so Capture acknowledgements and D365 updates preserve the correct contact/customer context.

## Requirements

### Requirement: PartyContact success responses preserve customer association
Capture-to-D365 PartyContact success responses SHALL map D365 `WTC_CustomerPartyNumber` to Capture `CustomerPartyNumber` and D365 `WTC_ContactPartyNumber` to Capture `ContactPartyNumber` when those fields are returned by `PartyContactsV3`.

#### Scenario: New PartyContact response maps D365 custom contact party field
- **WHEN** D365 returns a PartyContact result with `PartyNumber = "000177803"` and `WTC_ContactPartyNumber = "000177803"`
- **THEN** the successful Capture response contains `Data.ContactPartyNumber = "000177803"`
- **AND** the value is sourced from `WTC_ContactPartyNumber`

#### Scenario: New PartyContact response maps D365 custom customer party field
- **WHEN** D365 returns a PartyContact result with `PartyNumber = "000177803"` and `WTC_CustomerPartyNumber = "000000706"`
- **THEN** the successful Capture response contains `Data.CustomerPartyNumber = "000000706"`
- **AND** the response does not replace `CustomerPartyNumber` with the D365 result `PartyNumber`

#### Scenario: Reported PartyContact response keeps blank customer party field blank
- **WHEN** D365 returns the reported PartyContact result with `WTC_ContactPartyNumber = "000177803"` and `WTC_CustomerPartyNumber = ""`
- **THEN** the successful Capture response contains `Data.ContactPartyNumber = "000177803"`
- **AND** the successful Capture response contains `Data.CustomerPartyNumber = ""`

#### Scenario: Blank custom contact party field falls back to submitted contact party number
- **WHEN** Capture submits a `PartyContact` message with `ContactPartyNumber = "000177803"`
- **AND** D365 returns a PartyContact result with `PartyNumber = "000177803"`, `WTC_ContactPartyNumber = ""`, and `WTC_CustomerPartyNumber = ""`
- **THEN** the successful Capture response contains `Data.ContactPartyNumber = "000177803"`
- **AND** the successful Capture response contains `Data.CustomerPartyNumber = ""`

#### Scenario: Customer-only input does not populate contact party number from generic party number
- **WHEN** Capture submits a `PartyContact` message with `CustomerPartyNumber = "000029968"` and no `ContactPartyNumber`
- **AND** D365 returns a PartyContact result with `PartyNumber = "000029968"`, `WTC_ContactPartyNumber = ""`, and `WTC_CustomerPartyNumber = ""`
- **THEN** the successful Capture response contains `Data.ContactPartyNumber = ""`
- **AND** the successful Capture response contains `Data.CustomerPartyNumber = "000029968"`

### Requirement: PartyContact success responses use D365 result fields for D365-owned values
For `New` PartyContact actions with a D365 result entity, the response data SHALL use D365 result values for D365-owned electronic address fields and D365 custom relationship fields.

#### Scenario: New response includes D365 electronic address fields
- **WHEN** D365 returns a PartyContact result with `ElectronicAddressId`, `Locator`, `Type`, `LocationId`, `WTC_ContactPartyNumber`, and `WTC_CustomerPartyNumber`
- **THEN** the successful Capture response contains those fields from the D365 result
- **AND** the Capture party-number fields are populated from the corresponding D365 `WTC_*PartyNumber` fields

#### Scenario: Non-create responses continue to echo request identity fields
- **WHEN** a PartyContact `Update` or `Delete` action completes without a D365 result entity
- **THEN** the response data contains the source `ContactPartyNumber`, source `CustomerPartyNumber`, and source `ElectronicAddressId`

### Requirement: PartyContact create simulation sends only supplied party number fields
The PartyContact create simulation SHALL include only the party-number fields supplied by the user and SHALL NOT populate `ContactPartyNumber` with a default when only `CustomerPartyNumber` is entered.

#### Scenario: Customer-only create simulation does not add contact party number
- **WHEN** the user runs the PartyContact create simulation with `CustomerPartyNumber = "000029968"` and an empty `ContactPartyNumber`
- **THEN** the generated request payload contains `CustomerPartyNumber = "000029968"`
- **AND** the generated request payload does not contain `ContactPartyNumber`

#### Scenario: Contact-only create simulation does not add customer party number
- **WHEN** the user runs the PartyContact create simulation with `ContactPartyNumber = "000177803"` and an empty `CustomerPartyNumber`
- **THEN** the generated request payload contains `ContactPartyNumber = "000177803"`
- **AND** the generated request payload does not contain `CustomerPartyNumber`

### Requirement: PartyContact D365 updates carry WTC party context
PartyContact Capture-to-D365 updates SHALL keep using D365 `PartyNumber` plus `ElectronicAddressId` as the OData key, and SHALL map supplied Capture party-number fields to the matching D365 `WTC_*PartyNumber` fields in the update entity.

#### Scenario: Customer-only update maps customer party to key and WTC customer field
- **WHEN** Capture submits a `PartyContact` update with `CustomerPartyNumber = "000029968"`, no `ContactPartyNumber`, and `ElectronicAddressId = "000188147"`
- **THEN** the D365 OData key uses `PartyNumber = "000029968"` and `ElectronicAddressId = "000188147"`
- **AND** the D365 update payload contains `WTC_CustomerPartyNumber = "000029968"`
- **AND** the D365 update payload does not contain `WTC_ContactPartyNumber`

#### Scenario: Contact-only update maps contact party to key and WTC contact field
- **WHEN** Capture submits a `PartyContact` update with `ContactPartyNumber = "000177803"`, no `CustomerPartyNumber`, and `ElectronicAddressId = "000188147"`
- **THEN** the D365 OData key uses `PartyNumber = "000177803"` and `ElectronicAddressId = "000188147"`
- **AND** the D365 update payload contains `WTC_ContactPartyNumber = "000177803"`
- **AND** the D365 update payload does not contain `WTC_CustomerPartyNumber`