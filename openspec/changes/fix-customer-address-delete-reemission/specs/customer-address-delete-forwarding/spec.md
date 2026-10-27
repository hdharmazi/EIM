## MODIFIED Requirements

### Requirement: The delete action is constant, not derived from the event message name

Soft-delete events arrive with a message name indicating an update. The system SHALL emit
`Action = "Delete"` for every soft-delete event it accepts, regardless of the message name carried by
the event. The action SHALL NOT be derived from the message name for this entity. An update-shaped
event is accepted only when it records the address entering the deleted state, as specified in
*Re-notifications of an already-deleted address are not forwarded*.

#### Scenario: An update-shaped event yields a delete

- **WHEN** a soft-delete event arrives with message name `OnExternalUpdated` and its changed fields include the deleted flag `mserp_wtc_isdeleted`
- **THEN** the message forwarded to Capture has `Action` equal to `Delete`

#### Scenario: Any other message name yields a delete

- **WHEN** a soft-delete event arrives with a message name indicating creation or deletion instead of update
- **THEN** the message forwarded to Capture still has `Action` equal to `Delete`

#### Scenario: Changed-field tracking does not suppress fields

- **WHEN** a soft-delete event is forwarded and the event's changed-field list omits one of the mapped address fields
- **THEN** that field is still populated in the forwarded message from the event's attributes

## ADDED Requirements

### Requirement: Re-notifications of an already-deleted address are not forwarded

The system SHALL forward an update event from the soft-delete entity only when the deleted flag
`mserp_wtc_isdeleted` is among the event's changed fields, and SHALL acknowledge any other update event
from that entity without creating a message record and without sending anything to Capture. D365 F&SC
can re-raise update events on the soft-delete entity for addresses that are already deleted, for
example when related customer data changes while the entity still exposes several versions of a
deleted address. This filter is the integration's safety net and applies whether or not the D365
source fix is deployed.

#### Scenario: The deletion itself is forwarded

- **WHEN** a soft-delete event with message name `OnExternalUpdated` arrives whose changed fields include `mserp_wtc_isdeleted` and whose deleted flag means yes
- **THEN** it passes the ingress filter and is forwarded to Capture with `Action` equal to `Delete`

#### Scenario: A customer edit re-notifies a deleted address

- **WHEN** a soft-delete event with message name `OnExternalUpdated` arrives whose changed fields are `mserp_addressstreet`, `mserp_addressvalidfrom`, `mserp_addressvalidto` and `mserp_formatteddeliveryaddress` and whose PreImage already has the deleted flag set to yes
- **THEN** the Service Bus message is completed, no message record is created, and nothing is sent to Capture

#### Scenario: Any update without the deleted flag among the changed fields is withheld

- **WHEN** a soft-delete event with message name `OnExternalUpdated` arrives whose changed fields do not include `mserp_wtc_isdeleted`, whatever other fields they list
- **THEN** the Service Bus message is completed without a message record being created

#### Scenario: Withheld re-notifications are logged for diagnosis

- **WHEN** a soft-delete event is withheld because the deleted flag is not among its changed fields
- **THEN** an information-level log entry names the message, the entity, the event name, and the changed fields

#### Scenario: Missing changed-field list is withheld with a warning

- **WHEN** a soft-delete event with message name `OnExternalUpdated` arrives with no `ChangedFields` parameter
- **THEN** the event does not fail processing, the Service Bus message is completed without a message record being created, and a warning-level log entry names the message and the entity

#### Scenario: The create/update address route is unaffected

- **WHEN** a D365 data event with message name `OnExternalUpdated` arrives for `mserp_wtc_custpostaladdressv2entity` whose changed fields include a tracked address field such as `mserp_addressstreet`
- **THEN** it passes the ingress filter exactly as before

### Requirement: The soft-delete source exposes one record per deleted address

The D365 F&SC soft-delete entity `WTC_CustPostalAddressDeletedV2Entity` SHALL expose exactly one row per
deleted address location: the address version that was expired when the address was deleted. When an
address is deactivated for the first time, D365 SHALL record that version on the location in the same
update that sets the deleted flag. Locations deleted before this rule existed SHALL be given their
latest address version by a one-off backfill, so that those whose latest version is expired remain in
the entity. The entity's fields, key
and public names SHALL NOT change. These scenarios are verified manually in a D365 environment.

#### Scenario: A multi-version deleted address yields one entity row

- **WHEN** an address whose street was edited (so it has two versions) is deleted from a customer
- **THEN** the soft-delete entity contains exactly one row for that customer account and location id, showing the version that was current at deletion, and exactly one data event is raised for it

#### Scenario: A customer edit does not re-notify a deleted address

- **WHEN** the name of a customer that has deleted, multi-version addresses is changed after the D365 change and backfill are deployed
- **THEN** no update event is raised on the soft-delete entity for those addresses

#### Scenario: Pre-existing deletions survive the release

- **WHEN** the backfill runs in an environment that has locations already flagged as deleted
- **THEN** each such location has its latest address version recorded, every one whose latest version is expired is still listed in the soft-delete entity with one row, and rerunning the backfill changes nothing

#### Scenario: A deactivation that does not expire the address is not exposed

- **WHEN** an address is deactivated but its current version is not expired (its valid-to stays at the maximum date)
- **THEN** the location does not appear in the soft-delete entity
