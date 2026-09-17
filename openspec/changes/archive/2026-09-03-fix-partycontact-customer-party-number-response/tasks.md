## 1. Reproduce and Inspect

- [x] 1.1 Add or identify a focused test path that can invoke `PartyContact_To_D365FSC.BuildResponseData` without live D365 or Service Bus.
- [x] 1.2 Capture the failing `New` response behavior where Capture `CustomerPartyNumber` or `ContactPartyNumber` is malformed or copied from the generic D365 `PartyNumber` instead of the D365 `WTC_*PartyNumber` fields.

## 2. Core Implementation

- [x] 2.1 Add `WTC_ContactPartyNumber` and `WTC_CustomerPartyNumber` properties to `PartyContactsEntity` with matching JSON property names.
- [x] 2.2 Fix `PartyContact_To_D365FSC.BuildResponseData` so `ContactPartyNumber` is sourced from D365 `WTC_ContactPartyNumber` and `CustomerPartyNumber` is sourced from D365 `WTC_CustomerPartyNumber` for `New` success responses.
- [x] 2.3 Keep request-side mapping from source `ContactPartyNumber` or source `CustomerPartyNumber` to D365 `PartyNumber` unchanged.
- [x] 2.4 Preserve existing source-echo behavior for PartyContact `Update` and `Delete` responses.
- [x] 2.5 Fix PartyContact create simulation so it only includes party-number fields supplied by the user.
- [x] 2.6 Map supplied Capture party-number fields to D365 `WTC_ContactPartyNumber` and `WTC_CustomerPartyNumber` so PartyContact updates carry party context while retaining the `PartyNumber` OData key.

## 3. Test Coverage

- [x] 3.1 Add a focused test where D365 returns distinct `WTC_ContactPartyNumber` and `WTC_CustomerPartyNumber`, proving the Capture response maps each one to the matching Capture field.
- [x] 3.2 Add or update coverage for the reported D365 response shape where `WTC_ContactPartyNumber = "000177803"` and `WTC_CustomerPartyNumber = ""`.
- [x] 3.3 Assert D365 result fields such as `ElectronicAddressId`, `Locator`, `Type`, and `LocationId` still flow into the `New` success response.
- [x] 3.4 Add coverage for simulation create responses where D365 returns blank `WTC_ContactPartyNumber` but the submitted `ContactPartyNumber` identifies the contact party.
- [x] 3.5 Add coverage for customer-only simulation create responses where D365 returns blank `WTC_ContactPartyNumber` and the generic `PartyNumber` must not populate `ContactPartyNumber`.
- [x] 3.6 Add coverage that customer-only PartyContact updates map `CustomerPartyNumber` to both the OData `PartyNumber` key and `WTC_CustomerPartyNumber` payload context.

## 4. Validation

- [x] 4.1 Run the narrow test covering PartyContact response mapping.
- [x] 4.2 Run the functions build or nearest affected project build to confirm the orchestration compiles.
- [x] 4.3 Run `openspec status --change fix-partycontact-customer-party-number-response` and confirm the change is apply-ready.
- [x] 4.4 Build the Dashboard API project to confirm the simulation payload change compiles.
- [x] 4.5 Run focused PartyContact mapping tests after the update WTC party context fix.