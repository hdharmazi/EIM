## Why

Customer updates from Capture to D365 FSC fail with `update not allowed for field 'PartyNumber'`. The mapping layer is action-blind — `MapToTarget` produces one entity used for Create, Update, and Delete alike — so fields D365 refuses to patch are sent on every `Update`.

The problem is not specific to Customer. All four `D365CrudOrchestration` subclasses echo their OData key fields into the PATCH body, where they are at best redundant and at worst rejected. A single, uniform rule fixes the reported failure and removes the same latent defect from the other three flows.

## What Changes

- Introduce a `[D365ExcludeOnUpdate]` attribute that marks a D365 entity property as never belonging in a PATCH request body, with a `Reason` describing why.
- Change the `Update` branch of `D365CrudOrchestration` to build the entity key from the full mapped entity (unchanged), then strip all `[D365ExcludeOnUpdate]` properties from the serialized payload before sending.
- Annotate the four D365 entity models with the attribute, covering three categories:
  - **OData key fields** — already supplied in the request URL (`dataAreaId`, `CustomerAccount`, `AddressLocationId`, `ContactPersonId`, `PartyNumber`, `ElectronicAddressId` depending on entity).
  - **D365 read-only fields** — `CustomersEntity.PartyNumber`, `PartyContactsEntity.Type`.
  - **Create-only defaults** — `CustomersEntity.SalesCurrencyCode`, which is hardcoded to `"USD"` and currently overwrites the customer's currency on every update.
- Remove the hand-rolled `ExecuteActionAsync` override in `PartyContact_To_D365FSC` that exists solely to exclude `Type`; the attribute replaces it.
- Log the names of stripped fields via the orchestration step logger so update payload composition is observable.
- **BREAKING** (wire format, internal): PATCH bodies sent to `CustomersV3`, `WTCCustomerPostalAddressesV2`, `ContactPersons`, and `PartyContactsV3` no longer contain key fields. Record addressing is unaffected — keys continue to be supplied in the URL.

## Capabilities

### New Capabilities
- `d365-update-payload`: Rules governing which fields are included in the request body when a `D365CrudOrchestration` performs an `Update` against a D365 FSC OData entity, and how those exclusions are declared.

### Modified Capabilities
<!-- None. No existing spec in openspec/specs/ covers outbound D365 CRUD payload composition. -->

## Impact

**Affected code** (all in `ARM.Integration.EIM`):
- `Orchestration/D365CrudOrchestration.cs` — `Update` branch payload construction
- `Orchestration/PartyContact_To_D365FSC.cs` — remove `ExecuteActionAsync` override
- `Model/D365FSC/CustomersEntity.cs`, `CustomerPostalAddressesEntity.cs`, `ContactPersonEntity.cs`, `PartyContactsEntity.cs` — annotations
- New attribute type under `Model/D365FSC/` or `Utilities/`

**Not affected:**
- `ARM.Integration.Core` — `EntityKey`, `D365FSCClient`, and `ID365FSCClient` are unchanged. Exclusions are declared on the models rather than derived from `EntityKey`, keeping the change inside a single project.
- `Create` and `Delete` behaviour — only the `Update` payload changes.
- Capture message contracts and response envelopes.

**Systems:** D365 FSC OData endpoints for the four entities above, across every environment the EIM function app targets.

**Known coverage gap:** `SimulationService.CaptureCustomerUpdate` never populates `PartyNumber`, so the simulator does not reproduce the reported failure and will not regression-test it. Verification of the Customer path is manual against a real capture message. Extending the simulator is explicitly out of scope for this change.
