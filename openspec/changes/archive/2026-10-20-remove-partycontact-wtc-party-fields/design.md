## Context

`PartyContact_To_D365FSC` maps a Capture `PartyContact` message to D365 `PartyContactsV3`. `MapToTarget` sets `PartyNumber` (contact first, then customer) and also `WTC_ContactPartyNumber` / `WTC_CustomerPartyNumber`. Those two were unmapped entity fields added in D365 PR 563 so the response could echo them; they were removed from `DirPartyContactV3Entity.WTC.xml` when `WTC_PartyRole` was introduced. OData now rejects any payload containing them, so every Capture → D365 PartyContact create/update fails. Example: Capture SourceId 3754 (`ContactPartyNumber = "000181101"`, email `cchurch@mail.com`).

`PartyContactsV3` has no `AssociatedPartyNumber` (that field belongs to `ContactPersonsV2`); the owning party is the standard `PartyNumber`, which is also half of the OData key.

## Goals / Non-Goals

**Goals:**
- Capture → D365 PartyContact create and update succeed against the current D365 entity.
- Capture responses keep returning the same `ContactPartyNumber` / `CustomerPartyNumber` they do today.

**Non-Goals:**
- Changing `PartyNumber` resolution or the OData key.
- Using `WTC_PartyRole` in the Capture → D365 direction.
- Any change to the D365 → Capture data event flow.

## Decisions

### 1. Remove the WTC properties from the D365 model
Delete `WTC_ContactPartyNumber` / `WTC_CustomerPartyNumber` from `PartyContactsEntity` rather than just leaving them null. Null properties are already omitted on write, but keeping them invites reuse against a field D365 no longer has; removing them makes the contract match the entity.

### 2. Response party numbers come from the Capture request
`BuildResponseData` already falls back to the source values when the D365 `WTC_*` fields are blank, and those fields only ever echoed what the integration posted. Using `source.ContactPartyNumber` / `source.CustomerPartyNumber` directly gives the same output. `PreferPartyNumber` is no longer needed.

- *Alternative — derive from D365 `PartyNumber` + `WTC_PartyRole`*: rejected; `WTC_PartyRole` is computed and not populated in the create response buffer, and the response would lose the customer when Capture submits a contact of a customer.

### 3. Precedence differs by direction on purpose
Capture → D365 resolves `PartyNumber` contact-first: when Capture sends both, the electronic address belongs to the contact person. D365 → Capture classifies a single party customer-first (`restrict-partyrole-values`). They answer different questions and must not be "aligned".

### 4. D365 cleanup is optional and separate from the hotfix
`validateWrite` / `wtcResolvePartyNumber` in `WTC_DirPartyContactV3Entity_Extension` are now empty and harmless. Removing them is a D365 build/deploy and should not gate the integration hotfix.

## Risks / Trade-offs

- [Messages that failed before the fix] → They remain failed in EIM; resubmit them from the dashboard after deployment (e.g. SourceId 3754).
- [Environment with old D365 entity still deployed] → Omitting the `WTC_*` fields is valid against the old entity too, so the hotfix is safe to deploy everywhere regardless of D365 order.

## Migration Plan

1. Deploy the integration hotfix to every environment (safe with old and new D365 entity).
2. Resubmit failed PartyContact messages from the EIM dashboard.
3. Rollback: redeploy the previous integration build (only valid where the old D365 entity is still in place).
