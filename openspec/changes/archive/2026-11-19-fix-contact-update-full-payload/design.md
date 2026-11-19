## Context

Flow: D365 data event `mserp_smmcontactpersonv2entity` → `d365fsc-dataevent` queue → `DataEventProcessor` (route `Contact_To_Capture`, tracked-field and required-field filters) → EIM message → `Contact_To_Capture` (`D365EventForwardOrchestration<CaptureContactMessageContract>`) → `CaptureMessageEnvelope` on `D365ToCaptureQueue`.

`D365EventForwardOrchestration.ParseAndMap` passes `MapFields` a `getField(name, force)` delegate. On `Update`, a non-forced field that is not in `ChangedFields` returns `null`. Every property of `CaptureContactMessageContract` has `[JsonIgnore(Condition = WhenWritingNull)]`, so null fields disappear from the JSON.

`Contact_To_Capture.MapFields` calls `getField(..., false)` for `mserp_dataareaid`, `mserp_firstname`, `mserp_lastname`, and `mserp_middlename`. On an update that changes only the first name, the other three are therefore dropped (bug #5261). The keys and party numbers use `force: true` and are always sent. In the bug sample, `AssociatedPartyNumber` was unchanged and was still sent with its value, which shows that the `Target` of an `OnExternalUpdated` event carries the full record and not only the changed attributes.

`Customer_To_Capture` and `CustomerAddress_To_Capture` already force every field. `Contact_To_Capture` is the only `*_To_Capture` orchestration with non-forced fields.

## Goals / Non-Goals

**Goals:**
- Contact `Update` messages to Capture carry the same `Data` fields as `New`.
- Empty name fields are sent as `""`, not omitted, on every action.
- Existing forward/skip behaviour for updates is unchanged.

**Non-Goals:** see proposal. In particular, there is no change to the engine's change-tracking contract.

## Decisions

### D1. Force every mapped field in `Contact_To_Capture`
Change the four `getField(..., false)` calls to `getField(..., true)`.

`flagTrack` (→ `hasTrackedChanges`) is set only when a field is read on a non-`Update` action or is in `ChangedFields`. Forcing a field returns its value without setting `flagTrack`, so an update whose changed fields include none of the mapped fields is still completed as `not tracked fields`. `DataEventProcessor.TrackedFieldsByEntity` filters most such updates earlier, but it also tracks `mserp_associatedcontactnumber`, which this orchestration doesn't map. An update that changes only that field reaches the orchestration, and the `hasTrackedChanges` check is what stops it. This is the same pattern as `Customer_To_Capture` and `CustomerAddress_To_Capture`.

*Alternative rejected:* add an engine option such as `SendFullPayloadOnUpdate`. It adds engine surface for a one-line orchestration fix, and the other two forward orchestrations already solve it the same way.

### D2. Coalesce absent name and company values to `""`
Map `DataAreaId`, `FirstName`, `MiddleName`, and `LastName` as `getField(..., true) ?? ""`. Dataverse can omit an attribute whose value is null. Without the coalesce, the field would still disappear from the JSON in that case. The bug asks for every payload field "even though it's empty". The key fields (`ContactPersonId`, `AssociatedCustomerAccount`, `AssociatedPartyNumber`, `ContactPersonPartyNumber`) keep their current behaviour (forced, no coalesce): `AssociatedCustomerAccount` is already required non-blank by `DataEventProcessor.EntityRequiredFields`, and changing how a missing key looks to Capture is outside this bug.

*Alternative rejected:* remove `WhenWritingNull` from `CaptureContactMessageContract`. That would emit `null` (not `""`) and would also start emitting the unmapped `AssociatedContactNumber`/`AssociatedContactType` as `null`, which changes the `New` shape that Capture already accepts.

### D3. No contract, schema, or route change
The JSON property names and envelope are unchanged. The change only makes `Update` messages contain the fields that `New` messages already contain.

## Failure behaviour

- This is a pure mapping change. It adds no exception type, so `DefaultFailureClassifier` is untouched. Parse failures behave as before (terminal `JsonException`).
- Retries, idempotency, and ordering are unchanged. A retried or reprocessed message re-maps from the stored event payload and now produces the full payload, which is deterministic.
- Reprocessing an old contact update from the dashboard after deployment sends the full payload. Values come from that event's `Target`, so an old message reprocessed out of order can overwrite newer Capture data with older names. This is already true today for the forced fields, and reprocessing is a manual operator action.

## Risks / Trade-offs

- [Capture treats `""` as "clear this field"] → This is the intended behaviour per the bug: D365 is the source of truth, and `New` already sends `""`. Confirm in BAT (task 3.2).
- [A D365 `Target` that omits unchanged attributes would send `""` and blank a name in Capture] → The bug sample shows an unchanged forced field carrying its value, and Customer/Address already rely on this. BAT verification (task 3.2) checks that an update changing only `FirstName` still carries the real `LastName`.

## Migration Plan

No schema or config change. The code deploys through `eim` (BAT, TEST) and then `eim-prd`. To roll back, redeploy the previous package; messages already sent are unaffected.
