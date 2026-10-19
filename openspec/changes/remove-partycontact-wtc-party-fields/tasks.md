## 1. Integration

- [x] 1.1 Remove `WTC_ContactPartyNumber` and `WTC_CustomerPartyNumber` from `PartyContactsEntity`
- [x] 1.2 In `PartyContact_To_D365FSC.MapToTarget`, stop setting the `WTC_*` fields; keep `PartyNumber` resolved contact-first, then customer
- [x] 1.3 In `PartyContact_To_D365FSC.BuildResponseData`, take `ContactPartyNumber` / `CustomerPartyNumber` from the source message for `New` responses and remove `PreferPartyNumber`

## 2. Tests

- [x] 2.1 Rewrite the `PartyContactNewResponse_*` tests in `D365UpdatePayloadBuilderTests` to assert party numbers come from the submitted message (contact-only, customer-only, contact of a customer)
- [x] 2.2 Replace `PartyContactRequest_MapsSourcePartyNumbersToWtcFields` with a test that the serialized create payload for SourceId 3754's data has `PartyNumber = "000181101"` and no `WTC_ContactPartyNumber` / `WTC_CustomerPartyNumber`
- [x] 2.3 Replace `BuildUpdatePayload_PartyContact_RetainsWtcPartyNumberContext` with a test that the update payload contains no `WTC_*PartyNumber` properties and the key is `PartyNumber` + `ElectronicAddressId`
- [x] 2.4 Run `dotnet test ARM.Integration.EIM.Tests` and confirm all tests pass

## 3. Verify and deploy

- [ ] 3.1 In DEV, resubmit Capture SourceId 3754 and confirm D365 creates the email on party `000181101` and Capture receives a success response with `ContactPartyNumber = "000181101"`
- [ ] 3.2 Run a customer-only PartyContact create and update in DEV and confirm success
- [ ] 3.3 Deploy the hotfix to TEST, BAT, and PROD and resubmit any PartyContact messages that failed with the `WTC_ContactPartyNumber` error

## 4. D365 cleanup (optional, separate deployment)

- [ ] 4.1 Delete the empty `validateWrite` / `wtcResolvePartyNumber` methods from `WTC_DirPartyContactV3Entity_Extension`, build WTC, and confirm a PartyContact create via OData still succeeds
