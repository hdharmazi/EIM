## 1. Implementation (Luigi)

- [x] 1.1 (main session, not Luigi) Create branch `bug/5261/contact-update-full-payload` from `main` in `ARM.Integration.Wilson` (created 2026-10-27 from `db4e92f`; this repo has no `development` branch)
- [x] 1.2 In `Wilson.Integration.Orchestrations/Orchestrations/Contact_To_Capture.cs` `MapFields`, map `DataAreaId`, `FirstName`, `LastName`, and `MiddleName` with `getField(<attr>, true) ?? ""` (design D1, D2). Leave the other four fields and `CreateEnvelope` unchanged. Update the class summary to say that every mapped field is sent on `New` and `Update`. Proven by `ContactToCaptureTests` (task 2.1)

## 2. Tests (Luigi)

- [x] 2.1 Add `Wilson.Integration.Tests/ContactToCaptureTests.cs`, modelled on `PartyContactToCaptureTests` (inline `Event(...)` builder with `PrimaryEntityName`/`LogicalName` `mserp_smmcontactpersonv2entity`, and a `TestableContactToCapture` subclass exposing `ParseAndMap` and `CreateEnvelope`). Serialize the envelope with `JsonSerializer.Serialize((object)envelope)`, as `BaseForwardOrchestration` does, and assert on the JSON. Cover each spec scenario:
  - *Update that changes only the first name*: all eight fields present with the expected values and `Action = "Update"`
  - *Empty name fields are sent as empty strings on update*: `"MiddleName": ""` and `"LastName": ""` are present
  - *Absent name attribute is sent as an empty string*: `mserp_middlename` is removed from `Target`, for both `OnExternalCreated` and `OnExternalUpdated`
  - *New and Update carry the same fields*: the `Data` JSON objects are equal
  - *Unmapped contract fields stay omitted*: no `AssociatedContactNumber` or `AssociatedContactType`
  - *Update of an unmapped attribute only*: `hasTrackedChanges = false`
  - *Update of a mapped attribute*: `hasTrackedChanges = true`
- [x] 2.2 Run `dotnet build ARM.Integration.Wilson.slnx`, `dotnet test ARM.Integration.EIM.Tests/ARM.Integration.EIM.Tests.csproj`, and `dotnet test Wilson.Integration.Tests/Wilson.Integration.Tests.csproj`; all green

## 3. Deploy and verify (manual, not Luigi)

- [ ] 3.1 Merge the PR into `main`; the `eim` pipeline deploys BAT then TEST
- [ ] 3.2 In BAT, edit only the first name of a contact person whose last name is set and whose middle name is blank. In the dashboard, confirm that the `Contact_To_Capture` message's `MESSAGELOG` result has `Action = "Update"` with `dataAreaId`, `MiddleName: ""`, and the real `LastName`. Confirm with the Capture team that the contact updates correctly and that a blank name clears the field in Capture (design risks)
- [ ] 3.3 Promote to PRD via `eim-prd`, repeat 3.2 with a test contact, and record the result on #5261
