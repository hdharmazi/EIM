## 1. Pre-check

- [x] 1.1 Run the customer/contact party overlap query in TEST, BAT, and PROD (customer parties that also exist in `CONTACTPERSON`) and confirm zero or accept Customer precedence for any found

## 2. D365 (WTC model, `c:\Custom\Metadata`)

- [x] 2.1 In `WTC_DirPartyContactV3Entity_Extension.wtcPartyRole`, remove the `Both` branch so the `CASE` returns `Customer` first, then `Contact`, else blank
- [x] 2.2 Build the WTC model and run DB sync in DEV
- [x] 2.3 Verify in SQL that `SELECT DISTINCT WTC_PARTYROLE FROM DIRPARTYCONTACTV3ENTITY` returns only `Customer`, `Contact`, and blank

## 3. Integration

- [x] 3.1 In `PartyContact_To_Capture.MapFields`, map `Customer` to `CustomerPartyNumber` and `Contact` to `ContactPartyNumber`, removing the `Both` handling
- [x] 3.2 Replace the `Created_Both_MapsBothPartyNumbers` test with one asserting an unrecognised role leaves both party numbers blank
- [x] 3.3 Run `dotnet test ARM.Integration.EIM.Tests` and confirm all tests pass

## 4. Rollout

- [x] 4.1 Deploy D365 then the integration in TEST, BAT, and PROD
- [ ] 4.2 In each environment, create an email on a test customer and a test contact person and confirm Capture receives the correct party number
