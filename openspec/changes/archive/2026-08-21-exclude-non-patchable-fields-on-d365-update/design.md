## Context

`BaseCrudOrchestration<TSource, TDest>` maps a capture contract to a target entity via `MapToTarget(TSource)` — a signature that receives no action. The resulting entity is then handed to `ExecuteActionAsync(action, target, ...)`, which routes to Create, Update, or Delete. One entity instance therefore serves all three actions.

In `D365CrudOrchestration.ExecuteActionAsync`, the `Update` branch does:

```
var key = BuildEntityKey(target);              // dataAreaId + CustomerAccount
await d365Client.UpdateRawAsync(D365EntityName, key, JsonSerializer.Serialize(target), ct);
```

The key and the request body are produced from the same object. Every property that is non-null after mapping is serialized into the PATCH body — including the key fields, which are already present in the request URL, and including fields D365 refuses to modify.

Observed failure on `CustomersV3`: `update not allowed for field 'PartyNumber'`.

Current state across all four subclasses — every one sends its key fields in the body:

| Orchestration | D365 entity | Key fields | Non-key fields D365 rejects |
| --- | --- | --- | --- |
| `Customer_To_D365FSC` | `CustomersV3` | `dataAreaId`, `CustomerAccount` | `PartyNumber` |
| `CustomerAddress_To_D365FSC` | `WTCCustomerPostalAddressesV2` | `dataAreaId`, `CustomerAccount`, `AddressLocationId` | — |
| `Contact_To_D365FSC` | `ContactPersons` | `dataAreaId`, `ContactPersonId` | — |
| `PartyContact_To_D365FSC` | `PartyContactsV3` | `PartyNumber`, `ElectronicAddressId` | `Type` |

`PartyContact_To_D365FSC` already solves its case by overriding `ExecuteActionAsync` and hand-building a `Type`-free copy of the entity. That pattern does not generalise: it strips a non-key field, whereas Customer must strip `CustomerAccount`, which `BuildEntityKey` reads from the same object. Nulling it before calling `base` would produce `CustomersV3(dataAreaId='WTC',CustomerAccount='')`.

Serialization is `System.Text.Json` throughout. All entity properties are nullable `string` carrying `[JsonPropertyName]` and `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]`.

## Goals / Non-Goals

**Goals:**
- Stop sending fields D365 rejects on PATCH, resolving the reported `PartyNumber` failure.
- Apply one uniform rule across all four `D365CrudOrchestration` subclasses rather than patching Customer alone.
- Declare exclusions in one readable place per entity, so the model file fully describes update semantics.
- Stop `SalesCurrencyCode` from overwriting the customer's currency with `"USD"` on every update.
- Make the composed update payload observable in orchestration step logs.
- Replace the bespoke `PartyContact_To_D365FSC` override with the general mechanism.

**Non-Goals:**
- Changing `Create` or `Delete` behaviour.
- Modifying `ARM.Integration.Core` (`EntityKey`, `D365FSCClient`, `ID365FSCClient`).
- Changing capture message contracts or response envelopes.
- Extending `SimulationService` to exercise the `PartyNumber` path (tracked as a known gap).
- Introducing a general D365 metadata-driven read-only field discovery mechanism.

## Decisions

### 1. Declare exclusions with an attribute on the entity model, not by deriving keys from `EntityKey`

A `[D365ExcludeOnUpdate]` attribute is applied to every property that must not appear in a PATCH body — key fields included.

*Alternative considered:* expose the key field names from `EntityKey` (currently private `_segments`, reachable only through `ToString()`) and strip them automatically, using the attribute only for non-key fields. This guarantees the stripped keys can never disagree with `BuildEntityKey`.

*Rationale for rejecting it:* the attribute mechanism is required regardless, because `PartyNumber`, `Type`, and `SalesCurrencyCode` are not key fields and nothing can derive them. Key derivation would be a second mechanism layered on top of the first, and would pull `ARM.Integration.Core` into the change (new public member, version bump from 1.0.2). The duplication it prevents is between `BuildEntityKey` and the model annotations — and D365 natural keys are stable for the life of an entity, so that drift is a rare event. Where drift does occur, the common direction (a new key field added but not annotated) fails loudly at D365 rather than silently.

*Secondary benefit:* the model file becomes a complete description of update semantics. Reading `CustomersEntity.cs` answers "what does a PATCH actually send?" without opening the orchestration.

### 2. One attribute with a `Reason`, not separate attributes per category

Three distinct motivations produce exclusions: the field is an OData key, D365 refuses to patch it, or we choose not to send it. All three produce identical runtime behaviour, and no code branches on the difference — it is documentation. A single `[D365ExcludeOnUpdate(Reason = "...")]` carries the distinction in the reason text.

*Alternative considered:* `[D365NotPatchable]` plus `[D365CreateOnly]`. Rejected as duplicate machinery encoding a difference nothing consumes. If a behavioural split later emerges (for example, warning when D365 would have rejected a value), splitting is cheap because the change is confined to annotations.

Applying `[D365NotPatchable]` to `SalesCurrencyCode` would also be inaccurate — D365 patches that field fine; excluding it is our policy.

### 3. Strip fields from the serialized `JsonObject`, not from a cloned POCO

The `Update` branch serializes the entity, parses it into a `JsonObject`, removes excluded property names, and sends the result.

*Alternative considered:* clone the entity and null out excluded properties, relying on `JsonIgnoreCondition.WhenWritingNull` to drop them — the approach `PartyContact_To_D365FSC` uses today.

*Rationale for rejecting it:* it only works because every property on these entities is currently a nullable `string`. The moment a non-nullable value type is added (`public bool IsPrimary`), `null` is not representable and the field cannot be suppressed — it would ship silently. It also requires hand-written clone code per entity, which rots as properties are added; `CustomersEntity` already has twelve. That is the same drift this change exists to eliminate.

The usual objection to `JsonObject` is that it is stringly-typed. It is not, in the dangerous sense: no property name is authored by hand. Names are read from `[JsonPropertyName]` on the properties carrying `[D365ExcludeOnUpdate]`. Cost is one extra parse per update, against Service Bus-paced message volume.

Property removal must be case-insensitive, or must match on the exact `[JsonPropertyName]` value, so that a mismatch between C# property name and wire name cannot cause a silent miss.

### 4. Uniform application, not Customer-only

The rule applies to all four subclasses. Contact and CustomerAddress work today, so this alters flows that are not currently broken — but leaving them inconsistent means the same defect stays latent, and a per-entity opt-in would make the attribute's meaning conditional.

### 5. Key fields excluded, and record addressing preserved

`BuildEntityKey(target)` continues to run against the **full** mapped entity, before stripping. Only the serialized body is filtered. The record is addressed by the URL key exactly as before.

### 6. Log stripped field names

The `Update` branch logs the excluded property names through `IOrchestrationLogger`. With key exclusions declared by hand rather than derived, this log is the primary way to detect a missing annotation on the flows that have no simulator coverage.

## Risks / Trade-offs

- **A future entity or key field is added without the annotation, so a key silently rides along in the PATCH body.** → Step logging of stripped fields makes payload composition visible; a unit test asserting that no field named in `BuildEntityKey` appears in the composed update payload closes it properly and is included in scope.

- **`CustomerAccount` and `dataAreaId` rejection is unproven.** The observed D365 error names only `PartyNumber`; the request fails there before the key fields are evaluated. They may well be silently ignored today. This change removes them by construction rather than in response to evidence. → Low risk: neither field can be legitimately modified through a PATCH that addresses the record by that same key, so removing them cannot lose an intended update.

- **Contact and CustomerAddress flows currently work and this change alters their wire format.** → Exclusions are limited to key fields, which are redundant with the URL. Verify each flow after deployment using the step log to confirm the composed body.

- **`SalesCurrencyCode` becomes create-only, so existing customers keep whatever currency D365 holds.** This is the intended fix, but it is a behaviour change: updates will no longer normalise currency back to `"USD"`. → Deliberate. Callers cannot supply a currency (`CaptureCustomerMessageContract` has no such field), so the previous behaviour was an unconditional overwrite with no way to opt out.

- **No automated regression coverage for the reported failure.** `SimulationService.CaptureCustomerUpdate` never sets `PartyNumber`, so the simulator already sends a `PartyNumber`-free body and passes on a payload the real caller fails on. → Accepted for this change. Unit tests over payload composition provide coverage at the orchestration layer; end-to-end verification of the Customer path is manual.

- **The attribute is load-bearing for correctness, not merely descriptive.** A reader may assume it is documentation and remove it during cleanup. → The `Reason` text on each usage states the consequence.
