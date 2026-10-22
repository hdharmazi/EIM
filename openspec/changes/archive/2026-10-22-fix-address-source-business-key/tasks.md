## 1. Implementation: make the key map testable

- [x] 1.1 In `ARM.Integration.EIM/Utilities/BusinessKeyResolver.cs`, change `EntityKeyFields` from `private static readonly` to `internal static readonly`. No behaviour change. Proven by the tests in 2.1 and 2.2 compiling.
- [x] 1.2 Add `<InternalsVisibleTo Include="ARM.Integration.EIM.Tests" />` to `ARM.Integration.EIM/ARM.Integration.EIM.csproj`, mirroring the existing entry in `ARM.Integration.EIM.Customer/ARM.Integration.EIM.Customer.csproj`.

## 2. Tests: detection before correction (must fail on current code)

- [x] 2.1 Add `ARM.Integration.EIM.Tests/BusinessKeyRegistrationTests.cs` with `EveryRoutedEntity_HasRegisteredBusinessKey`: every key in `QueueDataEventsHandler.EntityRoutes` must exist in `BusinessKeyResolver.EntityKeyFields`. On failure, the message lists the missing routed names and all registered names.
- [x] 2.2 In the same file, add `EveryRegisteredBusinessKey_IsRouted`, the reverse direction with the same failure message shape.
- [x] 2.3 Add one fixture per routed entity under `ARM.Integration.EIM.Tests/TestData/SourceEntities/<PrimaryEntityName>.json`:
  - `mserp_wtc_custpostaladdressv2entity.json`: a verbatim copy of `openspec/changes/fix-address-source-business-key/samples/CustomerAddress.OnExternalCreated.json`, the sanitized live create, with location id `000146087` and operation id `00000000-0000-0000-0000-000000000007`.
  - `mserp_custcustomerv3entity.json`, `mserp_smmcontactpersonv2entity.json`, `mserp_dirpartycontactv3entity.json`: the same envelope shape with each entity's own attributes, including its key attribute. Use sanitized values only.
  - The delete entity reuses the existing `TestData/CustomerAddressDeleted.OnExternalUpdated.json`, mapped by name in the test.
  - The existing `TestData\**\*.json` content glob already copies subfolders, so the csproj needs no change.
- [x] 2.4 In `BusinessKeyRegistrationTests.cs`, add `EveryRoutedEntity_ResolvesBusinessKeyFromFixture`. It iterates `QueueDataEventsHandler.EntityRoutes.Keys` (not a hand-written list), loads each entity's fixture, and asserts that `BusinessKeyResolver.GetBusinessKey(context.PrimaryEntityName, D365FSCEventParser.GetTargetAttributes(context.InputParameters))` is non-empty. A routed entity with no fixture fails and names the entity.
- [x] 2.5 Run `dotnet test ARM.Integration.EIM.Tests/ARM.Integration.EIM.Tests.csproj --filter BusinessKeyRegistrationTests` and confirm it fails exactly as follows: 2.1 names `mserp_wtc_custpostaladdressv2entity`, 2.2 names `mserp_custpostaladdressv2entity`, and 2.4 fails for the address entity only. If any other entity fails, stop and report back before section 3. The scope has changed.

## 3. Implementation: the correction and the fallback warning

- [x] 3.1 In `ARM.Integration.EIM/Utilities/BusinessKeyResolver.cs`, change the key `"mserp_custpostaladdressv2entity"` to `"mserp_wtc_custpostaladdressv2entity"`. Copy the string from `QueueDataEventsHandler.EntityRoutes`; do not retype it. Proven by 2.1, 2.2, 2.4 passing.
- [x] 3.2 In the same file, change the `GetBusinessKey` parameter to `Dictionary<string, object>? targetAttributes`. Return null when the dictionary is null, the entity is unregistered, the attribute is absent, or the trimmed value is null, empty, or whitespace (today a blank value returns `""`). Update the XML doc. Proven by 4.4 to 4.6.
- [x] 3.3 In the same file, add `public static string? GetKeyField(string entityName)`, returning the registered attribute name or null. Proven by 4.7.
- [x] 3.4 In `ARM.Integration.EIM.Customer/QueueDataEventsHandler.cs`, extract `internal static string ResolveSourceId(EventContext context, ILogger logger, string messageId)`. It parses the target attributes, calls `GetBusinessKey`, and returns the key. Otherwise it logs one Warning, `"Message {MessageId}: business key field {KeyField} is missing or blank for entity {Entity}; SourceId falls back to operation id {FallbackSourceId}."` (with `KeyField` from `GetKeyField`, or `(none registered)`), and returns `context.OperationId`. Replace the `targetAttrs`/`businessKey` lines and `SourceId = businessKey ?? context.OperationId` in `Run` with `SourceId = ResolveSourceId(context, logger, message.MessageId)`. Proven by 4.8 to 4.10.
- [x] 3.5 In the same file, update the `EntityRoutes` XML comment ("To add a new entity, add a single entry here"). It should say that a new routed entity also needs a `BusinessKeyResolver.EntityKeyFields` entry and a `TestData/SourceEntities` fixture, enforced by `BusinessKeyRegistrationTests`. Comment-only.
- [x] 3.6 Leave `EntityRequiredFields`, `CustomerAddress_To_Capture`, and `CustomerAddressDelete_To_Capture` unchanged, and do not add a pre-insert guard for the address create/update entity (design Decision 1). Bowser checks this against the diff.
- [x] 3.7 In `ARM.Integration.EIM/Utilities/D365FSCEventParser.cs`, make `GetTargetAttributes` return null when there is no `Target` input parameter (`if (target is null || target.Value.ValueKind == JsonValueKind.Undefined) return null;`) instead of throwing `NullReferenceException`. Mark the return type nullable and update the XML doc. Proven by 4.9 and 4.13.
- [x] 3.8 In the same method, also return null when the `Target` value is JSON `null` (`ValueKind is JsonValueKind.Undefined or JsonValueKind.Null`) and when the deserialized entity or its `Attributes` is null (`entity?.Attributes is null`). Proven by 4.14. (Bowser review, minor 1.)

## 4. Tests: the correction and the fallback

- [x] 4.1 Add `ARM.Integration.EIM.Tests/CustomerAddressSourceIdTests.cs` with `Create_SourceIdIsAddressLocationId_NotOperationId` and `Update_SourceIdIsAddressLocationId_NotOperationId`. Load the address fixture (setting `MessageName` to `OnExternalUpdated` for the update case), call `QueueDataEventsHandler.ResolveSourceId`, and assert `"000146087"` and not `context.OperationId`. (Spec: *A customer address creation/update is identified by its location*.)
- [x] 4.2 In the same file, add `ForwardedEnvelope_SourceIdEqualsAddressLocationId`. Pass the address fixture through a `CustomerAddress_To_Capture` test subclass (`ParseAndMap` + `CreateEnvelope`, as `CustomerAddressDeleteToCaptureTests` does) with the resolved key, and assert the envelope's `SourceId` equals `Data.AddressLocationId` equals `"000146087"`. (Spec: *The forwarded address message carries the same identifier*.)
- [x] 4.3 In the same file, add `DistinctOperationsOnOneAddress_ShareSourceId_ButNotDedupeKey`. Use three fixture variants with different `OperationId` and the same location id; assert identical `ResolveSourceId` results and distinct operation ids, which are the handler's `DedupeKey`. (Spec: *Repeated changes to one record share an identifier*.)
- [x] 4.4 In `BusinessKeyRegistrationTests.cs`, add `GetBusinessKey_NullTargetAttributes_ReturnsNull`, a theory over every `EntityRoutes` key, asserting null with no exception.
- [x] 4.5 In the same file, add `GetBusinessKey_BlankOrWhitespaceValue_ReturnsNull`, a theory over `""`, `"   "`, and a null value.
- [x] 4.6 In the same file, add `GetBusinessKey_UnregisteredEntity_ReturnsNull`. (Spec: *An unregistered entity yields no key rather than an error*.)
- [x] 4.7 In the same file, add `GetKeyField_ReturnsRegisteredAttribute`, asserting `mserp_addresslocationid` for `mserp_wtc_custpostaladdressv2entity` and null for an unregistered name.
- [x] 4.8 In `CustomerAddressSourceIdTests.cs`, add `MissingLocationId_FallsBackToOperationId_AndLogsWarning`. Remove `mserp_addresslocationid` from the address fixture, call `ResolveSourceId` with an in-test recording `ILogger`, and assert the operation id is returned and exactly one `LogLevel.Warning` entry names `mserp_wtc_custpostaladdressv2entity`, `mserp_addresslocationid`, and that operation id. Use no new package. (Spec: *A missing key falls back with a warning*.)
- [x] 4.9 In the same file, add `BlankLocationId_FallsBackToOperationId_AndLogsWarning` (value `""`), and `NoTargetParameter_FallsBackToOperationId_AndLogsWarning` (the `Target` input parameter removed). (Spec: *A blank key falls back…*, *An event without target attributes…*.)
- [x] 4.10 In the same file, add `ResolvedKey_LogsNoWarning`, asserting the recording logger has no Warning entries for the unmodified fixture. (Spec: *A resolved key logs no warning*.)
- [x] 4.11 Confirm the existing `CustomerAddressDeleteToCaptureTests`, `PartyContactToCaptureTests`, `IngestGateTests`, `HoldGateTests`, and `ReleaseSweepTests` pass unchanged. (Spec: *Other routed entities with a populated key are unaffected*.)
- [x] 4.12 Run `dotnet build ARM.Integration.Wilson.slnx`, `dotnet test ARM.Integration.EIM.Tests/ARM.Integration.EIM.Tests.csproj`, and `dotnet test ARM.Integration.D365FSC.Gateway.Tests/ARM.Integration.D365FSC.Gateway.Tests.csproj`. All green.
- [x] 4.13 Add `GetTargetAttributes_NoTargetParameter_ReturnsNull` and `GetTargetAttributes_TargetWithoutValue_ReturnsNull` next to the existing parser tests (or in `BusinessKeyRegistrationTests.cs` if there are none), asserting null and no exception. Confirm the first one fails with `NullReferenceException` before 3.7 is applied.
- [x] 4.14 Add `GetTargetAttributes_TargetValueNull_ReturnsNull` (`{"key":"Target","value":null}`) and `GetTargetAttributes_TargetWithoutAttributes_ReturnsNull` (Target entity object with no `Attributes`), plus `ResolveSourceId` tests for both shapes that assert the operation-id fallback with one warning. Confirm they fail before 3.8 is applied. (Bowser review, minor 1.)
- [x] 4.15 Add `ParseAndMap_NoTargetAttributes_ThrowsJsonException`: a Target-less address fixture through a `CustomerAddress_To_Capture` test subclass throws `JsonException`, and `DefaultFailureClassifier` classifies it as terminal `Rejected`. (Bowser review, minor 2.)

## 5. Deploy and verify (manual; not Luigi's)

- [x] 5.1 Before releasing to each environment (DEV2, BAT, TEST), query `ROUTETABLE` for `SOURCEENTITY = 'mserp_wtc_custpostaladdressdeletedv2entity'`. Record whether migration 014 has been applied and the row's `ENABLED`/`ONPAUSE`. With `ENABLED = 1` the delete route is live (`ONPAUSE` applies only when disabled). Informational only; it does not block this change.
- [x] 5.2 Deploy the Functions host to DEV2. No `ARM.Integration.DbMigrator` run is required.
- [x] 5.3 Trigger a real address create in F&SC, and confirm the persisted `SOURCEID` is the address location id and the message is searchable by it in the dashboard.
- [x] 5.4 Trigger a real update of the same address, and confirm it carries the same `SOURCEID` as the create.
- [x] 5.5 Confirm customer, contact, party contact, and (if 014 is applied) address delete messages still carry their business keys.
- [x] 5.6 Skipped. Redeliver an address event and confirm duplicate detection still recognises it (it keys on `DEDUPEKEY`, not `SourceId`).
- [x] 5.7 Query Application Insights for the fallback warning (`business key field … SourceId falls back to operation id`) over the first DEV2 cycle. Investigate any non-zero count by entity before promoting.
- [x] 5.8 Promote to BAT and TEST.
- [x] 5.9 In the release notes, record that no backfill is performed. If existing rows are retained, note that addresses ingested before the change are findable by operation id and those ingested after it by address location id.

> Deployed and verified 2026-10-21.
>
> **5.1:** 014 had not been applied anywhere. DEV, DEV2, BAT and TEST share one `eimdb`, so 014 could not be applied to BAT alone. Address deletions were dead-lettered with `NoRoute` until it was. The BAT and TEST rows were inserted paused (`ENABLED = 0`, `ONPAUSE = 'Hold'`) before 013 and 014 were applied. DEV and DEV2 are live, and BAT and TEST stay paused until Capture confirms delete handling.
>
> **5.2 and 5.8:** all three Function Apps (dev, bat, test) were deployed after PR 649.
>
> **5.6:** skipped. This change does not touch dedupe, which still keys on `DEDUPEKEY` (the operation id).
>
> **5.7:** zero fallback warnings in BAT and TEST App Insights after deploy, and zero GUID `SOURCEID`s on address rows. DEV has no App Insights connected and saw no address traffic.
>
> **5.9:** no backfill was performed.
