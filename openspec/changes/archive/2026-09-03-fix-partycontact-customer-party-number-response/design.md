## Context

`PartyContact_To_D365FSC` maps Capture `PartyContact` messages to D365 `PartyContactsV3` CRUD operations and then builds a Capture-facing success response through `BuildResponseData`. The reported D365 response includes the generic key field `PartyNumber` plus custom relationship fields `WTC_ContactPartyNumber` and `WTC_CustomerPartyNumber`; the Capture response must use the custom relationship fields rather than copying the generic key into both Capture party-number fields.

The base CRUD orchestration already creates the outbound success envelope and delegates response field selection to the entity-specific orchestration, so this change should stay inside the PartyContact mapping and its tests.

## Goals / Non-Goals

**Goals:**

- Add `WTC_ContactPartyNumber` and `WTC_CustomerPartyNumber` to the D365 PartyContact entity model so the OData response can be deserialized.
- Map D365 `WTC_ContactPartyNumber` to Capture `ContactPartyNumber` and D365 `WTC_CustomerPartyNumber` to Capture `CustomerPartyNumber` for successful PartyContact responses.
- Map supplied Capture party-number fields back into the matching D365 `WTC_*PartyNumber` fields when building create/update entities.
- Continue using D365 result values for D365-owned create outputs such as `ElectronicAddressId`, `LocationId`, locator, and electronic address attributes.
- Cover the reported `New` response shape with a focused test that would fail if Capture `CustomerPartyNumber` is copied from D365 `PartyNumber` or left malformed.

**Non-Goals:**

- Change Capture request payload names or queue routing.
- Change the D365 OData key for `PartyContactsV3`; it remains `PartyNumber` plus `ElectronicAddressId`.
- Rework the base CRUD response envelope behavior.

## Decisions

- Keep request-side party resolution unchanged for the OData key: source `ContactPartyNumber` maps to D365 `PartyNumber` when present, otherwise source `CustomerPartyNumber` maps to `PartyNumber`.
- Add nullable/string properties for `WTC_ContactPartyNumber` and `WTC_CustomerPartyNumber` on `PartyContactsEntity` using matching `JsonPropertyName` attributes. These fields are returned by D365 and are needed to distinguish contact and customer party numbers in the response.
- Populate the same WTC fields from the source Capture contract in `MapToTarget`, so update payload construction retains the party context D365 expects while `PartyNumber` and `ElectronicAddressId` remain excluded from the PATCH body as OData keys.
- Build Capture response party fields from the D365 custom fields: `ContactPartyNumber = d365Result.WTC_ContactPartyNumber` and `CustomerPartyNumber = d365Result.WTC_CustomerPartyNumber`.
- Use `PartyNumber` only as a fallback if the matching D365 custom field is missing and the existing source value is also unavailable. This prevents the generic key from overwriting the wrong Capture relationship field while still avoiding empty identity values for unexpected D365 payloads.
- Add a test through a testable subclass or nearest existing protected-method test pattern. This keeps the check deterministic and avoids requiring live Service Bus or D365 connectivity.

## Risks / Trade-offs

- D365 returns only one custom party field for a given record -> map the populated custom field to the matching Capture field and leave the other field empty/null unless the source request supplies it and the implementation chooses source fallback.
- Request contains only `CustomerPartyNumber` and no `ContactPartyNumber` -> the current resolver uses the customer party number as the D365 `PartyNumber` key. This change should not broaden into party resolution policy without a separate requirement.
- Existing malformed code may currently prevent build/test execution -> fix the response mapping before running focused tests, then run the narrow test project or build slice.