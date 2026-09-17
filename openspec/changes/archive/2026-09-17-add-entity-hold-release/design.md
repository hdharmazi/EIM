## Context

`CaptureToD365Queue` is a single Service Bus queue carrying every entity. `QueueCaptureHandler` reads the `Entity` field from the envelope and routes via a hardcoded `Dictionary<string, string>` to an orchestration type. Queues are per-flow, not per-entity, so the transport offers no entity-level control.

The current pause mechanism is an environment variable checked at the top of `QueueCaptureHandler.ProcessAsync`:

```
var active = Environment.GetEnvironmentVariable(activeFlag);
if (string.IsNullOrEmpty(active) || string.Equals(active, "False", ...))
    return;                     // message never settled
```

With `host.json` setting `autoCompleteMessages: false`, returning without completing or abandoning leaves the lock to expire. The message redelivers until `MaxDeliveryCount` is exhausted and is then dead-lettered. Pausing therefore destroys traffic rather than deferring it.

Relevant existing assets this design builds on:

- `MESSAGEQUEUETABLE` already documents a status lifecycle (`0 New`, `1 Processing`, `2 Completed`, `3 Failed`, `4 Dead Letter`, `5 Awaiting D365`) and `SPINSERTMESSAGEQUEUE` already accepts `@STATUS INT = '0'`, so inserting without processing needs no procedure change.
- `AwaitingD365SweepHandler` is a working timer-sweep precedent: it finds stale status-5 rows and escalates them.
- `EIMController.EscalateStuckMessages` is already generic in `fromStatus`/`toStatus` and already performs an atomic `UPDATE ... OUTPUT inserted.ID` claim. Both sweeps in this change reuse that pattern.
- The dashboard already lists, filters, inspects, and reprocesses messages, and `MessageQueueRepository.ReprocessAsync` already re-runs a single message.
- `OrchestrationRegistry` already centralises the type-name to `IOrchestration` mapping; only the entity-to-type mapping is scattered.

A second defect surfaced while designing release. `ProcessMessageAsync` catches every exception and returns normally rather than rethrowing, so `QueueCaptureHandler` always reaches `CompleteMessageAsync`. Failures are terminal in SQL and nothing redelivers. This is a deliberate design — SQL is the retry surface, not Service Bus — but there is no automatic retry on that surface. A D365 throttle that outlasts Polly, a timeout, or a brief outage produces a status-`4` row that only a manual Reprocess can clear. Release makes this acute: a drain that hits throttling produces a large batch of terminal rows requiring individual operator action.

Volume is assumed low (customer master data; `maxConcurrentCalls: 1`; a 10-minute function timeout). This assumption drives several decisions below and should be confirmed.

## Goals / Non-Goals

**Goals:**

- Pause and resume a single entity's integration without affecting other entities on the same queue.
- Guarantee no message loss and no dead-lettering as a consequence of pausing.
- Recover automatically from transient destination failures instead of converting them into manual operator work.
- Keep held messages visible, countable, and inspectable in the dashboard.
- Guarantee that replayed messages never overwrite newer data in D365.
- Replace the hardcoded entity-to-orchestration dictionary with a data-driven registry, so adding an entity does not require editing shared engine code.
- Remove the engine's compile-time and reflection-time dependencies on client orchestration types, and move those types into their own assembly so the dependency direction is compiler-enforced rather than conventional.
- Stay entirely within the current Azure Functions host. No platform migration.

**Non-Goals:**

- Compaction or folding of held messages (see Decision 9).
- Notifying the source system that a message is held (see Decision 10).
- Per-business-key parallel draining. Release is serial per route in this change.
- Automatic discard of aged held messages. A staleness sweep is deferred.
- Publishing the engine as a versioned package, establishing a package feed, or separating repositories. This change moves client orchestrations into their own assembly so the compiler enforces a one-way dependency, but the engine is still consumed by project reference from the same repository.
- Relocating other client-specific code that sits on the product side, such as `SalesOrderExcelParser` in the Dashboard API, or moving `Model/D365FSC` into a dedicated D365 layer. Those are separate, and cheaper once this boundary exists.
- Moving queue names, connection strings, or trigger schedules out of app settings. Those are binding-time values the Functions host resolves before worker code runs, and are not addressable from SQL.
- Replacing the reflection-based orchestration dispatch fallback in `EIMController`. It is **removed**, not replaced (Decision 22).

## Decisions

### 1. Hold in SQL at a new status, not in a dedicated Service Bus queue

Held messages are inserted into `MESSAGEQUEUETABLE` at status `6` and the Service Bus message is completed normally.

*Alternatives considered:*

- **Per-entity hold queue as overflow.** Divert held messages to `hold-<entity>` and set the queue `ReceiveDisabled`. Rejected: on resume the hold queue drains while live traffic flows the direct path, so old messages overtake new ones. Detecting "hold queue empty" requires a management-API call (`ActiveMessageCount`) that is far too expensive per message.
- **Per-entity work queue for all traffic (content-based router).** A splitter fans the ingress queue out to `work-<entity>` queues; pause is `ReceiveDisabled` on one queue. This is architecturally clean and removes the race, the drain sweep, and the claim logic entirely. Rejected for now because: held messages become invisible to the dashboard; Service Bus TTL silently expires messages during exactly the long pause the feature exists for; it requires *entities x environments* queues plus per-entity trigger classes, which cuts against the goal of reducing per-client code; and at the assumed volume the table-bloat problem it solves does not exist.
- **Hybrid: insert to SQL for visibility, put only the message ID on a per-entity work queue.** Retains visibility and removes the sweep. Rejected for this change on cost grounds, not correctness — it needs the splitter, the queue topology, and TTL management. It remains the natural target if the host later moves to a long-running `ServiceBusProcessor` model, and nothing in this design blocks it.

The deciding factor is volume. At hundreds of messages/day, `MESSAGEQUEUETABLE` holding a few thousand rows at status 6 costs nothing, and the dashboard, reprocess action, and timer-sweep pattern already exist. If volume grows by orders of magnitude, revisit the hybrid.

### 2. New status `6 = Held`, not reuse of `0 = New`

Status `0` is the table default and is already reachable by accident: a crash between `InsertMessage` and `ProcessMessageAsync` leaves a row at `0`. Reusing it would make "deliberately held" indistinguishable from "we died before processing", and would preclude a future stuck-at-0 detector.

### 3. The gate lives in `EIMController`, not in each handler

A new `EIMController.InsertAndProcessAsync(MessageRequest)` resolves the route, decides held vs live, inserts with the correct status, and processes only when live.

*Alternative:* put the check in `QueueCaptureHandler`. Rejected: `QueueDataEventsHandler` performs the same insert-then-process sequence, as would every future handler, so the gate would be duplicated per entry point and would drift. Consolidating also removes a pre-existing duplication.

### 4. Route resolution moves behind `IRouteResolver`, backed by `ROUTETABLE`

```
IRouteResolver.Resolve(environment, sourceSystem, sourceEntity)
    -> { OrchestrationType, Enabled, OnPause }
```

`ROUTETABLE` is keyed on `(ENVIRONMENT, SOURCESYSTEM, SOURCEENTITY)`. `ENVIRONMENT` is part of the key so a route can be paused in production while remaining live in a lower environment to test a fix.

One abstraction serves two purposes: it replaces the hardcoded `EntityToOrchestration` dictionary (so adding an entity is a data change, not an edit to shared engine code) and it supplies the pause flag. `OrchestrationRegistry` continues to map type name to `IOrchestration` instance and is unchanged.

Resolution must be cached. The enabled check runs before anything else on every message and is a free environment-variable read today; an uncached implementation would add a database round trip per message. A short TTL (order of 60 seconds) plus an explicit invalidate is sufficient — pause is not a sub-second-latency operation.

*Alternative:* assembly scanning with an `[Orchestration]` attribute. This is attractive for the separate productisation goal but does not provide runtime-mutable enabled state, so it does not address this change.

### 5. Routes have three states, and `Live` is derived

```
  PAUSED  --operator enables-->  DRAINING  --backlog = 0-->  LIVE
     ^                              |                          |
     +------------------------------+--- operator pauses ------+

  PAUSED   : new messages -> Held(6).  Nothing drains.
  DRAINING : new messages -> Held(6).  Sweep drains oldest first.
  LIVE     : new messages processed directly. No backlog.
```

This is the correctness core of the design. If a resumed route processes live traffic directly while the sweep drains the backlog, the two paths race and older held messages land in D365 after newer live ones. Holding new arrivals until the backlog is empty forces all traffic — old and new — through one `ID`-ordered pipe.

`LIVE` is not stored. It is derived as *route enabled AND the route's backlog is empty*, where backlog means messages at `Held` **or** at `Retry Scheduled` (Decision 16). A message awaiting retry is an older message not yet applied to the destination, so it presents exactly the same ordering hazard as a held one and gets the same treatment.

This makes the transition self-healing: no flag can get stuck, and a message arriving mid-drain simply holds and is picked up on the next tick.

Consequences:

- If arrival rate exceeds drain rate, a route never returns to `LIVE`. That is still correct, just permanently ordered and batched. It is also a useful operational signal, so backlog trend must be surfaced (Decision 12).
- A single message stuck in retry stalls its whole route. This is bounded by the maximum attempt count, after which the message escalates to Dead Letter and the route resumes. The trade-off is accepted deliberately: a brief route stall is preferable to silently applying stale data.

### 6. Release is a throttled timer sweep, not a synchronous action

A new timer function mirrors `AwaitingD365SweepHandler`. Each tick, for each enabled route with held messages, it drains a bounded batch.

*Alternative:* a dashboard button that loops through held messages in the request. Rejected: thousands of messages exceed any reasonable HTTP timeout, there is no throttle, and a dropped connection leaves the drain half-done with no resumption.

Batch size is the throttle and must be configurable. D365 is visibly rate-sensitive here (`maxConcurrentCalls: 1`, Polly retrying 429s), and throttling applies per tenant, so an unbounded address drain would starve Customer and Contact traffic too.

### 7. The claim must be atomic

The sweep claims work with a single statement that selects and marks in one operation, taking the oldest rows first:

```
UPDATE TOP (@batch) MESSAGEQUEUETABLE
   SET STATUS = 1
OUTPUT INSERTED.ID
 WHERE STATUS = 6 AND <route predicate>
 -- oldest-first ordering
```

Read-then-process allows two concurrent sweeps to claim the same rows and write to D365 twice. Timer triggers are singleton across instances today, so this is currently belt-and-braces — but that guarantee disappears if the host ever changes, and the cost of the atomic form is zero.

### 8. Release skips failures, it does not stop

A message that fails during release follows the normal path to status `3` or `4` and the sweep continues.

*Alternative:* halt the drain on first failure to preserve strict ordering. Rejected: one poison message would block the entire backlog indefinitely — head-of-line blocking on the very feature intended to unblock the operator. Failed messages land in `3`/`4` where they are already visible and reprocessable. The sweep must report drained and failed counts rather than silently stopping.

### 9. No compaction in this change

Held messages are replayed individually, in `ID` order.

Collapsing held messages to "latest per business key" is unsafe here because `D365CrudOrchestration` dispatches `New`, `Update`, and `Delete` as distinct operations rather than an upsert:

- `[New, Update, Update]` collapsed to the last `Update` fails — the record was never created.
- `[New, Update, Delete]` collapsed to the last `Update` loses the delete and leaves a ghost record.
- `[Update, Update, Update]` collapsed to the last `Update` is safe.

Correct compaction requires folding *actions*, not just taking the latest, and the edge cases lose data silently when wrong. Deferred until volume demands it.

### 10. Held messages generate no notification to the source

`BaseCrudOrchestration` is the only component that emits response envelopes, and held messages never reach it, so silence is the default behaviour and costs nothing to implement. Confirmed with stakeholders: Capture does not require notification.

This also narrows the `ONPAUSE` policy set. `Fail` existed mainly to inform the source; `Hold` is the only policy implemented, with `Drop` the plausible second.

### 11. `ONPAUSE` is modelled now, implemented as `Hold` only

The column exists from the start and always contains `Hold` in this change. Retrofitting a policy column into a table that multiple client deployments already depend on is materially more expensive than carrying an unused column.

### 12. Duplicate ingest is rejected on `SOURCEID`, as a prerequisite

If the source re-sends while a route is held — a timeout, a nightly reconciliation, an operator retrigger — duplicates accumulate in the held pile and all fire on release. For `Update` actions this is wasteful; for `New` it creates duplicate records in D365.

A hold window is precisely when duplicates accumulate and release is precisely when they discharge, so dedupe sequences *before* release, not after.

### 13. Release and Reprocess remain distinct operations

| | Release | Reprocess |
|---|---|---|
| Scope | all held for a route | one message |
| Trigger | route enabled, sweep runs | operator action |
| Ordering | `ID` ascending, serial | not applicable |
| Purpose | resume normal flow | validate a fix |

Adding `6` to `ReprocessAsync`'s existing `WHERE STATUS IN (3, 4)` gives operators a canary: run one held message, inspect `MESSAGELOGDETAIL`, then release the rest. Without it, an operator flipping the switch after a bad fix converts thousands of held messages into thousands of dead-lettered ones.

### 14. The status catalogue gets a single source of truth

Status values are bare integer literals in `EIMController`, in `MessageQueueRepository` (`WHERE STATUS IN (3, 4)`, filters, counts), in the frontend badge map, and in a comment in the schema baseline. `STATUSTABLE` exists in `Migrations/001_baseline.sql` but is commented out.

Adding a sixth value without consolidating means editing six places across three deployables, and the count grows per client. This change activates and seeds `STATUSTABLE`, has the dashboard read labels from it, and introduces shared status constants in the engine.

### 15. Failures are classified as retryable or terminal

`ProcessMessageAsync` currently classifies by exception type alone: `ApplicationException` becomes `3` and everything else becomes `4`. That conflates "D365 rejected this data" with "D365 was briefly unavailable", and both end up requiring manual intervention.

The classifier distinguishes:

- **Retryable** — HTTP `408`, `429`, `5xx`, connection failures, and timeouts. These get status `7` (Retry Scheduled).
- **Terminal** — validation rejections, `400`, `401`, `403`, `404`, and deserialization or mapping errors. These keep the existing `3` / `4` behaviour.

`D365FSCException` already carries `StatusCode`, `ODataErrorCode`, and `ResponseBody`, so the information needed is already surfaced. Classification must default to **terminal** for anything unrecognised: retrying an unclassified failure risks repeating a partially applied write, whereas failing terminally is visible and recoverable by an operator.

*Alternative:* retry everything with a low attempt cap. Rejected — retrying a validation rejection cannot succeed and merely delays the operator seeing it, while consuming D365 quota.

### 16. Retry uses a scheduled status with bounded exponential backoff

A retryable failure sets status `7`, increments `RETRYCOUNT`, and stores `NEXTATTEMPTDATETIME` computed with exponential backoff plus jitter. A retry sweep claims rows at status `7` whose next attempt time has passed, using the same atomic claim as Decision 7. Once `RETRYCOUNT` reaches the configured maximum, the message escalates to `4` (Dead Letter).

*Alternative:* keep failures at status `4` and add a retry count column, with the sweep picking up status-4 rows that still have attempts remaining. Rejected: it destroys the meaning of Dead Letter. Operators need to distinguish "the system is still working on this" from "this needs a human", and a status the dashboard can filter on is the cheapest way to express that.

Maximum attempts, base delay, and jitter are configurable per environment. Backoff must be bounded so a route stall (Decision 5) has a predictable worst case.

### 17. The retry sweep respects route state

The retry sweep SHALL NOT process messages whose route is disabled. Rows at status `7` on a paused route simply wait; they are not re-held and not escalated by the pause.

This avoids a pause being partially ineffective — an operator pausing `CustomerAddress` because its mapping is broken would otherwise still see retry attempts writing to D365.

### 18. Stored-procedure parameter widths are corrected as part of this change

`SPINSERTMESSAGEQUEUE` declares `@SOURCEENTITY VARCHAR(20)` and `@DESTINATIONENTITY VARCHAR(20)` against `nvarchar(50)` columns, and `@DESTINATIONID VARCHAR(30)` against `nvarchar(150)`. SQL Server truncates stored-procedure parameter values silently, unlike a direct column insert which raises an error.

This was previously catalogued as an unrelated latent defect. It is in scope here because routing now keys on source entity:

```
  route predicate:  SOURCEENTITY = 'CustomerPostalAddress'   (21 chars)
  stored value:     SOURCEENTITY = 'CustomerPostalAddres'    (truncated at 20)
                                            |
                                            v
                    release sweep never matches the row
                    held messages can never drain
```

The longest entity name in use today is `CustomerAddress` at 15 characters, leaving five characters of headroom. Adding one longer entity would create permanently unreleasable messages.

### 19. Dedupe distinguishes "ingested and progressed" from "ingested only"

Duplicate rejection applies only when an existing row for the same source id has moved beyond status `0`. A row still at `0` is re-driven rather than rejected or duplicated.

Without this distinction, dedupe introduces a stranding path that does not exist today:

```
  insert row (status 0) --> crash before ProcessMessageAsync
                            transport message never settled
                                    |
                            redelivery --> dedupe sees the SOURCEID --> REJECT
                                    |
                            row sits at status 0 forever, never processed
```

Today this self-corrects, because redelivery simply re-inserts and processes. Naive dedupe would remove that self-correction. Re-driving preserves it and is also the correct semantic: an unprocessed row is not evidence that the work was done.

*Alternative:* wrap insert and the first status update in a transaction. Rejected as insufficient — it narrows the window but a crash can still occur after commit and before dispatch. The re-drive rule handles the whole class.

This makes the deferred stuck-at-`0` sweep a genuine safety net rather than an optional extra.

### 20. Lifecycle timestamps use a single clock, and the trigger is the real authority

Four timestamp sources currently disagree, and the last one overrides the others:

```
  MESSAGEQUEUETABLE.CREATEDDATETIME    DEFAULT (sysdatetime())      server LOCAL
  MESSAGEQUEUETABLE.MODIFIEDDATETIME   <- DateTime.UtcNow (app)     UTC
  MESSAGELOGDETAIL.CREATEDDATETIME     DEFAULT SYSUTCDATETIME()     UTC
  TRIGGER dbo.MODIFIEDDATETIME         SET ... = SYSDATETIME()      server LOCAL  <- wins
```

The trigger is easy to miss and changes the conclusion. `EIMController.UpdateMessageStatus` writes `DateTime.UtcNow`, and then an `AFTER UPDATE` trigger on `MESSAGEQUEUETABLE` immediately overwrites that column with `SYSDATETIME()`. Normalising only the application code and the column default would therefore have no effect on the value actually stored.

The trigger has a second defect:

```sql
DECLARE @ID BIGINT;
SELECT @ID = INSERTED.ID FROM INSERTED;   -- captures ONE arbitrary row
UPDATE MESSAGEQUEUETABLE
SET MODIFIEDDATETIME = SYSDATETIME()
WHERE ID = @ID;
```

On a multi-row `UPDATE` only one row is stamped; every other row retains its previous `MODIFIEDDATETIME`. This is not hypothetical for this change: `EscalateStuckMessages` already performs a set-based update, and both the release sweep (Decision 7) and the retry sweep (Decision 16) claim batches the same way. `MODIFIEDDATETIME` is precisely the column a staleness or stuck-message sweep filters on, so rows carrying a stale value could be escalated prematurely.

Therefore:

- The trigger is rewritten to be **set-based**, joining `INSERTED` rather than capturing a single id.
- The trigger uses **`SYSUTCDATETIME()`**.
- `CREATEDDATETIME`'s default becomes `SYSUTCDATETIME()`.
- `MODIFIEDDATETIME` is populated on insert rather than left NULL, so predicates over it are well-defined for rows that have never had a status update.

Azure SQL runs in UTC, which masks the clock discrepancy today. It surfaces on any server configured otherwise — precisely the second-client scenario. The multi-row defect, by contrast, is live now and independent of server time zone.

### 21. Orchestration instances are supplied by the host, not constructed by the engine

Routing data moving to SQL fixes entity-to-type. Type-to-instance is a separate coupling, and it is the one that actually blocks packaging:

```
  ARM.Integration.EIM/Orchestration/OrchestrationRegistry.cs   <- ENGINE
      ["Customer_To_Capture"]   = new Customer_To_Capture();   <- CLIENT type
      ["SalesOrder_To_D365FSC"] = new SalesOrder_To_D365FSC();

  TODAY                          REQUIRED
  Engine --+                     Engine  <-- Client
           +--> Client                   (one direction only)
  Client --+
  (circular; cannot split)
```

The seam already exists and is already used: `EIMController` takes `IReadOnlyDictionary<string, IOrchestration>` by constructor, and `Program.cs` supplies it. Only the *provider* is misplaced. `OrchestrationRegistry` relocates into a client-owned assembly; the engine retains `IOrchestration`, `OrchestrationResult`, the base orchestrations, and the injected contract, and holds no reference to any concrete client orchestration.

The client assembly is named `Wilson.Integration.Orchestrations`. This establishes a naming convention that makes the boundary legible at a glance: `ARM.Integration.*` is product, `<Client>.Integration.*` is client-specific. A reviewer can tell which side of the seam a file belongs to from its assembly name alone, without consulting a document.

*Alternatives considered:*

- **Attribute plus assembly scanning.** Removes the explicit list entirely. Rejected for now: the explicit dictionary is greppable, debuggable at compile time, and already wired. Scanning can be layered on later without changing the engine contract.
- **Loading orchestration assemblies by path at runtime.** Rejected: it recreates the untyped failure modes being removed in Decision 22, for no benefit while hosts are compiled per client anyway.

### 22. The reflection fallback is removed rather than retained

```csharp
Type.GetType($"ARM.Integration.EIM.Orchestration.{message.Type}")
```

This is not incidental debt — it is the only mechanism today that dispatches an orchestration not present in the DI registry, which is why it has survived. It also hardcodes the engine's namespace, forcing client orchestrations to live inside it.

It is safe to remove *now*, and only now, because its two jobs are both covered: route resolution is data-driven (Decision 4) and registration is host-supplied (Decision 21). Removing it earlier would have made the system less extensible; removing it later would mean carrying an untyped path that can only fail at runtime, with `TargetInvocationException` stack traces, into every client deployment.

The legacy `Task<string>` adapter goes with it. An unknown orchestration type becomes an explicit, logged error consistent with the unresolvable-route handling in Decision 4.

### 23. Both hosts reference the client orchestration assembly

`MessageQueueRepository.ReprocessAsync` constructs `OrchestrationRegistry.CreateDefault()` and runs orchestrations inside the Dashboard API process. So the API, like the Functions host, executes client code and calls the destination system directly.

Both hosts therefore receive the orchestration dictionary by injection from the client assembly. Neither host is part of the packaged engine, so this preserves the one-way dependency.

*Alternative:* make reprocess asynchronous — the API flips the message to a pending status and a sweep in the Functions host performs the work. Rejected for this change on two grounds: it converts reprocess from synchronous to eventual, weakening the canary workflow in Decision 13 where an operator validates a fix and expects an immediate result; and it requires a further status plus a third sweep.

Note that this alternative would **not** by itself free the Dashboard API of client dependencies. `SimulationService` constructs `CaptureMessageEnvelope<CaptureCustomerMessageContract>` directly and carries client-specific scenarios, D365 extension field names, and sample values, and `SalesOrderExcelParser` encodes a client-specific spreadsheet schema. Since task 3.1 relocates the `Model/Capture/*` contracts into the client assembly, the Dashboard API must reference that assembly whatever reprocess does.

This has since been settled deliberately: **the dashboard tier is client-deployed and is not part of the productised surface.** The engine tier — `ARM.Integration.Core`, `ARM.Integration.EIM`, the schema, and the deployment tooling — is what gets versioned and shared. The Dashboard API and frontend are built per client, which is already true of the frontend regardless, since its Entra configuration is baked in at build time as Vite build arguments. Simulation and the sales-order parser therefore stay where they are, and no relocation is required by this change.

## Risks / Trade-offs

- **Release floods D365 and triggers throttling, starving other entities** -> Batch size per sweep tick is the throttle and must be configurable per environment. Start conservative.
- **Replay overwrites newer manual edits made directly in D365 during the pause** -> Inherent to replay: the source wins retroactively. Acceptable if the source is authoritative for these entities; see Open Questions.
- **A route never returns to `LIVE` because arrivals outpace the drain** -> Correct but degraded behaviour. Surface held-count trend and drain rate in the dashboard so it is visible rather than silent.
- **Stale route cache means a pause takes up to the TTL to take effect** -> Acceptable for this operation. Provide an explicit invalidate so an operator action applies immediately.
- **`MESSAGEQUEUETABLE` growth degrades dashboard queries** -> There is no index on `STATUS` today and `GetStatusCountsAsync` already performs a `GROUP BY STATUS` full scan. Add the index as part of this change; the existing cleanup utility handles long-term volume.
- **Pausing an entity breaks its dependents** -> Dependencies are directional. Holding `CustomerAddress` while `Customer` flows is safe. The reverse is not: addresses would arrive referencing customers that do not yet exist in D365 and would fail to `3`/`4` rather than wait. The console should warn when pausing a route that others depend on. Modelling explicit dependencies is out of scope here.
- **Two concurrent sweeps double-process** -> Mitigated by the atomic claim in Decision 7, independent of the singleton timer guarantee.
- **Seeding `ROUTETABLE` incorrectly silently drops traffic** -> An unresolvable route must be treated as an explicit error and logged, never as an implicit hold or an implicit skip. Seed values are asserted against the current hardcoded dictionary during migration.
- **A message stuck in retry stalls its whole route** -> Bounded by the maximum attempt count, after which it escalates to Dead Letter and the route resumes. Surface retry-pending counts alongside held counts so the cause of a stalled route is visible.
- **Retry amplifies load against an already-struggling destination** -> Exponential backoff with jitter, a bounded attempt count, and a batch limit on the retry sweep. Retry and release sweeps share the destination's capacity, so their batch sizes must be considered together.
- **A failure misclassified as retryable repeats a partially applied write** -> Classification defaults to terminal for anything unrecognised, so only explicitly known-transient conditions retry.
- **Widening stored-procedure parameters changes existing behaviour for values that were previously truncated** -> Nothing in use today exceeds the current widths, so no existing value changes. Verify before deploying that no stored `SOURCEENTITY` or `DESTINATIONENTITY` value is exactly 20 characters, and no `DESTINATIONID` is exactly 30, since those would indicate prior truncation.
- **Changing the `CREATEDDATETIME` default shifts timestamps on servers not running UTC** -> Existing rows are unaffected; only newly inserted rows change. On Azure SQL there is no shift at all. Where a shift is possible, the deployment note records it so ages computed across the migration boundary are interpreted correctly.
- **The `MODIFIEDDATETIME` trigger silently defeats timestamp normalisation** -> The trigger overwrites whatever the application wrote. Normalising the column default and the application code without also rewriting the trigger would produce no observable change. Covered explicitly by Decision 20 and its task.
- **Batch claims leave stale `MODIFIEDDATETIME` values on all but one row** -> The trigger is rewritten set-based. Until then, any sweep filtering on `MODIFIEDDATETIME` after a batch update is operating on stale data. Verified explicitly by updating several rows at once and asserting every row is stamped.
- **The deferred stuck-at-`0` sweep silently matches nothing if built on the `MODIFIEDDATETIME` predicate** -> Populating `MODIFIEDDATETIME` on insert (Decision 20) removes the NULL comparison problem. Whichever sweep is built later must still be verified against rows that have never had a status update.
- **Removing the reflection fallback breaks any orchestration not present in the registry** -> Enumerate every orchestration currently reachable by reflection and confirm each is registered before removal. The registry and `ROUTETABLE` seed are asserted against each other, so a type present in one and absent from the other fails at seed time rather than at runtime.
- **The one-way dependency regresses silently once achieved** -> A boundary test asserts the engine assembly references no client orchestration type. Without an automated check, the next convenience `new Customer_To_Capture()` in engine code re-establishes the cycle and nobody notices until the split is attempted.

## Migration Plan

Each step is independently deployable and behaviour-neutral until the final step.

1. **Schema.** Create `ROUTETABLE`; activate and seed `STATUSTABLE` including `6 - Held` and `7 - Retry Scheduled`; add `RETRYCOUNT` and `NEXTATTEMPTDATETIME`; add the `STATUS` index; add the `SOURCEID` dedupe support; widen the truncating `SPINSERTMESSAGEQUEUE` parameters; normalise timestamps to UTC and populate `MODIFIEDDATETIME` on insert. No code reads the new objects yet.
2. **Seed routes.** Populate `ROUTETABLE` from the current `EntityToOrchestration` dictionary with `ENABLED = 1` for every route in every environment. Assert parity against the dictionary before proceeding.
3. **Engine.** Add status constants, `IRouteResolver` with caching, `MessageRequest.Status`, and `InsertAndProcessAsync`. With every route enabled, behaviour is identical to today.
4. **Extensibility seam.** Relocate `OrchestrationRegistry` to a client-owned assembly, inject it into both hosts, remove the reflection fallback and the legacy string adapter, and add the boundary test. Behaviour-neutral provided every reachable orchestration is registered.
5. **Handlers.** Switch `QueueCaptureHandler` and `QueueDataEventsHandler` to `InsertAndProcessAsync` and delete the unsafe `*Active` early-return. This is the step that fixes the dead-lettering defect.
6. **Dedupe.** Enable `SOURCEID` rejection, including the status-`0` re-drive rule. Must land before release exists.
7. **Retry.** Add failure classification and the retry sweep. Independently valuable and independently deployable; landing it before release means the first drain benefits from it.
8. **Release sweep.** Add the timer function. Inert while no route is disabled and no message is held.
9. **Dashboard.** Route control endpoints and screens; reprocess accepts `6`; status labels from `STATUSTABLE`; retry and held counts surfaced.

**Rollback.** Set `ENABLED = 1` on all routes and disable the release and retry sweep timers. The system reverts to current behaviour with no held messages produced. Held messages already in the table can be drained by re-enabling the sweep, or reprocessed individually. Messages left at status `7` can be reprocessed individually or escalated to `4` with the existing `EscalateStuckMessages` helper. Schema additions are additive and need not be reverted.

### 23. The source is authoritative on replay, and the resulting divergence is an accepted risk

Decided 2026-08-27. The release sweep replays held messages blindly, in order, without consulting the destination. This is the simplest sweep and the one this change implements.

It does lose data in a specific case, and that case is real rather than theoretical — Capture is bidirectional and users do edit addresses in it directly:

1. The `D365FSC -> CAPTURE` route for an entity is paused. D365 changes accumulate as held messages.
2. During the pause a user edits the same record in Capture.
3. The `CAPTURE -> D365FSC` route is *not* paused, so that edit reaches D365 immediately. D365 is now correct.
4. That write was made by the integration's own service principal, which is listed in the `ExcludedUsers` app setting, so `QueueDataEventsHandler` discards the resulting D365 event. **No corrective message is queued.**
5. On release, the held messages replay and overwrite Capture with the pre-edit value.

The user's edit is lost in Capture, and Capture and D365 are left disagreeing until something else touches the record. There is no error and nothing in the logs marks it.

It is worth being explicit that echo suppression is what makes this bite. Without `ExcludedUsers`, step 4 would queue a corrective event ordered after the held ones, and ordered replay would converge on the correct value by itself.

*Alternatives, both rejected for now on cost:* compare each held message's timestamp against the destination record's last-modified time and skip stale ones, which needs a per-record destination read during the drain; or flag conflicts as `Failed` for a human, which on a long pause over 77,699 messages produces more rows than anyone will review.

Two mitigations are cheap enough to include and do not change the shape of the sweep: skip a held message superseded by a newer held message for the same record, which is queue-local and needs no destination read; and report what a release wrote, so the divergence is at least visible afterwards. Both are covered in group 10.

The exposure is bounded by pause duration, which makes that open question more pointed than it first appeared.

## Open Questions

- **Should aged held messages replay at all?** If the source performs any periodic reconciliation, a three-week-old held message is redundant and replaying it may be worse than discarding it. This determines whether the deferred staleness sweep escalates or discards.
- **What is the actual message rate and payload size on the busiest flow?** This sizes the release batch, validates the low-volume assumption behind Decision 1, and indicates when the queue-based hybrid becomes worth revisiting.
- **How long is a realistic pause?** Hours, days, or weeks changes the weight given to table growth and to the staleness question. This now also bounds the exposure in Decision 23.
- **What maximum attempt count and backoff ceiling are acceptable?** This bounds the worst-case route stall from Decision 5 and should be set against how long D365 throttling episodes actually last in practice.
