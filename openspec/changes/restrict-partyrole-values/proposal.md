## Why

`WTC_PartyRole` on `DirPartyContactV3Entity` can return `Both` when a party is both a customer and a contact person. Wilson treats party contacts as either customer or contact, and in DEV no party has both roles (68,617 organization customers, 1 person customer, 3 contact persons, 0 overlap). Allowing a fourth value means the integration and spec carry a branch for data that does not occur. Limiting the field to `Customer`, `Contact`, or blank keeps D365, the integration, and the spec aligned.

## What Changes

- **D365 (WTC model)**: `wtcPartyRole` no longer returns `Both`. When a party is both a customer and a contact person, `Customer` takes precedence. The field returns only `Customer`, `Contact`, or blank.
- **Integration (`PartyContact_To_Capture`)**: remove the `Both` handling; `Customer` maps to `CustomerPartyNumber`, `Contact` maps to `ContactPartyNumber`.
- **Integration (`QueueDataEventsHandler`)**: unchanged. Blank roles are already filtered out before EIM insert.
- **BREAKING (edge case only)**: a party with both roles is now sent to Capture as a customer contact only (`ContactPartyNumber = ""`) instead of populating both party numbers.

## Capabilities

### New Capabilities
<!-- None -->

### Modified Capabilities
- `partycontact-data-event-capture`: the party role value set changes from `Customer` / `Contact` / `Both` / blank to `Customer` / `Contact` / blank, with `Customer` precedence; the forwarding mapping drops `Both`.

## Impact

- D365 metadata (`c:\Custom\Metadata`): `WTC/WTC/AxClass/WTC_DirPartyContactV3Entity_Extension.xml` (`wtcPartyRole` CASE expression). Requires WTC build, DB sync, and deployment; no Dataverse virtual entity refresh needed (column unchanged).
- Integration code: [Wilson.Integration.Orchestrations/Orchestrations/PartyContact_To_Capture.cs](../../../Wilson.Integration.Orchestrations/Orchestrations/PartyContact_To_Capture.cs), [ARM.Integration.EIM.Tests/PartyContactToCaptureTests.cs](../../../ARM.Integration.EIM.Tests/PartyContactToCaptureTests.cs).
- No change to routes, queues, business keys, the handler filter, or the Capture message contract.
