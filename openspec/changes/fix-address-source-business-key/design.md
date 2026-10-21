## Context

`QueueDataEventsHandler.Run` (`ARM.Integration.EIM.Customer/QueueDataEventsHandler.cs`) resolves a
business key just before building the `MessageRequest`, then falls back:

```csharp
var targetAttrs = D365FSCEventParser.GetTargetAttributes(context.InputParameters);
var businessKey = BusinessKeyResolver.GetBusinessKey(context.PrimaryEntityName, targetAttrs);
...
SourceId = businessKey ?? context.OperationId,
```

`SourceId` is EIM's business identifier for the message. It is persisted to
`MESSAGEQUEUETABLE.SOURCEID`, searched by the dashboard's message queue screen, and logged by
`ReleaseSweep`. It is also copied into the outbound `CaptureMessageEnvelope.SourceId`, but Capture
does not read, store, or match on it (user-confirmed 2026-10-21). The value matters inside EIM only.

`GetBusinessKey` returns null in two indistinguishable cases: the entity is not registered, or it is
registered but the attribute is absent. Both silently degrade to `OperationId`.

Current state of the five routed entities (`EntityRoutes` vs `BusinessKeyResolver.EntityKeyFields`,
as of commit `99baf9c`):

| Routed by handler | Key in resolver | Resolves? |
|---|---|---|
| `mserp_custcustomerv3entity` | `mserp_customeraccount` | yes |
| `mserp_smmcontactpersonv2entity` | `mserp_contactpersonid` | yes |
| `mserp_wtc_custpostaladdressv2entity` | *(absent; resolver holds `mserp_custpostaladdressv2entity`)* | **no** |
| `mserp_wtc_custpostaladdressdeletedv2entity` | `mserp_addresslocationid` | yes (covered by `CustomerAddressDeleteToCaptureTests.SourceId_IsAddressLocationId_NotOperationId`) |
| `mserp_dirpartycontactv3entity` | `mserp_electronicaddressid` | yes (read with `force: true` in `PartyContact_To_Capture.MapFields`) |

Facts about the current code that shape the implementation:

- **Test visibility.** `EntityKeyFields` is `private static readonly` in `ARM.Integration.EIM`, which
  has no `InternalsVisibleTo`. `EntityRoutes` is `internal` and visible to the tests via
  `ARM.Integration.EIM.Customer.csproj`.
- **Missing `Target`.** `D365FSCEventParser.GetTargetAttributes` throws a `NullReferenceException`
  when the event has no `Target` parameter: `InputParameter` is a class, so `FirstOrDefault` returns
  null and `target.Value` dereferences it. It returns null only when a `Target` entry exists without a
  value, despite its non-nullable signature, and `GetBusinessKey` then calls `TryGetValue` on null.
  The handler already crashes on a missing `Target` today. Decided with the user (2026-10-21): fix the
  parser to return null when there is no `Target` entry, so every caller gets null instead of an
  exception, and the handler's fallback (Decision 4) takes over.
- **Blank values.** `GetBusinessKey` returns `value?.ToString()?.Trim()`, so a blank attribute yields
  `""`, and `"" ?? context.OperationId` is `""`. A blank key therefore produces an **empty**
  `SourceId` today, not the fallback. The delete entity and the contact/party-contact guards reject
  some blanks pre-insert. Customer, contact-person id, party-contact electronic address id, and
  (after this change) address create/update do not.
- **The handler has no testable seam.** `Run` creates an `EIMController` against `EIMDB` right after
  resolving the key, so neither `SourceId` nor a log entry can be observed in a unit test without a
  database.

**Evidence that real creates carry the key.** The user supplied a live `OnExternalCreated` payload
for `mserp_wtc_custpostaladdressv2entity` (Stage 40). Its `Target` attributes include
`mserp_addresslocationid`, `mserp_customeraccount`, `mserp_dataareaid`, `mserp_isprimary` as an
`OptionSetValue`, and `/Date(...)/` valid-from/valid-to values, and it has no `mserp_wtc_isdeleted`.
A sanitized copy is at `samples/CustomerAddress.OnExternalCreated.json`. Every customer, address,
organisation, user, and correlation value is replaced, the location id is set to `000146087`, and
the JSON is pretty-printed with unescaped `/`, which parses identically.

**Address delete route status** (verified in code; database state unknown).
`add-customer-address-delete-route` is fully implemented: it is routed in `EntityRoutes`, its key is
registered under the correct `wtc_` name, `mserp_addresslocationid` is required in
`EntityRequiredFields`, it is registered in `OrchestrationRegistry`, and it has nine tests in
`CustomerAddressDeleteToCaptureTests`. Migration 014 seeds BAT/DEV/DEV2/TEST with
`ENABLED = 1, ONPAUSE = 'Hold'`. Per `HoldGate.DecideAsync`, `ONPAUSE` only governs a route whose
`ENABLED = 0`. An enabled route with no backlog is *live*, so wherever 014 has been applied, delete
messages are forwarded immediately, not held. Whether 014 has been applied in each environment is
not visible from the repo.

## Goals / Non-Goals

**Goals:**
- Address create/update messages carry `AddressLocationId` as `SourceId`, so a record's create,
  updates, and delete share one identifier in EIM.
- A routed entity without a registered business key fails a test rather than degrading silently.
- Any runtime fallback to the operation id is logged at Warning with enough detail to diagnose.
- The correction introduces no new handler crash path.

**Non-Goals:**
- Backfilling historical rows (the data is disposable and may be truncated).
- Discarding or rejecting messages whose key is missing (Decision 1).
- Any change to `DEDUPEKEY`, duplicate detection, hold/release/retry, or the Capture contract.
- Relocating `BusinessKeyResolver` out of the engine, even though it holds client entity names.

## Decisions

### 1. Fall back to the operation id, and log a warning (user decision, Option A)

When no business key can be resolved, the message is still ingested with `SourceId =
context.OperationId`, and a Warning is logged. Alternatives rejected:

| Option | Why not |
|---|---|
| Keep the silent fallback | This is the property that hid the defect. |
| Discard pre-insert (an `EntityRequiredFields` entry for the address entity, as the delete route has) | Justified for deletes, where a delete with no key is not actionable. For create/update it would drop address data that reaches Capture today, over an identifier only EIM uses. |
| Fail the message / throw | Converts a cosmetic gap into a retry storm and dead letters. |

### 2. The warning is logged by the handler, through an extracted `ResolveSourceId`

`BusinessKeyResolver` is a static engine helper with no logger, and it is also used by the
party-contact skip path where a missing key is not a fallback. So the warning belongs where the
fallback is applied: the handler, at the point `SourceId` is set.

Extract that point into
`internal static string ResolveSourceId(EventContext context, ILogger logger, string messageId)` on
`QueueDataEventsHandler`. It parses target attributes, calls `GetBusinessKey`, returns the key, or
logs and returns `context.OperationId`. `Run` calls it in place of the two current lines and the
`??`. Being `internal` in `ARM.Integration.EIM.Customer`, it is already visible to the tests and can
be exercised with a recording `ILogger` and no database.

The warning uses structured properties, so it is queryable in Application Insights:

```
"Message {MessageId}: business key field {KeyField} is missing or blank for entity {Entity}; SourceId falls back to operation id {FallbackSourceId}."
```

`KeyField` comes from a new `public static string? BusinessKeyResolver.GetKeyField(string entityName)`.
The handler is in another assembly and cannot see the `internal` map. When the entity has no
registration, which the cross-check makes unreachable for routed entities, `KeyField` is logged as
`(none registered)`.

*Alternative considered:* pass an `ILogger` into `GetBusinessKey`. Rejected: it changes an engine
helper's contract for one caller, and would also warn on the party-contact skip path, where a
missing key is expected.

### 3. Blank and whitespace key values count as missing

`GetBusinessKey` returns null when the value is null, empty, or whitespace, instead of `""`. This
routes blank keys through the same fallback-and-warn path rather than persisting an empty
`SOURCEID`. It changes behaviour for every routed entity, but only for messages that today get an
empty identifier. The party-contact skip log would then show a null business key instead of `""`,
which is harmless.

### 4. `GetBusinessKey` tolerates missing target attributes

The parameter becomes `Dictionary<string, object>?`, and null returns null before any lookup. For
address events this preserves today's outcome (operation id), now with a warning, instead of a
`NullReferenceException` that the handler would rethrow into Functions retry and Service Bus
dead-lettering. It also closes the same latent path for `mserp_custcustomerv3entity`.

### 5. Correct the key, and cross-check the two maps in both directions

The one-key fix restores the intended behaviour. A test asserting that every `EntityRoutes` key is
in `EntityKeyFields`, and vice versa, would have caught this on day one; the reverse direction names
the misspelled `mserp_custpostaladdressv2entity` explicitly. This is the drift
`RouteRegistryValidator` prevents between routes and orchestrations (`orchestration-extensibility`,
*Registrations and routes are consistent*), applied one layer down. The `EntityRoutes` comment ("add
a single entry here") is part of why the drift happened, and it is corrected.

To make `EntityKeyFields` testable: make it `internal` and add
`<InternalsVisibleTo Include="ARM.Integration.EIM.Tests" />` to `ARM.Integration.EIM.csproj`.

*Alternatives considered:*

| Option | Why not |
|---|---|
| Merge key fields into the `EntityRoutes` tuple | Different assemblies and different information. `BusinessKeyResolver` is also used by the party-contact skip log. A refactor this fix does not need. |
| Reflection on the private field | Brittle and hides the dependency. |
| Startup validation in the Functions host | A host refusing to start over a cosmetic identifier is an outage. A test failure is the right severity. |

### 6. Attribute presence is verified against payloads

A registration naming an attribute that never appears is indistinguishable at runtime from no
registration. One fixture per routed entity under `TestData/SourceEntities/`, iterated from
`EntityRoutes`, proves each key resolves; a routed entity without a fixture fails. The address
fixture is copied from `samples/CustomerAddress.OnExternalCreated.json`; the delete entity reuses
`TestData/CustomerAddressDeleted.OnExternalUpdated.json`.

### 7. No backfill

The current data is disposable and may be truncated. Pre-change rows keep their GUID `SOURCEID`.

## Failure behaviour

- **No new exception types**, so `DefaultFailureClassifier` and `FailureClassifierTests` are
  unchanged. Key resolution happens in the handler before insert, outside the classifier.
- **Missing or blank key**: not a failure. The message is ingested and processed with the operation
  id as `SourceId`, and one Warning is logged per message. No retry, no dead letter.
- **Missing `Target`**: handled as a missing key (Decision 4), not a crash. This includes a `Target` that is absent, JSON `null`, or has no `Attributes`.
- **Forwarding a message with no target attributes**: the message is ingested with the operation id as `SourceId`, then `D365EventForwardOrchestration.ParseAndMap` throws `JsonException` ("has no Target attributes"). `DefaultFailureClassifier` treats `JsonException` as terminal `Rejected`, so it ends as a visible `Failed` row with no retry, instead of an unclassified `NullReferenceException`. Reprocessing from the dashboard fails the same way.
- **Other handler exceptions** keep today's behaviour: rethrown, retried by the Functions host, and
  dead-lettered by Service Bus.
- **Idempotency**: unchanged. Duplicate detection and re-drive match on `DEDUPEKEY`
  (`context.OperationId`) via `SqlDuplicateProbe`. Several changes to one address now share a
  `SOURCEID` but have distinct `DEDUPEKEY`s, so none is rejected (`message-ingest-dedupe`, *A later
  change to the same record is ingested*). A redelivery is still recognised.
- **Ordering**: unchanged. Nothing orders or groups by `SOURCEID`.

## Schema and deployed-code compatibility

No migration. `SOURCEID` is `NVARCHAR(150)` (migration 009) and `R__SPINSERTMESSAGEQUEUE` declares
`@SOURCEID NVARCHAR(150)`. The deployed host and the new host write the same column with differently
shaped values, and neither reads the other's. Rolling back the host needs no database step.

## Risks / Trade-offs

[Mixed identifiers in historical data] Pre-change address rows are findable only by operation id,
post-change rows by location id. → Applies only if existing data is retained; truncation removes it.
Recorded in the release notes.

[Warning volume] If some event shape routinely lacks its key, every such message logs a warning. →
That is the intended signal. The live create sample shows the address key present, and the first
DEV2 cycle is checked for the warning.

[Blank-key behaviour change for other entities] Customer, contact, and party-contact messages with a
blank key move from an empty `SourceId` to the operation id plus a warning. → Strictly more
informative. No consumer reads an empty `SourceId`.

[Unknown delete-route deployment per environment] With 014 applied, address deletes are already
live (`ENABLED = 1`) and keyed by location id, while creates/updates still use GUIDs until this
ships. → Only an EIM search inconsistency; Capture is unaffected. Check `ROUTETABLE` per environment
before release (task 5.1).

[The cross-check may fail on another entity] → The detection tests run before the correction. Code
inspection shows only the address entry is wrong, and the fixtures prove it.

## Migration Plan

1. Add the cross-check and fixture assertions. Confirm they fail on the address entry only.
2. Correct the key, harden `GetBusinessKey`, add `GetKeyField`, and extract `ResolveSourceId` with
   the warning.
3. Per environment, check `ROUTETABLE` for the delete route (014 applied, `ENABLED`) and record it.
4. Deploy the Functions host to DEV2. No database step. Trigger an address create and update in F&SC
   and confirm `SOURCEID` is the location id. Check Application Insights for fallback warnings.
5. Promote to BAT and TEST.

**Rollback:** redeploy the previous host. New address messages revert to GUID `SourceId` and the
warning disappears. No database rollback.

## Open Questions

- Should the message queue search treat a business key and an operation id as distinct search
  fields, given both occupy `SOURCEID` across historical rows? Out of scope here.
