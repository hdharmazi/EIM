## ADDED Requirements

### Requirement: Update payloads exclude declared fields

When a `D365CrudOrchestration` executes an `Update` action, the serialized request body sent to D365 SHALL omit every entity property marked with the `D365ExcludeOnUpdate` attribute. All unmarked, non-null properties SHALL be included unchanged.

#### Scenario: Marked property is omitted from the PATCH body

- **WHEN** an `Update` action is executed for an entity whose `PartyNumber` property is marked `D365ExcludeOnUpdate` and has a non-null value
- **THEN** the request body sent to D365 does not contain a `PartyNumber` property

#### Scenario: Unmarked property is retained

- **WHEN** an `Update` action is executed for a `CustomersEntity` with `OrganizationName` set to `"Acme"` and no exclusion attribute on that property
- **THEN** the request body contains `"OrganizationName": "Acme"`

#### Scenario: Null properties remain omitted

- **WHEN** an `Update` action is executed for an entity with unmarked properties whose values are null
- **THEN** those properties are absent from the request body, as they were before exclusion filtering

#### Scenario: Customer update no longer rejected by D365

- **WHEN** a capture message with `Action = "Update"` supplies both `CustomerAccount` and `PartyNumber` for an existing customer
- **THEN** D365 accepts the PATCH request and does not return `update not allowed for field 'PartyNumber'`

### Requirement: Record addressing is unaffected by exclusion

Exclusion SHALL apply only to the serialized request body. The OData entity key used to address the record SHALL continue to be built from the complete mapped entity, before any field is removed.

#### Scenario: Key field is excluded from the body but present in the URL

- **WHEN** an `Update` action is executed for a `CustomersEntity` with `DataAreaId = "WTC"` and `CustomerAccount = "C-001"`, both marked `D365ExcludeOnUpdate`
- **THEN** the request targets `CustomersV3(dataAreaId='WTC',CustomerAccount='C-001')`
- **AND** the request body contains neither `dataAreaId` nor `CustomerAccount`

#### Scenario: No key field appears in any update payload

- **WHEN** an `Update` action is executed for any `D365CrudOrchestration` subclass
- **THEN** no field name used to construct that orchestration's entity key appears in the request body

### Requirement: Exclusions are declared on the entity model

Fields excluded from update payloads SHALL be declared by applying the `D365ExcludeOnUpdate` attribute to the property on the D365 entity model. The attribute SHALL carry a `Reason` describing why the field is excluded. Property matching SHALL be performed against the property's `JsonPropertyName` value.

#### Scenario: Attribute matches on wire name rather than C# name

- **WHEN** a property is declared as `public string DotNumber` with `[JsonPropertyName("DOTNumber")]` and is marked `D365ExcludeOnUpdate`
- **THEN** the field removed from the request body is `DOTNumber`

#### Scenario: Reason is recorded for each exclusion

- **WHEN** a property is marked `D365ExcludeOnUpdate`
- **THEN** the attribute usage states the reason the field is excluded, distinguishing OData key fields, fields D365 refuses to modify, and fields excluded by integration policy

### Requirement: Create and Delete behaviour is unchanged

The `D365ExcludeOnUpdate` attribute SHALL have no effect on `New` or `Delete` actions.

#### Scenario: Create payload retains excluded fields

- **WHEN** a `New` action is executed for a `CustomersEntity` with `SalesCurrencyCode = "USD"` and `PartyNumber` set, both marked `D365ExcludeOnUpdate`
- **THEN** the request body sent to D365 contains both fields

#### Scenario: Delete is unaffected

- **WHEN** a `Delete` action is executed
- **THEN** the request is issued exactly as before, with no body filtering applied

### Requirement: Customer sales currency is a creation default

`CustomersEntity.SalesCurrencyCode` SHALL be sent only when a customer is created. Update requests SHALL NOT modify the currency held in D365.

#### Scenario: New customer receives the default currency

- **WHEN** a capture message with `Action = "New"` creates a customer
- **THEN** the request body contains `"SalesCurrencyCode": "USD"`

#### Scenario: Existing customer currency is preserved

- **WHEN** a capture message with `Action = "Update"` targets a customer whose currency in D365 is not `USD`
- **THEN** the request body contains no `SalesCurrencyCode` field
- **AND** the customer's currency in D365 is unchanged after the update

### Requirement: Excluded fields are logged

Each `Update` action SHALL record, through the orchestration step logger, the names of the fields removed from the request body.

#### Scenario: Step log names the stripped fields

- **WHEN** an `Update` action removes `dataAreaId`, `CustomerAccount`, `PartyNumber`, and `SalesCurrencyCode` from a customer payload
- **THEN** the orchestration step log for that update includes those four field names

#### Scenario: Nothing excluded is still recorded

- **WHEN** an `Update` action is executed for an entity with no excluded fields
- **THEN** the step log records that no fields were removed
