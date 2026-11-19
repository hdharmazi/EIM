# contact-data-event-capture Specification

## Purpose

Defines how D365 contact person data events are mapped and forwarded to Capture by `Contact_To_Capture`, including sending the full contact record on update.

## Requirements

### Requirement: Contact messages to Capture carry the full record on every action
`Contact_To_Capture` SHALL populate `dataAreaId`, `ContactPersonId`, `FirstName`, `MiddleName`, `LastName`, `AssociatedPartyNumber`, `AssociatedCustomerAccount`, and `ContactPersonPartyNumber` in the Capture `Data` object from the event's `Target` attributes for both `New` and `Update` actions, regardless of the update's changed-field set. `dataAreaId`, `FirstName`, `MiddleName`, and `LastName` SHALL be serialized as `""` when the source value is empty, null, or absent, and SHALL NOT be omitted.

#### Scenario: Update that changes only the first name
- **WHEN** an `OnExternalUpdated` contact event has `ChangedFields = ["mserp_firstname"]` and a `Target` with `mserp_dataareaid = "wtc"`, `mserp_contactpersonid = "000060"`, `mserp_firstname = "First Name Update"`, `mserp_middlename = ""`, `mserp_lastname = "Smith"`, `mserp_associatedpartynumber = "000029968"`, `mserp_wtc_associatedcustomeraccount = "100300"`, `mserp_contactpersonpartynumber = "000181501"`
- **THEN** the Capture message has `Action = "Update"` and `Data` contains `dataAreaId = "wtc"`, `ContactPersonId = "000060"`, `FirstName = "First Name Update"`, `MiddleName = ""`, `LastName = "Smith"`, `AssociatedPartyNumber = "000029968"`, `AssociatedCustomerAccount = "100300"`, `ContactPersonPartyNumber = "000181501"`

#### Scenario: Empty name fields are sent as empty strings on update
- **WHEN** an `OnExternalUpdated` contact event has `ChangedFields = ["mserp_firstname"]` and a `Target` with `mserp_middlename = ""` and `mserp_lastname = ""`
- **THEN** the serialized `Data` object contains `"MiddleName": ""` and `"LastName": ""`

#### Scenario: Absent name attribute is sent as an empty string
- **WHEN** a contact event (`OnExternalCreated` or `OnExternalUpdated`) has a `Target` without an `mserp_middlename` attribute
- **THEN** the serialized `Data` object contains `"MiddleName": ""`

#### Scenario: New and Update carry the same fields
- **WHEN** the same contact `Target` is delivered once as `OnExternalCreated` and once as `OnExternalUpdated` with `ChangedFields = ["mserp_firstname"]`
- **THEN** the two serialized `Data` objects have the same property names and values

#### Scenario: Unmapped contract fields stay omitted
- **WHEN** any contact event is mapped
- **THEN** the serialized `Data` object does not contain `AssociatedContactNumber` or `AssociatedContactType`

### Requirement: Contact updates without a tracked change are not forwarded
Forcing full-record mapping SHALL NOT change which contact updates are forwarded. `Contact_To_Capture` SHALL report no tracked changes, and SHALL complete the message as `not tracked fields` without sending it, when an `OnExternalUpdated` event's changed fields include none of the attributes it maps.

#### Scenario: Update of an unmapped attribute only
- **WHEN** an `OnExternalUpdated` contact event has `ChangedFields = ["mserp_associatedcontactnumber"]`
- **THEN** `ParseAndMap` returns `hasTrackedChanges = false`

#### Scenario: Update of a mapped attribute
- **WHEN** an `OnExternalUpdated` contact event has `ChangedFields = ["mserp_lastname"]`
- **THEN** `ParseAndMap` returns `hasTrackedChanges = true`
