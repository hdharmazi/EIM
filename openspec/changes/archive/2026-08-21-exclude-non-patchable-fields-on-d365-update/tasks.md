## 1. Attribute

- [x] 1.1 Add `D365ExcludeOnUpdateAttribute` in `ARM.Integration.EIM/Model/D365FSC/`, targeting properties, sealed, with a required or settable `Reason` string
- [x] 1.2 Document on the attribute that it is load-bearing for correctness — removing a usage changes the wire payload sent to D365

## 2. Payload filtering in the orchestration base

- [x] 2.1 Add a helper that, given a target entity type, returns the set of `[JsonPropertyName]` values for all properties carrying `[D365ExcludeOnUpdate]`
- [x] 2.2 Cache the resolved name set per entity type so reflection runs once per type, not once per message
- [x] 2.3 Add a helper that serializes an entity, parses it to a `JsonObject`, removes the excluded property names (case-insensitive match), and returns the resulting JSON string
- [x] 2.4 In `D365CrudOrchestration.ExecuteActionAsync`, change the `Update` branch to call `BuildEntityKey(target)` against the unmodified entity, then send the filtered JSON to `UpdateRawAsync`
- [x] 2.5 Confirm the `New` and `Delete` branches still serialize the unfiltered entity
- [x] 2.6 Log the removed field names through `IOrchestrationLogger` in the `Update` branch, including the case where nothing was removed

## 3. Annotate entity models

- [x] 3.1 `CustomersEntity`: mark `DataAreaId` and `CustomerAccount` (OData key, supplied in URL)
- [x] 3.2 `CustomersEntity`: mark `PartyNumber` with reason quoting the D365 error `update not allowed for field 'PartyNumber'`
- [x] 3.3 `CustomersEntity`: mark `SalesCurrencyCode` as a create-only default so updates no longer overwrite the customer's currency
- [x] 3.4 `CustomerPostalAddressesEntity`: mark `DataAreaId`, `CustomerAccount`, `AddressLocationId` (OData key)
- [x] 3.5 `ContactPersonEntity`: mark `DataAreaId`, `ContactPersonId` (OData key)
- [x] 3.6 `PartyContactsEntity`: mark `PartyNumber`, `ElectronicAddressId` (OData key)
- [x] 3.7 `PartyContactsEntity`: mark `Type` (set on creation, not patchable in `PartyContactsV3`)
- [x] 3.8 Cross-check each annotated key field against the corresponding `BuildEntityKey` implementation so the names match exactly

## 4. Remove the superseded override

- [x] 4.1 Delete the `ExecuteActionAsync` override in `PartyContact_To_D365FSC` that hand-builds a `Type`-free entity
- [x] 4.2 Remove the now-unused explanatory comment above it, having moved the rationale into the attribute's `Reason`

## 5. Tests

- [x] 5.1 Create a unit test project for `ARM.Integration.EIM` — the solution currently has no test framework, so this includes adding the project, test SDK packages, and wiring it into `ARM.Integration.Wilson.slnx`
- [x] 5.2 Test that an `Update` payload omits every property marked `D365ExcludeOnUpdate` and retains every unmarked non-null property
- [x] 5.3 Test that for each of the four orchestrations, no field name used by its `BuildEntityKey` appears in the composed update payload
- [x] 5.4 Test that the entity key is still built from the full entity, so key values survive filtering
- [x] 5.5 Test that `New` payloads retain `SalesCurrencyCode` and `PartyNumber`
- [x] 5.6 Test that exclusion matches on `[JsonPropertyName]` rather than the C# property name, using `DotNumber` / `DOTNumber` as the case

## 6. Verification

- [x] 6.1 Build the solution and confirm no callers of the removed `PartyContact_To_D365FSC` override remain
- [ ] 6.2 Send a real capture message with `Action = "Update"` carrying both `CustomerAccount` and `PartyNumber`; confirm D365 accepts it and the step log shows the four stripped fields
- [ ] 6.3 Exercise the Contact update flow and confirm from the step log that only key fields were removed and the update still succeeds
- [ ] 6.4 Exercise the CustomerAddress update flow and confirm the same
- [ ] 6.5 Exercise the PartyContact update flow and confirm `Type` is still excluded after the override removal
- [ ] 6.6 Confirm a customer whose D365 currency is not `USD` retains it after an update
