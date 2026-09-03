## Why

Capture-to-D365 PartyContact create responses can return an incorrect `CustomerPartyNumber` because the response mapping reads the generic D365 `PartyNumber` instead of the custom party relationship fields returned by `PartyContactsV3`. Capture needs the response envelope to map D365 `WTC_CustomerPartyNumber` to `CustomerPartyNumber` and `WTC_ContactPartyNumber` to `ContactPartyNumber`.

## What Changes

- Add the D365 `WTC_ContactPartyNumber` and `WTC_CustomerPartyNumber` fields to the PartyContact D365 entity model.
- Correct the PartyContact Capture-to-D365 response mapping so `ContactPartyNumber` is sourced from D365 `WTC_ContactPartyNumber` and `CustomerPartyNumber` is sourced from D365 `WTC_CustomerPartyNumber`.
- Keep request-side mapping to D365 `PartyNumber`: source `ContactPartyNumber` maps to `PartyNumber` when present, otherwise source `CustomerPartyNumber` maps to `PartyNumber`.
- Map supplied Capture party-number fields into D365 `WTC_ContactPartyNumber` / `WTC_CustomerPartyNumber` so update payloads carry the same party context as the key.
- Correct PartyContact create simulation so entering only one party-number field does not populate the other with a default value.
- Add focused test coverage for the PartyContact `New` response contract using the reported D365 response shape.
- No breaking changes to queue names, entity names, or Capture request payload format.

## Capabilities

### New Capabilities
- `partycontact-capture-response`: Defines the Capture response contract for PartyContact acknowledgements after D365 CRUD processing.

### Modified Capabilities
<!-- No existing spec capabilities are modified. -->

## Impact

- `Wilson.Integration.Orchestrations/Orchestrations/PartyContact_To_D365FSC.cs` response data construction.
- `Wilson.Integration.Orchestrations/Contracts/CapturePartyContactMessageContract.cs` usage semantics for PartyContact responses.
- `ARM.Integration.EIM/Model/D365FSC/PartyContactsEntity.cs` D365 response model fields.
- `ARM.Integration.Dashboard.API/Services/SimulationService.cs` PartyContact create simulation payload construction.
- Focused orchestration tests in `ARM.Integration.EIM.Tests` or the nearest existing test project covering D365 CRUD response mapping.