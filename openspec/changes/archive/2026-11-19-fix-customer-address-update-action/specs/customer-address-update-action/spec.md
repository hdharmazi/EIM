## ADDED Requirements

### Requirement: The D365 address entity marks records that supersede a prior version

The D365 F&SC customer postal address entity SHALL expose a read-only yes/no field that marks a
superseding version. The entity is `WTC_CustPostalAddressV2Entity` (Dataverse
`mserp_wtc_custpostaladdressv2entity`); the field is `WTC_HasPriorVersion`
(`mserp_wtc_haspriorversion`) and is **Yes** when at least one other postal
address row exists for the same location with an earlier valid-from date, and **No** otherwise. The
value SHALL be computed when the record is read, so that it is correct regardless of whether the
address was changed through the data entity, OData, or the standard D365 address form.

#### Scenario: First version of an address

- **WHEN** a customer address is created in D365 and its data event is raised
- **THEN** the event's `mserp_wtc_haspriorversion` attribute means No

#### Scenario: Edited address creates a new version

- **WHEN** an existing customer address is edited in the D365 address form so that a new date-effective version is created for the same location id
- **THEN** the data event for the new version carries `mserp_wtc_haspriorversion` meaning Yes and the same `mserp_addresslocationid` as the original creation

#### Scenario: Field is present in the Dataverse data event

- **WHEN** the virtual entity metadata has been refreshed in an environment
- **THEN** data events for `mserp_wtc_custpostaladdressv2entity` include the `mserp_wtc_haspriorversion` attribute as an option set value (`200000001` = Yes, `200000000` = No)

### Requirement: A creation event for a superseding version is forwarded as an update

For events on `mserp_wtc_custpostaladdressv2entity`, the system SHALL forward `Action = "Update"`
to Capture when the event's message name indicates a creation **and** `mserp_wtc_haspriorversion`
means Yes. When the message name indicates a creation and the marker is absent, blank, or does not
mean Yes, the system SHALL forward `Action = "New"`. Actions derived from any other message name
SHALL NOT be changed by the marker.

#### Scenario: Superseding version becomes an update

- **WHEN** an `OnExternalCreated` event for address location `000312031` arrives with `mserp_wtc_haspriorversion` = `{"Value": 200000001}`
- **THEN** the message forwarded to Capture has `Action` equal to `Update`, `Entity` equal to `CustomerAddress`, and `SourceId` equal to `000312031`

#### Scenario: Marker accepted as integer or string

- **WHEN** an `OnExternalCreated` event arrives with `mserp_wtc_haspriorversion` given as the number `200000001` or the string `"200000001"`
- **THEN** the forwarded `Action` is `Update`

#### Scenario: First version stays a creation

- **WHEN** an `OnExternalCreated` event arrives with `mserp_wtc_haspriorversion` = `{"Value": 200000000}`
- **THEN** the forwarded `Action` is `New`

#### Scenario: Marker absent keeps today's behaviour

- **WHEN** an `OnExternalCreated` event arrives whose target attributes do not contain `mserp_wtc_haspriorversion`
- **THEN** the forwarded `Action` is `New` and no warning or error is recorded for the missing attribute

#### Scenario: Unexpected marker value is treated as No

- **WHEN** an `OnExternalCreated` event arrives with `mserp_wtc_haspriorversion` equal to `""`, `null`, or an unrecognised value such as `"maybe"`
- **THEN** the forwarded `Action` is `New` and the message is not failed

#### Scenario: Genuine update is unaffected by the marker

- **WHEN** an `OnExternalUpdated` event arrives with changed field `mserp_addresslocationroles` and `mserp_wtc_haspriorversion` meaning either Yes or No
- **THEN** the forwarded `Action` is `Update`, as before this change

#### Scenario: Delete forwarding is unaffected by the marker

- **WHEN** a soft-delete event on `mserp_wtc_custpostaladdressdeletedv2entity` arrives, with or without `mserp_wtc_haspriorversion`
- **THEN** the forwarded `Action` is `Delete`

### Requirement: A version-based update carries the full address

The forwarded message SHALL contain every mapped address field present in the event when a
creation-shaped event is forwarded as `Update` because of the prior-version marker, not only fields
listed in a changed-field list, because D365 provides no field-level diff for a new version.

#### Scenario: All mapped fields are populated

- **WHEN** an `OnExternalCreated` event with `mserp_wtc_haspriorversion` meaning Yes is forwarded
- **THEN** the forwarded `Data` contains `DataAreaId`, `CustomerAccount`, `AddressLocationId`, `IsPrimary`, `AddressLocationRoles`, `AddressDescription`, `AddressCity`, `AddressZipCode`, `AddressStreet`, `AddressCountryRegionId`, and `AddressState` with the values from the event

#### Scenario: Version-based update is not withheld as untracked

- **WHEN** an `OnExternalCreated` event with `mserp_wtc_haspriorversion` meaning Yes is processed
- **THEN** the message is sent to the Capture queue rather than completed with result `not tracked fields`

### Requirement: Orchestrations can refine the action from the event

The D365 event forward base behaviour SHALL let a concrete orchestration refine the action derived
from the event's message name using the parsed event and its target attributes, before any forced
action is applied. An orchestration that does not refine the action SHALL behave exactly as before.

#### Scenario: Default refinement is identity

- **WHEN** a D365 event is processed by an orchestration that does not override the refinement
- **THEN** the action equals the one derived from the message name (`New`, `Update`, or `Delete`)

#### Scenario: Forced action wins over refinement

- **WHEN** an orchestration both refines the action and declares a forced action
- **THEN** the forwarded action is the forced action

#### Scenario: Engine remains entity-agnostic

- **WHEN** the engine source is inspected for the address marker attribute name
- **THEN** `mserp_wtc_haspriorversion` appears only in client orchestration code and tests, not in `ARM.Integration.EIM`
