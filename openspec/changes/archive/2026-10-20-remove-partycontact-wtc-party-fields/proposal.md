## Why

Capture → D365 PartyContact creates and updates fail with `The property 'WTC_ContactPartyNumber' does not exist on type 'Microsoft.Dynamics.DataEntities.PartyContactV3'`. The D365 `DirPartyContactV3Entity` extension replaced the unmapped `WTC_ContactPartyNumber` / `WTC_CustomerPartyNumber` fields with the computed `WTC_PartyRole` (see `fix-partycontact-data-event-capture`), but `PartyContact_To_D365FSC` still posts both fields. Every environment that has the new D365 entity rejects every PartyContact message from Capture.

## What Changes

- **BREAKING (D365 contract)**: `PartyContact_To_D365FSC` stops sending `WTC_ContactPartyNumber` and `WTC_CustomerPartyNumber` on create and update. D365 receives only the standard `PartyNumber` (resolved from `ContactPartyNumber`, falling back to `CustomerPartyNumber`) plus the electronic address fields.
- Remove `WTC_ContactPartyNumber` / `WTC_CustomerPartyNumber` from `PartyContactsEntity`.
- Capture success responses take `ContactPartyNumber` / `CustomerPartyNumber` from the submitted Capture message instead of D365 `WTC_*` fields (those values were an echo of the request anyway). D365-owned fields (`ElectronicAddressId`, `Locator`, `LocationId`, …) still come from the D365 result.
- No change to how `PartyNumber` is resolved or to the OData key (`PartyNumber` + `ElectronicAddressId`).
- Optional D365 cleanup: delete the now-empty `validateWrite` / `wtcResolvePartyNumber` from `WTC_DirPartyContactV3Entity_Extension`.

## Capabilities

### New Capabilities
<!-- None -->

### Modified Capabilities
- `partycontact-capture-response`: response party numbers come from the Capture request, not D365 `WTC_*` fields; D365 requests no longer carry `WTC_*PartyNumber` fields.

## Impact

- Integration code: [ARM.Integration.EIM/Model/D365FSC/PartyContactsEntity.cs](../../../ARM.Integration.EIM/Model/D365FSC/PartyContactsEntity.cs), [Wilson.Integration.Orchestrations/Orchestrations/PartyContact_To_D365FSC.cs](../../../Wilson.Integration.Orchestrations/Orchestrations/PartyContact_To_D365FSC.cs), [ARM.Integration.EIM.Tests/D365UpdatePayloadBuilderTests.cs](../../../ARM.Integration.EIM.Tests/D365UpdatePayloadBuilderTests.cs).
- D365 (optional): `WTC/WTC/AxClass/WTC_DirPartyContactV3Entity_Extension.xml` in `c:\Custom\Metadata`.
- Hotfix priority: deploy to every environment that already has the `WTC_PartyRole` D365 entity.
- No change to the D365 → Capture direction, routes, queues, or the Capture message contract.
