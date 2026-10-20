## ADDED Requirements

### Requirement: D365 updates and deletes require complete entity keys
Before sending a D365 update or delete, the system SHALL verify that every OData key field has a non-blank value. When a key field is null or blank, the system SHALL NOT call D365 and SHALL fail the message with a response message that names the missing key field.

#### Scenario: PartyContact update without ElectronicAddressId
- **WHEN** Capture submits a `PartyContact` Update with `ContactPartyNumber = "000181101"` and no `ElectronicAddressId`
- **THEN** no request is sent to D365
- **AND** the message is recorded at status `3` with response message `Required key field 'ElectronicAddressId' is missing from the source message.`

#### Scenario: Blank key value
- **WHEN** a D365 update is built with a key field whose value is whitespace only
- **THEN** it is treated as missing and the message fails naming that key field

#### Scenario: Create is unaffected
- **WHEN** Capture submits a `PartyContact` New without `ElectronicAddressId`
- **THEN** the create is sent to D365 as before
