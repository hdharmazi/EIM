## Context

### Observed behaviour (BAT, eimdb, 2026-10-26)

| ID | SOURCEID | MessageName | OperationId | `mserp_addressvalidfrom` | `mserp_addressstreet` | Sent to Capture |
|---|---|---|---|---|---|---|
| 78829 | 000312031 | `OnExternalCreated` | 16e4b44d-… | 1793027363000 | `123 New Address` | `Action = "New"` |
| 78832 | 000312031 | `OnExternalCreated` | 9a15c068-… | 1793027433000 | `123 New Address # 2` | `Action = "New"` |

Both carry the same `mserp_wtc_custpostaladdressv2entityid` (`000109be-…`), the same location id, the
same `ValidTo` (max). Only `ValidFrom` and the edited field differ. 60-day distribution for
`mserp_wtc_custpostaladdressv2entity`: 45 × `OnExternalCreated`, 32 × `OnExternalUpdated`; the
`ChangedFields` on the updates are only `mserp_addresslocationroles` (21) and
`mserp_addressdescription` (11), i.e. `DirPartyLocation`/`LogisticsLocation` corrections. Two
locations received more than one `OnExternalCreated` in that window.

### Why D365 reports an edit as a creation

`LogisticsPostalAddress` is date-effective. In `WTC_CustPostalAddressV2Entity.initializeEntityDataSource`
(`C:\Custom\Metadata\WTC\WTC\AxDataEntityView\WTC_CustPostalAddressV2Entity.xml`, lines 99–132)
an edit that moves `ValidFrom` is converted to an `Insert` with
`validTimeStateUpdateMode(ValidTimeStateUpdate::CreateNewTimePeriod)`; the standard address form
does the same. A new `LogisticsPostalAddress` row is created with a new `RecId` and the previous row
gets `ValidTo = now − 1 s`. The entity's `AddressRecId` field is that `RecId`, so from the entity's
(and Dataverse's) point of view a new record appeared → `OnExternalCreated`. The expired row raises an
`OnExternalUpdated` whose only change is `ValidTo`, which `DataEventProcessor` drops because
`ValidTo` is not in `TrackedFieldsByEntity`.

### Current integration flow

```
Service Bus d365fsc-dataevent
  → QueueDataEventsHandler → DataEventProcessor.ProcessAsync        (Wilson.Integration.Automation/Outbound)
      EntityRoutes[mserp_wtc_custpostaladdressv2entity] = CustomerAddress_To_Capture
      SourceId = mserp_addresslocationid (BusinessKeyResolver), DedupeKey = OperationId
  → EIMController.InsertAndProcessAsync + IngestGate
  → CustomerAddress_To_Capture : D365EventForwardOrchestration<CaptureCustomerAddressMessageContract>
      ParseAndMap: MessageName → "New" | "Update" | "Delete"; ForcedAction overrides (delete subclass)
      MapFields(getField, action) → CaptureMessageEnvelope { SourceId, Action, Entity="CustomerAddress", Data }
  → Service Bus d365fsc-to-capture
```

The only place `Action` is decided is `D365EventForwardOrchestration.ParseAndMap`
(`ARM.Integration.EIM/Orchestration/D365EventForwardOrchestration.cs`, lines 64–89). The only
extension point today is the constant `ForcedAction`, which is what `CustomerAddressDelete_To_Capture`
uses. There is no hook that can look at the event's attributes.

### Constraints

- `ARM.Integration.EIM` must not reference client orchestrations or entity-specific attribute names
  (`orchestration-extensibility` spec, `EngineBoundaryTests`). The entity-specific rule therefore
  lives in `CustomerAddress_To_Capture`; the engine only gains a generic hook.
- `CustomerAddressDelete_To_Capture` inherits from `CustomerAddress_To_Capture`; whatever the address
  orchestration does for `New` must not disturb the forced `Delete`.
- `BusinessKeyRegistrationTests` expects exactly one representative payload per routed entity under
  `TestData/SourceEntities`; extra fixtures go elsewhere under `TestData` (precedent:
  `TestData/CustomerAddressDeleted.OnExternalUpdated.json`). `Wilson.Integration.Tests.csproj` copies
  `TestData\**\*.json`.
- Both hosts (Functions and Dashboard API) share `OrchestrationRegistry`; no registry change is
  needed because no new orchestration type is introduced.

## Goals / Non-Goals

**Goals:**
- A customer address edit that D365 surfaces as `OnExternalCreated` is forwarded to Capture as
  `Action = "Update"`, carrying the full address.
- A first-time address creation is still forwarded as `New`.
- The integration behaves exactly as today until the D365 field is present in the payload, and never
  fails a message because the field is missing or malformed.

**Non-Goals:** see proposal.

## Decisions

### D1. Mark the version on the D365 entity with a computed `NoYes` field, `WTC_HasPriorVersion`

The payload needs a fact that only D365 knows: "a previous version of this location's address
exists". Add to `WTC_CustPostalAddressV2Entity`:

- Field: `AxDataEntityViewUnmappedFieldEnum`, `Name = WTC_HasPriorVersion`,
  `ExtendedDataType = NoYesId`, `EnumType = NoYes`, `IsComputedField = Yes`,
  `ComputedFieldMethod = wtcHasPriorVersion`, `AllowEdit = No`, `AllowEditOnCreate = No`,
  public (it must be exposed to Dataverse). Same shape as `AddressLocationExist` but computed.
- Method (static, `private static str wtcHasPriorVersion()`), built with `SysComputedColumn`
  (precedent: `formattedDeliveryAddress` in the same entity and `wtcPartyRole` in
  `WTC_DirPartyContactV3Entity_Extension` from #4883). Intent in SQL terms:

  ```sql
  CASE WHEN EXISTS (
         SELECT 1 FROM LOGISTICSPOSTALADDRESS PRIOR
         WHERE PRIOR.LOCATION   = <LogisticsLocation.RecId>
           AND PRIOR.PARTITION  = <LogisticsPostalAddress.Partition>
           AND PRIOR.RECID     <> <LogisticsPostalAddress.RecId>
           AND PRIOR.VALIDFROM  < <LogisticsPostalAddress.ValidFrom>)
       THEN 1 ELSE 0 END
  ```
  where the `<…>` references come from `SysComputedColumn::returnField(...)` on the entity's
  `LogisticsLocation` and `LogisticsPostalAddress` data sources. `LogisticsPostalAddress` is a
  shared (no `DataAreaId`) table; only `Partition` applies.
- Dataverse exposes it as `mserp_wtc_haspriorversion`, delivered in data events as an
  `OptionSetValue` (`200000001` = Yes, `200000000` = No) exactly like `mserp_isprimary`, which
  `CustomerAddress_To_Capture.ResolveYesNo` already decodes.

*Alternatives rejected*

- **EIM decides from its own history** (`SELECT TOP 1 … FROM MESSAGEQUEUETABLE WHERE SOURCEID = …`):
  the history is incomplete (addresses created before go-live, messages discarded by filters,
  environments sharing one `eimdb` with different `ENVIRONMENT` values), it would couple an
  orchestration to the database, and reprocessing a message would change its answer.
- **Heuristics on existing attributes** (`ValidFrom` vs `OperationCreatedOn`, entity id): 78829 is a
  genuine creation whose `ValidFrom` is 40 s before `OperationCreatedOn`; the entity id is identical
  for both versions. Nothing present today is a reliable discriminator.
- **Always send `Update`** and rely on Capture upserting: loses the genuine `New`, and Capture's
  behaviour on an unknown `AddressLocationId` is not confirmed.
- **Stop D365 versioning addresses** (force `Correction` mode): changes standard D365 address
  history for every consumer, far outside this ticket.
- **A stored `WTC_IsUpdate` table field set in the entity's `insertEntityDataSource`**: would miss
  edits made through the standard address form (the path that produced 78832), which bypass the
  entity's persistence methods. A query-time computed column covers every path.

### D2. A generic `ResolveAction` hook in the engine; the rule in the client orchestration

In `D365EventForwardOrchestration` (`ARM.Integration.EIM`):

```csharp
/// <summary>
/// Lets a subclass refine the action derived from <c>MessageName</c> using the event itself.
/// Runs after the message-name mapping and before <see cref="ForcedAction"/>.
/// </summary>
protected virtual string ResolveAction(
    string derivedAction, EventContext context, IReadOnlyDictionary<string, object> targetAttributes)
    => derivedAction;
```

Applied in `ParseAndMap` as:

```csharp
actionString = ResolveAction(actionString, context, targetAttrs);
if (ForcedAction is { } forced) actionString = forced;
```

In `CustomerAddress_To_Capture`:

```csharp
internal const string PriorVersionField = "mserp_wtc_haspriorversion";

protected override string ResolveAction(string derivedAction, EventContext context,
    IReadOnlyDictionary<string, object> targetAttributes)
{
    if (derivedAction != "New") return derivedAction;
    targetAttributes.TryGetValue(PriorVersionField, out var raw);
    return ResolveYesNo(raw?.ToString()) == "Yes" ? "Update" : derivedAction;
}
```

Why this shape:
- The engine stays entity-agnostic (`orchestration-extensibility`); the attribute name lives with
  the Capture address contract.
- Ordering keeps `ForcedAction` authoritative, so `CustomerAddressDelete_To_Capture` is unaffected
  even though it inherits the override.
- `targetAttrs` is already parsed in `ParseAndMap`; passing it avoids re-parsing `InputParameters`
  (which `GetSkipReason` currently does).
- Default implementation returns the input, so `Customer_To_Capture`, `Contact_To_Capture`, and
  `PartyContact_To_Capture` are byte-for-byte unchanged in behaviour.

*Alternative rejected*: making `ForcedAction` a `Func<EventContext, string?>` — breaks the existing
constant override pattern and conflates "always" with "sometimes".

### D3. Field filtering for a version-based `Update`

`GetFieldValue` only suppresses unchanged fields when `actionString == "Update"` **and** the field is
not in `changedFields`. For an `OnExternalCreated` event `changedFields = targetAttrs.Keys`, so every
mapped field is still populated and `hasTrackedChanges` is `true`. A version-based update therefore
carries the **full address**, which is what Capture needs: the new version may differ in any field,
and D365 provides no diff for it. The spec makes this explicit so it is not "optimised" away later.

### D4. Fallback and failure behaviour

- Attribute absent (D365 package not yet deployed, or Dataverse not refreshed) → `New`. Identical to
  today. No warning: absence is the expected pre-deployment state and would otherwise fire on every
  create forever in an environment that lags. Verification of the D365 rollout is a deploy task, not
  a runtime log.
- Attribute present with `200000000`/`No`/anything else → `New`.
- Attribute present with `200000001`/`Yes` (integer, string, or `OptionSetValue` object) → `Update`.
- No new exception types; nothing new to classify in `DefaultFailureClassifier`. Terminal/transient
  behaviour of the orchestration is unchanged (a missing `Target` still throws `JsonException`,
  classified terminal/`Rejected`).
- Idempotency and ordering are unchanged: `DedupeKey` is still the operation id, `SourceId` is still
  the location id, and a reprocessed message yields the same action because the marker is inside the
  persisted payload.

### D5. Observability

`BaseForwardOrchestration.ProcessAsync` already logs the step `DetermineAction` with the final action
and `MappedPayload`/`Result` with the envelope, so an operator can see `Update` on a message whose
payload says `OnExternalCreated`. No additional step is added.

## Payload shapes

Inbound attribute (new):

```json
{ "key": "mserp_wtc_haspriorversion",
  "value": { "__type": "OptionSetValue:http://schemas.microsoft.com/xrm/2011/Contracts", "Value": 200000001 } }
```

Outbound envelope for 78832 after the change (data unchanged from today):

```json
{ "SourceId": "000312031", "Action": "Update", "Entity": "CustomerAddress",
  "CreatedDateTime": "/Date(1793027444000+0000)/",
  "Data": { "DataAreaId": "wtc", "CustomerAccount": "100300", "AddressLocationId": "000312031",
            "IsPrimary": "No", "AddressLocationRoles": "Business", "AddressDescription": "New Address",
            "AddressCity": "…", "AddressZipCode": "…", "AddressStreet": "123 New Address # 2",
            "AddressCountryRegionId": "USA", "AddressState": "…" } }
```

## Risks / Trade-offs

- [Capture receives `Update` for an address it never saw as `New` (created before go-live, or whose
  `New` was filtered)] → Not new: every `OnExternalUpdated` correction already does this today.
  Confirm with the Capture team that `Update` on an unknown `AddressLocationId` upserts (open
  question 1).
- [Computed column cost: a correlated `EXISTS` per address row in the entity view] → `LOGISTICSPOSTALADDRESS`
  is indexed on `LOCATION`; the entity is read per event, not scanned. Check the view's query plan
  once in DEV during task 1.3.
- [Dataverse not refreshed in an environment] → attribute absent → `New` (today's behaviour), no
  errors. Detectable by the per-environment verify task.
- [Future-dated address versions] → the `VALIDFROM <` predicate marks only rows that supersede an
  earlier one; a future-dated version entered before it takes effect is still "has prior", which is
  correct for Capture (the location already exists there).
- [The expired old version's `OnExternalUpdated` (`ChangedFields = [ValidTo]`)] → still dropped by
  the tracked-field filter; unchanged.

## Migration Plan

No SQL migration. No route change. Deployment order is free (see D4). Recommended:

1. Merge and deploy the integration (`eim` pipeline → BAT → TEST). Behaviour unchanged.
2. Deploy the WTC package with `WTC_HasPriorVersion`, DB sync, then refresh the virtual entity, per
   environment (DEV/UT → BAT/TEST → PRD). After the refresh, confirm in F&O (Business events →
   Data events) that the entity's **Create and Update data events are both active**. In DEV the
   Create activation was missing after the change, and brand-new addresses raised no event until it
   was re-activated.
3. Verify per environment: create an address (expect `New`), edit its street (expect `Update`).

Rollback: redeploy the previous integration package; the D365 field can stay (ignored).

## Open Questions

1. **Capture semantics for `Update` on an unknown address.** Assumed upsert (same assumption the
   existing correction updates rely on). If Capture rejects unknown records, a follow-up could add a
   "first time seen" fallback — out of scope here. The marker is per postal-address row, while
   `OnExternalCreated` is raised per entity record (customer link × address version). A customer
   newly linked to a location that already has several versions (address shared with a second
   customer, or re-added after a soft delete) therefore arrives as `Update` for an address Capture
   has not seen for that customer, or has deleted. This is harmless only if Capture upserts;
   task 5.4 verifies it in BAT (found in review, Bowser round 1).
2. **Historical `New` duplicates.** Two BAT locations received repeat `New` messages in the last 60
   days (including 000312031). **Resolved 2026-10-26: no clean-up needed** (user decision); no
   automated backfill is proposed.
3. **D365 branch/slug.** Assumed `bug/5236/customer-address-update-action` in `C:\Custom`, following
   the `bug/4883/issueinpartycontact` precedent.
