## 1. Implementation

- [x] 1.1 Add `MissingEntityKeyException` (with `KeyField`) to `ARM.Integration.Core/Helpers`
- [x] 1.2 Make `EntityKey.Create` / `Add` throw `MissingEntityKeyException` for null or whitespace values
- [x] 1.3 Classify `MissingEntityKeyException` as positively terminal in `DefaultFailureClassifier`

## 2. Tests

- [x] 2.1 `PartyContact_To_D365FSC` key with null `ElectronicAddressId` throws `MissingEntityKeyException` naming `ElectronicAddressId`
- [x] 2.2 Whitespace key value throws `MissingEntityKeyException`
- [x] 2.3 `FailureClassifierTests`: `MissingEntityKeyException` is terminal with reason `Rejected`, and `FailureOutcomeSelector` does not append `(unclassified failure)`
- [x] 2.4 Run `dotnet test ARM.Integration.EIM.Tests` and confirm all tests pass

## 3. Deploy and verify

- [x] 3.1 Deploy the function app and the Dashboard API to BAT
- [x] 3.2 Reprocess EIM 78782 and confirm it fails with `Required key field 'ElectronicAddressId' is missing from the source message.` and no `(unclassified failure)` suffix
