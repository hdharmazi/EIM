## Why

Bug #5261 ("1197 - Contact Person Integration Update"): when a contact person is updated in D365 F&SC, the message `Contact_To_Capture` sends to Capture contains only the fields that changed plus the forced keys. Unchanged and empty name fields (`MiddleName`, `LastName`, and also `dataAreaId`) are dropped, so Capture cannot apply the update as a full record and cannot tell "unchanged" from "cleared". Capture needs the same complete `Data` object on `Update` as on `New`, with empty values sent as `""`.

Example from the bug: an `Update` sends only `ContactPersonId`, `FirstName`, `AssociatedPartyNumber`, `AssociatedCustomerAccount`, `ContactPersonPartyNumber`. The `New` message for the same contact also includes `dataAreaId`, `MiddleName: ""`, and `LastName: ""`.

## What Changes

- `Contact_To_Capture` maps every contract field it populates from the event's `Target` on every action, instead of only the changed fields on `Update`. This matches `Customer_To_Capture` and `CustomerAddress_To_Capture`.
- `dataAreaId`, `FirstName`, `MiddleName`, and `LastName` are always present in the Capture `Data` object. A missing or null source value is sent as `""`, not omitted.
- Update filtering is unchanged. An `OnExternalUpdated` event is still forwarded only when a tracked field changed (`DataEventProcessor.TrackedFieldsByEntity` and the orchestration's `hasTrackedChanges`).

## Non-goals

- No change to the Capture → D365 direction (`Contact_To_D365FSC`).
- No change to `Customer_To_Capture`, `CustomerAddress_To_Capture`, or `PartyContact_To_Capture`, because they already send full payloads.
- No change to the engine (`D365EventForwardOrchestration` / `BaseForwardOrchestration`) or its `getField` change-tracking semantics.
- `AssociatedContactNumber` and `AssociatedContactType` on `CaptureContactMessageContract` stay unmapped, and stay omitted as they are on `New` today.
- No D365 (WTC model), Dataverse, SQL, route, or deployment-config change.
- No replay of contact updates already sent with partial payloads.

## Capabilities

### New Capabilities
- `contact-data-event-capture`: How D365 `ContactPersonV2Entity` data events are mapped and forwarded to Capture by `Contact_To_Capture`, including the full payload on update.

### Modified Capabilities
None.

## Impact

- Code: `Wilson.Integration.Orchestrations/Orchestrations/Contact_To_Capture.cs` (mapping only).
- Tests: new `Wilson.Integration.Tests/ContactToCaptureTests.cs`.
- Capture: the contact `Update` messages on `D365ToCaptureQueue` gain `dataAreaId`, `FirstName`, `MiddleName`, and `LastName`, with `""` for empty values. The JSON shape is unchanged and additive, matching the existing `New` message.
- Deployment: normal `eim` pipeline (BAT → TEST), then `eim-prd`. No migration and no config change.
