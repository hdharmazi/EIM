## Context

`wtcPartyRole` (`WTC_DirPartyContactV3Entity_Extension`) builds a SQL `CASE` with two `EXISTS` checks against the contact's owning party (`DirPartyLocation.Party`): one on `CUSTTABLE`, one on `CONTACTPERSON`. Today it returns `Both` when both match. In D365 the Global Address Book allows one party to hold several roles, but a contact person must be a `DirPerson`, so customer + contact overlap is only possible for person-type customers. Wilson DEV has 68,617 organization customers, 1 person customer, 3 contact persons, and no overlap.

## Goals / Non-Goals

**Goals:**
- `mserp_wtc_partyrole` only ever carries `Customer`, `Contact`, or blank.
- Integration mapping and tests reflect the three-value domain.

**Non-Goals:**
- Preventing a party from holding both roles in D365.
- Changing the handler filter (blank is already dropped) or adding an allow-list.

## Decisions

### 1. Customer takes precedence over Contact
New `CASE` order:

```sql
CASE
  WHEN EXISTS (... CUSTTABLE ...)     THEN 'Customer'
  WHEN EXISTS (... CONTACTPERSON ...) THEN 'Contact'
  ELSE ''
END
```

- *Why*: the customer record is the primary entity in Capture; a person-type customer's own addresses belong to that customer.
- *Alternative — Contact wins*: rejected; it would route a customer's own contact info as a contact of another customer.
- *Alternative — keep `Both`*: rejected; it widens the contract for data that does not occur at Wilson.

### 2. Drop `Both` from `PartyContact_To_Capture`
`isCustomer` = role is `Customer`; `isContact` = role is `Contact` (case-insensitive). Any other value maps both party numbers to empty, which cannot happen once D365 is deployed.

## Risks / Trade-offs

- [A both-role party appears in TEST/BAT/PROD] → Its contacts go to Capture with `CustomerPartyNumber` only. Run the overlap query (customer parties that also exist in `CONTACTPERSON`) in each environment before deployment to confirm zero or accept the behaviour.
- [Integration deployed before D365] → An existing `Both` value (none in DEV) would map to blank party numbers until D365 is deployed. Deploy D365 first or together.

## Migration Plan

1. D365: update `wtcPartyRole`, build WTC, DB sync, deploy DEV → TEST → BAT → PROD. No virtual entity refresh needed.
2. Integration: deploy with or after D365 in each environment.
3. Rollback: revert the X++ `CASE`; the integration change is harmless with `Both` absent.
