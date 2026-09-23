## 1. Detection before correction

- [ ] 1.1 Add a test asserting every key in `QueueDataEventsHandler.EntityRoutes` has a corresponding entry in `BusinessKeyResolver.EntityKeyFields`, naming both spellings on failure.
- [ ] 1.2 Confirm the test fails on the current code, identifying `mserp_wtc_custpostaladdressv2entity` as unregistered. A test that passes before the fix is testing the wrong thing.
- [ ] 1.3 Assert the reverse direction too — a registered key naming an entity that is not routable is equally a drift signal.
- [ ] 1.4 Check the result for entities other than the address one. If a second entity is mis-registered, stop and reassess scope before task 3.1.

## 2. Verify the registered attributes exist

- [ ] 2.1 Check in a representative payload fixture per routed entity, if not already present under `ARM.Integration.EIM.Tests/TestData/`.
- [ ] 2.2 Assert `BusinessKeyResolver.GetBusinessKey` returns a non-empty value for each entity against its fixture. Registration alone does not prove the attribute is populated — both failure modes return null.
- [ ] 2.3 Confirm `mserp_electronicaddressid` resolves for `mserp_dirpartycontactv3entity`; it is read with `force: true` in `PartyContact_To_Capture.MapFields`, so it should be present.

## 3. The correction

- [ ] 3.1 Change the `BusinessKeyResolver.EntityKeyFields` key from `mserp_custpostaladdressv2entity` to `mserp_wtc_custpostaladdressv2entity`.
- [ ] 3.2 Confirm the task 1.1 test now passes.
- [ ] 3.3 Assert `SourceId` on the `CustomerAddress` envelope equals the address location id for both a create and an update fixture.
- [ ] 3.4 Assert several distinct change events for one address all produce the same `SourceId`.
- [ ] 3.5 Leave the `?? context.OperationId` fallback in place — it remains correct for an entity with no natural business key.
- [ ] 3.6 Run the full solution build and test suite.

## 4. Deploy gate — blocks everything below

- [ ] 4.1 Confirm with the Capture team whether `SourceId` is read on the `CustomerAddress` envelope, and whether it is stored, correlated, or deduplicated on.
- [ ] 4.2 Confirm Capture absorbs a key-shaped `SourceId` without orphaning existing correlations or creating duplicates.
- [ ] 4.3 If `add-customer-address-delete-route` has shipped, cite its delete-route evidence — that route already emits a key-shaped `SourceId` with no legacy consumer.
- [ ] 4.4 Record the outcome in the change before deploying. Sent messages cannot be recalled.

## 5. Verification

- [ ] 5.1 Deploy to DEV2 only.
- [ ] 5.2 Trigger a real address create in F&SC; confirm the persisted `SOURCEID` and the forwarded envelope both carry the address location id.
- [ ] 5.3 Trigger a real address update; confirm it carries the same `SourceId` as the create.
- [ ] 5.4 Confirm customer, contact, and party contact messages are unchanged.
- [ ] 5.5 Confirm duplicate detection still behaves correctly — it keys on `DEDUPEKEY`, not `SourceId`, so a redelivery should still be recognised.
- [ ] 5.6 Promote to BAT and TEST only after Capture confirms clean absorption.

## 6. Release notes

- [ ] 6.1 Record that no backfill is performed. If existing rows are retained rather than truncated, note that addresses ingested before the change are findable by operation id and after the change by address location id.
