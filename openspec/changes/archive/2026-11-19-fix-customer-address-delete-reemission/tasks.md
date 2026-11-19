## 1. Implementation: integration safety net (Luigi)

- [x] 1.1 In `ARM.Integration.EIM/Utilities/D365FSCEventParser.cs`, make `GetChangedFields` return an empty list when there is no `ChangedFields` parameter (`FirstOrDefault` returns null) or when its value is `Undefined` or JSON `null`. It must never throw. Do not touch `GetChangeAttributes`. Proven by `ChangedFields_MissingParameter_ReturnsEmpty` (task 2.4)
- [x] 1.2 In `Wilson.Integration.Automation/Outbound/DataEventProcessor.cs`, add the `TrackedFieldsByEntity` entry `["mserp_wtc_custpostaladdressdeletedv2entity"] = { "mserp_wtc_isdeleted" }` with the comment from design D5. Proven by tasks 2.2 and 2.3
- [x] 1.3 In the same file, extract the inline tracked-field block into `internal static bool HasTrackedChange(EventContext context, out IReadOnlyList<string> changedFields)`.
  - It returns true when the message is not `OnExternalUpdated`, when the entity has no tracked set, or when a tracked field is changed.
  - Call it from `ProcessAsync` at the same position (after routing, before the required-field checks).
  - Change the skip log to `"Message {id} has no tracked field changes, skipping. Entity={entity}, Event={eventName}, ChangedFields=[{changedFields}]"` at `Information`. Log at `Warning` instead when the event has no `ChangedFields` parameter at all (design D7).
  - Update the class and field XML docs to match.
  - Proven by tasks 2.2–2.5
- [x] 1.4 Confirm that `CustomerAddressDelete_To_Capture` and `CustomerAddress_To_Capture` need no change, since the filter sits before the insert. No edit is expected; note the result in your task report

## 2. Tests: integration (Luigi)

- [x] 2.1 Add the fixture `Wilson.Integration.Tests/TestData/CustomerAddressDeleted.Reemission.OnExternalUpdated.json`, an anonymised copy of eimdb message 78833. Do not put it under `TestData/SourceEntities`.
  - Anonymise as in `TestData/CustomerAddressDeleted.OnExternalUpdated.json`: zero GUIDs and a placeholder operation id.
  - Keep location `000311920` and customer `100300`.
  - Keep Target street `222 Main street No. 2` and PreImage street `222 Main street`, both with `mserp_wtc_isdeleted` `OptionSetValue 200000001`.
  - Keep `ChangedFields = ["mserp_addressstreet","mserp_addressvalidfrom","mserp_addressvalidto","mserp_formatteddeliveryaddress"]`.
- [x] 2.2 Add `Wilson.Integration.Tests/CustomerAddressDeleteReemissionTests.cs`. `HasTrackedChange` must be `true` for:
  - the existing genuine fixture `CustomerAddressDeleted.OnExternalUpdated.json` (`ChangedFields = ["mserp_wtc_isdeleted"]`)
  - a variant shaped like 78800, whose `ChangedFields` lists every address field plus `mserp_wtc_isdeleted`, with `PreImage` `null`

  Covers spec *The deletion itself is forwarded* and *An update-shaped event yields a delete*
- [x] 2.3 In the same class:
  - `HasTrackedChange` is `false` for the re-emission fixture (2.1), and for variants with `ChangedFields` `[]` and `["mserp_addresslocationroles"]`.
  - `HasTrackedChange` is `true` for the re-emission fixture with `MessageName` set to `OnExternalCreated`.
  - Add a handler test that follows `CustomerAddressDeleteToCaptureTests.Handler_RejectsBlankAddressLocationId_BeforeInsert` (null collaborators). It runs `QueueDataEventsHandler.Run` on the re-emission fixture and asserts that the message is completed without reaching the insert path.

  Covers *A customer edit re-notifies a deleted address*, *Any update without the deleted flag among the changed fields is withheld*, and *Any other message name yields a delete*
- [x] 2.4 Test `D365FSCEventParser.GetChangedFields` for a missing parameter and a missing-changed-fields handler path:
  - Add `ChangedFields_MissingParameter_ReturnsEmpty`, plus a case where the value is JSON `null`. Put them in `Wilson.Integration.Tests` next to 2.2 (EIM internals are visible there) or in a new `ARM.Integration.EIM.Tests/D365FSCEventParserTests.cs`.
  - Add a handler test that removes the `ChangedFields` parameter from the re-emission fixture. It completes the message and records a `Warning` that names the entity. Capture the logs with a recording `ILoggerFactory` (see `RecordingLogger` in `CustomerAddressSourceIdTests`).

  Covers *Missing changed-field list is withheld with a warning*
- [x] 2.5 Add a handler test with a recording logger on the re-emission fixture. It expects one `Information` entry that contains `mserp_wtc_custpostaladdressdeletedv2entity`, `OnExternalUpdated` and `mserp_addressvalidfrom`. Covers *Withheld re-notifications are logged for diagnosis*
- [x] 2.6 Test `HasTrackedChange` on `TestData/SourceEntities/mserp_wtc_custpostaladdressv2entity.json` with `MessageName = OnExternalUpdated`:
  - `true` with `ChangedFields = ["mserp_addressstreet"]`
  - `false` with `ChangedFields = ["mserp_addressvalidto"]` (today's behaviour)

  Covers *The create/update address route is unaffected*
- [x] 2.7 Confirm that the existing `CustomerAddressDeleteToCaptureTests` still pass unchanged, including `AnyMessageName_YieldsDelete` and `MapsEveryAddressField_EvenWhenChangedFieldsOmitsThem`. Covers *Changed-field tracking does not suppress fields*
- [x] 2.8 Run these and make sure all are green:
  - `dotnet build ARM.Integration.Wilson.slnx`
  - `dotnet test ARM.Integration.EIM.Tests/ARM.Integration.EIM.Tests.csproj`
  - `dotnet test Wilson.Integration.Tests/Wilson.Integration.Tests.csproj`

## 3. Implementation: D365 F&SC, WTC model in `C:\Custom` (Luigi authors; the build is unverified on this host)

There is no X++ compiler or DB sync on this machine. Luigi authors the metadata XML following `C:\Custom\.github\copilot-instructions.md` (naming, labels, Allman braces, `///` docs, never edit `XppSource`/`XppMetadata`/`bin`). Compilation and sync happen in group 4.

- [x] 3.1 *(Peach)* Ticket **#5236**. Work on the existing `C:\Custom` branch `bug/5236/customer-address-update-action`, with no new branch. Its uncommitted #5236 edits must be kept: `PostalAddress.rnrproj` and `WTC_1197_CustomerAddressDelete.rnrproj` move `WTC_CustPostalAddressV2Entity` between projects, and `BuildProjectResult.xml` is a build output that must not be touched
- [x] 3.2 Add field `WTC_DeletedPostalAddress` to `Metadata/WTC/WTC/AxTableExtension/LogisticsLocation.WTC.xml`:
  - `AxTableFieldInt64`, `ExtendedDataType = LogisticsPostalAddressRecId`
  - a new `@WTC:` label and help text, added to the WTC label file the model already uses
  - mirror the existing `WTC_IsDeleted` field's XML shape

  Design D1
- [x] 3.3 In `Metadata/WTC/WTC/AxClass/WTC_LogisticsPostalAddressTable_Extension.xml`, inside the existing `if (logisticsLocation.RecId && logisticsLocation.WTC_IsDeleted != NoYes::Yes)` block, set `logisticsLocation.WTC_DeletedPostalAddress = this.RecId;` before the single `update()`. Update the `///` doc. Design D1
- [x] 3.4 In `Metadata/WTC/WTC/AxDataEntityView/WTC_CustPostalAddressDeletedV2Entity.xml`, on the `LogisticsPostalAddress` data source (relations around lines 1131–1144), keep only `QueryDataSourceRelation2` (`Field = WTC_DeletedPostalAddress`, `JoinDataSource = LogisticsLocation`, `RelatedField = RecId`) and **remove** `QueryDataSourceRelation1` (`RecId → Location`). With both, the build fails with "Both Incoming and Outgoing are inferred…" (see design D2). Keep the `ValidTo` range, and change nothing else (fields, key, public names). Design D2
- [x] 3.5 Add `Metadata/WTC/WTC/AxClass/WTC_CustPostalAddressDeletedBackfill.xml`, a net-new final class with `public static void main(Args _args)`:
  - Implement the set described in design D3: `WTC_IsDeleted == Yes` and `WTC_DeletedPostalAddress == 0`, take the latest `ValidTo` across all valid-time states, and update.
  - Wrap it in `ttsbegin`/`ttscommit` and finish with an `info` label giving the count of updated locations.
  - Don't add a menu item unless the model has a convention for runnable classes (design open question 4).
- [x] 3.6 Check that each new or changed file is well-formed XML. Add `AxClass\WTC_CustPostalAddressDeletedBackfill` (and `AxClass\WTC_LogisticsPostalAddressTable_Extension` if it is missing) to `Projects/WTC_1197_CustomerAddressDelete/WTC_1197_CustomerAddressDelete.rnrproj`, using the existing `<Content Include=…>` entry shape and keeping the user's uncommitted removal there. Report clearly which tasks are authored but not compiled

## 4. Deploy and verify (manual, not Luigi)

- [ ] 4.1 Integration: merge. The `eim` pipeline deploys BAT and then TEST
- [ ] 4.2 In BAT, edit the name of customer `100300`, which has the deleted locations `000311920` and `000311720`. Confirm that no new `CustomerAddressDelete_To_Capture` rows appear in `MESSAGEQUEUETABLE`, and that App Insights shows `has no tracked field changes` entries for `mserp_wtc_custpostaladdressdeletedv2entity`
- [ ] 4.3 In the D365 DEV/UDE environment:
  - Build the WTC model and run a DB sync.
  - Run the design D4 count query against `WTC_CUSTPOSTALADDRESSDELETEDV2ENTITY`.
  - Run `WTC_CustPostalAddressDeletedBackfill` (`?mi=SysClassRunner&cls=WTC_CustPostalAddressDeletedBackfill`) and rerun the query. Every `(CUSTOMERACCOUNT, ADDRESSLOCATIONID)` must have `COUNT(*) = 1`, and the number of flagged locations listed must be the same as before the release. The only exception is flagged locations whose latest version is still active (deactivated without being expired); these correctly drop out.
  - Rerun the backfill and confirm it reports 0 updated.
- [ ] 4.4 In a D365 environment connected to the integration (BAT after its package):
  - Create an address on a test customer, edit its street so it has two versions, then delete it. Expect exactly one entity row, one data event, and one `CustomerAddressDelete_To_Capture` message with `Action = "Delete"` and `SOURCEID` set to its location id.
  - Then edit the customer name. Expect no event on the soft-delete entity in App Insights.
  - Deactivate an address in a way that leaves it active (if reproducible) and check that it is not listed.
- [ ] 4.5 **Blocked on design open question 5 (backfill event shape).** In DEV, first run the backfill on one hidden deleted location and check which data event it raises. If it raises a creation-shaped event (PreImage null, `mserp_wtc_isdeleted` among the changed fields), switch to the two-package rollout or exclude the backfill user before going further. Then deploy the D365 package to BAT/TEST and then PRD. After each package, run the backfill once and the D4 query. In App Insights, confirm that any backfill-triggered `OnExternalUpdated` on the soft-delete entity is logged as skipped and not forwarded, and that no `CustomerAddressDelete_To_Capture` rows appear in `MESSAGEQUEUETABLE` for already-deleted locations
- [ ] 4.6 Ask the Capture team whether the 40 duplicate deletes already delivered in BAT (78808–78850, locations `000311920`/`000311720`) had any effect that needs clean-up (design open question 2)
- [ ] 4.7 Before enabling the delete route in TEST/PRD, make sure the integration part (group 1) is deployed there. The TEST and PRD delete routes are currently `ENABLED = 0`
