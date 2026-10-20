## Context

`D365CrudOrchestration` builds an `EntityKey` from the mapped D365 entity for Update and Delete. `EntityKey.ToString()` escapes each value with `Uri.EscapeDataString`, which throws `ArgumentNullException` for null. `DefaultFailureClassifier` does not recognise that exception, so the message is recorded as `(unclassified failure)` with an error-level log, as seen for BAT EIM 78782 (PartyContact Update with no `ElectronicAddressId`).

## Goals / Non-Goals

**Goals:**
- A missing key fails before D365 is called, with a message naming the field.
- It is recorded as a positively terminal rejection, not an unclassified defect.

**Non-Goals:**
- Recovering the key (looking up the record by locator, or converting the update to a create).
- Validating non-key required fields.

## Decisions

### 1. Validate in `EntityKey`, not in each orchestration
`EntityKey.Create` / `Add` throw `MissingEntityKeyException(keyField)` for null or whitespace values. Every orchestration's `BuildEntityKey` gets the check without per-entity code, and the failure happens when the key is built, before the update payload or HTTP call.

- *Alternative — validate in `PartyContact_To_D365FSC`*: rejected; Customer, CustomerAddress, and Contact have the same exposure.
- *Alternative — validate in `D365CrudOrchestration`*: would need key-field metadata per entity, duplicating what `BuildEntityKey` already knows.

### 2. Classify as positively terminal
`DefaultFailureClassifier.IsPositivelyTerminal` returns true for `MissingEntityKeyException`, alongside `JsonException`. The source message is unusable until corrected, so retrying cannot help, and it is a data problem, not a defect.

### 3. Exception lives in ARM.Integration.Core
`EntityKey` is in `ARM.Integration.Core.Helpers`, which `ARM.Integration.EIM` already references for `D365FSCException`; the new exception sits next to `EntityKey`.

## Risks / Trade-offs

- [A caller relied on building a key with an empty value] → Only `BuildEntityKey` implementations call `EntityKey`, and all are used for Update/Delete where an empty key is never valid.
- [Dashboard reprocess runs its own copy of the orchestrations] → Deploy the Dashboard API with the function app, otherwise reprocess keeps reporting the old unclassified error.
