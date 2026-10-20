## Why

A Capture `PartyContact` Update without `ElectronicAddressId` (BAT EIM 78782, SourceId 3753) fails with `Value cannot be null. (Parameter 'stringToEscape') (unclassified failure)`. The OData key is built from a null value and `Uri.EscapeDataString` throws before any call to D365. The message reads like a system defect, is recorded as unclassified, and gives the Capture team nothing to act on. The same happens for any D365 update or delete whose key field is missing.

## What Changes

- `EntityKey` rejects a null or blank key value with a `MissingEntityKeyException` naming the key field, e.g. `Required key field 'ElectronicAddressId' is missing from the source message.`
- The failure classifier treats `MissingEntityKeyException` as a positively terminal rejection, so the message is recorded at status `3` without the `(unclassified failure)` suffix and without an error-level log.
- Applies to every D365 CRUD orchestration that builds a key (Customer, CustomerAddress, Contact, PartyContact) on Update and Delete. Create is unaffected.

## Capabilities

### New Capabilities
- `d365-entity-key-validation`: D365 update and delete requests require every OData key field to be present in the source message.

### Modified Capabilities
- `transient-failure-retry`: a missing entity key is a known terminal condition.

## Impact

- Code: [ARM.Integration.Core/Helpers/EntityKey.cs](../../../ARM.Integration.Core/Helpers/EntityKey.cs), new `MissingEntityKeyException` in `ARM.Integration.Core/Helpers`, [ARM.Integration.EIM/Retry/DefaultFailureClassifier.cs](../../../ARM.Integration.EIM/Retry/DefaultFailureClassifier.cs).
- Tests: `FailureClassifierTests`, `D365UpdatePayloadBuilderTests`.
- Function app and Dashboard API both compile the orchestrations; deploy both so dashboard reprocess reports the same message.
- No change to message contracts, routes, or the D365 requests that do have complete keys.
