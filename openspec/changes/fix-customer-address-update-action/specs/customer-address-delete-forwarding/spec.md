## MODIFIED Requirements

### Requirement: Address deletions are recognised from a dedicated soft-delete entity

D365 F&SC does not emit a delete event for customer postal addresses. The system SHALL treat events
on the dedicated soft-delete source entity `mserp_wtc_custpostaladdressdeletedv2entity` as customer
address deletions, and SHALL route them to Capture.

#### Scenario: A soft-delete event is routed

- **WHEN** a D365 data event arrives whose primary entity is `mserp_wtc_custpostaladdressdeletedv2entity`
- **THEN** the system routes it to an orchestration rather than discarding it as an unsupported entity

#### Scenario: The New/Update route is unaffected

- **WHEN** a D365 data event arrives whose primary entity is `mserp_wtc_custpostaladdressv2entity`
- **THEN** it is handled by the customer address creation/update orchestration, with its action derived from the event's message name and the prior-version marker as specified in `customer-address-update-action`, and never as a deletion
