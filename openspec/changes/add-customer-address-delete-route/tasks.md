## 1. Engine: action override and skip reason

- [ ] 1.1 Add `protected virtual string? ForcedAction => null;` to `D365EventForwardOrchestration`, applied after the `MessageName` switch and before `MapFields` is invoked, so the `GetFieldValue` closure sees the forced action.
- [ ] 1.2 Widen the `ParseAndMap` tuple in `BaseForwardOrchestration` to `(string action, bool hasTrackedChanges, TDest mappedData, string? sourceTimestamp, string? skipReason)`.
- [ ] 1.3 In `BaseForwardOrchestration.ProcessAsync`, return without sending when `skipReason` is non-null, recording it via `stepLog.LogWarning`. Keep the existing `hasTrackedChanges = false` path at Information with its current `"not tracked fields"` result.
- [ ] 1.4 Update the two implementers — `D365EventForwardOrchestration` and `SamplePoll_To_Target` — to return `null` for `skipReason`, preserving current behaviour.
- [ ] 1.5 Add a `protected virtual string? GetSkipReason(EventContext context)` hook on `D365EventForwardOrchestration` returning `null` by default, so subclasses can veto a forward without reimplementing `ParseAndMap`.

## 2. Orchestration

- [ ] 2.1 Add `Wilson.Integration.Orchestrations/Orchestrations/CustomerAddressDelete_To_Capture.cs` as `class CustomerAddressDelete_To_Capture : CustomerAddress_To_Capture` with `ForcedAction => "Delete"`.
- [ ] 2.2 Override `GetSkipReason` to return a reason naming the observed value unless `mserp_wtc_isdeleted` resolves to `200000001`. Reuse the option-set decoding already present in `CustomerAddress_To_Capture`.
- [ ] 2.3 Add a one-line comment recording the assumption that both D365 address entities expose the same attribute set — it is what makes the inherited `MapFields` valid and what would force promotion to a shared base.
- [ ] 2.4 Register `["CustomerAddressDelete_To_Capture"] = new CustomerAddressDelete_To_Capture()` in `OrchestrationRegistry`.

## 3. Handler wiring

- [ ] 3.1 Add `["mserp_wtc_custpostaladdressdeletedv2entity"] = ("CustomerAddressDelete_To_Capture", "D365ToCaptureQueue")` to `QueueDataEventsHandler.EntityRoutes`. Copy the entity name from the captured payload; do not retype it.
- [ ] 3.2 Add `["mserp_wtc_custpostaladdressdeletedv2entity"] = "mserp_addresslocationid"` to `BusinessKeyResolver.EntityKeyFields`.
- [ ] 3.3 Add `["mserp_wtc_custpostaladdressdeletedv2entity"] = "mserp_addresslocationid"` to `QueueDataEventsHandler.EntityRequiredFields`, so a blank key is rejected before the `SourceId` fallback to `OperationId` can occur.
- [ ] 3.4 Confirm no entry is added to `TrackedFieldsByEntity` for the new entity, and that `CustomerAddress_To_Capture` is unmodified.
- [ ] 3.5 Leave the existing `["mserp_custpostaladdressv2entity"]` entry in `BusinessKeyResolver` untouched — correcting its missing `wtc_` prefix is a separate change with its own deploy gate.

## 4. Database

- [ ] 4.1 Verified: `MESSAGEQUEUETABLE.SOURCEENTITY`, `ROUTETABLE.SOURCEENTITY`, and `SPINSERTMESSAGEQUEUE.@SOURCEENTITY` are all `NVARCHAR(50)`; the 42-character entity name fits. Re-confirm against the target database before seeding.
- [ ] 4.2 Add `script/SQL/Migrations/014_seed_customer_address_delete_route.sql` inserting one route per environment (BAT, DEV, DEV2, TEST) with `ORCHESTRATIONTYPE = 'CustomerAddressDelete_To_Capture'`, `ENABLED = 1`, `ONPAUSE = 'Hold'`, using a `MERGE ... WHEN NOT MATCHED` so re-running is safe.
- [ ] 4.3 Add a parity check to 014 that throws unless exactly four new rows exist for the new source entity — silent under-seeding dead-letters in one environment only.
- [ ] 4.4 Confirm `006_seed_routetable.sql` needs no edit: it is journaled in `dbo.SchemaVersions`, so its `@routes INT = 9` tripwire and its orchestration-type allowlist do not re-run, and on a fresh database it still executes before 014.

## 5. Dashboard

- [ ] 5.1 Add `mserp_wtc_custpostaladdressdeletedv2entity: "address"` to `entityStem` in `ARM.Integration.Dashboard.Frontend/src/features/routes/api.ts`, so pausing either address route warns about the other.

## 6. Tests

- [ ] 6.1 Check in the captured delete payload as a test fixture under `ARM.Integration.EIM.Tests/TestData/`.
- [ ] 6.2 Assert that the fixture, whose message name is `OnExternalUpdated`, produces an envelope with `Action = "Delete"` and `Entity = "CustomerAddress"`.
- [ ] 6.3 Assert the mapped data matches the expected Capture shape field for field, including `IsPrimary` decoded from its option set and the `/Date(...)/` creation timestamp taken from `OperationCreatedOn`.
- [ ] 6.4 Assert `SourceId` equals the address location id and not the operation id.
- [ ] 6.5 Assert a payload with `mserp_wtc_isdeleted` set to `200000000` is not forwarded and yields a skip reason naming the observed value.
- [ ] 6.6 Assert a payload with a blank `mserp_addresslocationid` is rejected by the handler before insert.
- [ ] 6.7 Assert that the same fixture with message name `OnExternalCreated` and with `OnExternalDeleted` both still yield `Action = "Delete"`.
- [ ] 6.8 Assert the entity name string in `EntityRoutes` matches the `SOURCEENTITY` value seeded by migration 014, so a typo cannot produce a silent drop in one place and a dead letter in the other.
- [ ] 6.9 Assert existing `CustomerAddress_To_Capture` tests still pass unchanged, and that `RouteRegistryValidator` accepts the new route/orchestration pair.
- [ ] 6.10 Run the full solution build and test suite.

## 7. Rollout

- [ ] 7.1 Record the deploy order in the runbook: deploy the Functions host **before** running migration 014 in each environment. A route row present while a host without the registration restarts fails `RouteRegistryValidator` at startup — an outage, not a degraded route.
- [ ] 7.2 Confirm with the Capture team that a key-shaped `SourceId` is accepted on the delete message. Blocks enabling in any shared environment; sent messages cannot be recalled.
- [ ] 7.3 Confirm with the Capture team whether the full address body is required on a `Delete` or only the key fields.
- [ ] 7.4 Deploy to DEV2, trigger a real address deletion in F&SC, and verify the Capture envelope end to end.
- [ ] 7.5 Watch for warning-severity skips from the `mserp_wtc_isdeleted` guard over the first cycle — a non-zero count answers whether the flag can ever be `No` and reopens the undelete question.
- [ ] 7.6 Promote through BAT and TEST following the same deploy-then-migrate order.
