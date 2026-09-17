## Why

Today an integration flow can only be paused at queue granularity, but `CaptureToD365Queue` carries every entity (Customer, Contact, PartyContact, CustomerAddress, SalesOrder) and routes them via a hardcoded dictionary in `QueueCaptureHandler`. There is no way to stop CustomerAddress while Customer keeps flowing.

Worse, the existing pause mechanism is unsafe. `QueueCaptureHandler.ProcessAsync` returns early when its `*Active` environment variable is false, without settling the message. With `autoCompleteMessages: false`, the lock simply expires, the message redelivers, and after `MaxDeliveryCount` attempts it is **dead-lettered**. "Pausing" a flow today silently destroys everything that arrives during the pause.

This change introduces entity-level hold and release: messages for a paused route are captured durably in `MESSAGEQUEUETABLE` at a new Held status, remain visible and inspectable in the dashboard, and are replayed in order when the route is resumed.

## What Changes

- **Route resolution moves to SQL.** A new `ROUTETABLE` maps (environment, source system, source entity) to an orchestration type, with an `ENABLED` flag and an `ONPAUSE` policy. This replaces the hardcoded `EntityToOrchestration` dictionary in `QueueCaptureHandler`.
- **Orchestration instances are supplied by the host, not constructed by the engine.** `OrchestrationRegistry` lives in the engine and instantiates client types directly (`new Customer_To_Capture()`), and the reflection fallback hardcodes the engine's own namespace (`Type.GetType($"ARM.Integration.EIM.Orchestration.{message.Type}")`). Both make the engine depend on client code, which is a circular dependency that prevents the engine from ever being packaged separately. Moving route data to SQL fixes only half of this — entity to type. Type to instance must move too, or the seam remains blocked.
- **The reflection dispatch fallback is removed.** **BREAKING** for any orchestration that relies on being resolved by type name from the engine's namespace. With routing data-driven and registration host-supplied, the registry becomes the single supported mechanism, and the untyped path adds only runtime failure modes.
- **New message status `6` (Held).** Messages for a disabled route are inserted at status 6 and not processed. The Service Bus message is completed normally, so nothing dead-letters.
- **The hold gate moves into `EIMController`.** A new `InsertAndProcessAsync` consolidates the insert-then-process sequence duplicated across `QueueCaptureHandler`, `QueueDataEventsHandler`, and future handlers, and applies the gate in one place.
- **Routes have three states, not two.** `Paused` -> `Draining` -> `Live`. While a route has held messages, new arrivals are also held, so the backlog and live traffic flow through a single ID-ordered pipe. Without this, a resumed route lets new messages overtake older held ones and stale data overwrites current data in D365.
- **Release is a throttled timer sweep**, not a synchronous action. It atomically claims a batch of held rows in `ID` order, processes them serially, and continues past individual failures rather than head-of-line blocking.
- **Reprocess accepts Held messages**, giving operators a canary: validate a fix against one held message before releasing thousands.
- **Transient failures retry automatically.** Today `ProcessMessageAsync` swallows every exception and the handler completes the transport message regardless, so nothing ever redelivers. A D365 throttle that outlasts Polly, a timeout, or a brief outage becomes a terminal status-`4` row requiring a manual Reprocess click. This change adds status `7` (Retry Scheduled) with attempt counting and exponential backoff, escalating to Dead Letter only after attempts are exhausted. Without it, the first release that hits D365 throttling hands an operator hundreds of rows to click through — the exact toil this change exists to prevent.
- **Duplicate ingest is rejected on `SOURCEID`.** A hold window is exactly when a re-sending source piles up duplicates, and release is when they would all fire at once. This is a prerequisite for release, not a follow-up. Rejection applies only to messages that have progressed past ingest — a row still at status `0` is re-driven rather than rejected, so a crash between insert and processing cannot strand a message permanently.
- **Stored-procedure parameter widths are corrected.** `SPINSERTMESSAGEQUEUE` declares `@SOURCEENTITY` and `@DESTINATIONENTITY` as `VARCHAR(20)` against `nvarchar(50)` columns, and `@DESTINATIONID` as `VARCHAR(30)` against `nvarchar(150)`. SQL Server truncates stored-procedure parameters silently. With routing now keyed on source entity, an entity name longer than 20 characters would be stored truncated, the release sweep's route predicate would never match it, and its held messages could never drain. This is not hypothetical: 77,993 of 78,090 existing rows were already truncated this way, the real source entity names being 26 to 35 characters. A backfill recovers them from each row's own payload.
- **Lifecycle timestamps move to a single clock.** `CREATEDDATETIME` defaults to `SYSDATETIME()` (server local), `MODIFIEDDATETIME` is written as `DateTime.UtcNow` (UTC), and `MESSAGELOGDETAIL.CREATEDDATETIME` defaults to `SYSUTCDATETIME()` (UTC). Azure SQL masks this because it runs in UTC. The route console's oldest-held-message age and the deferred staleness sweep both compare these values, so the mismatch must be resolved before it silently skews them on a differently configured server.
- **The status catalogue gets a single source of truth.** Status values are currently bare integer literals across the engine, the API, and the frontend, and `STATUSTABLE` is commented out in `Migrations/001_baseline.sql`. Adding a sixth value without consolidating would mean editing six places across three deployables.
- **Dashboard gains route control**: pause/resume per route, held counts, drain progress.

No breaking changes. Existing routes default to `ENABLED = 1`, which reproduces current behaviour exactly.

## Capabilities

### New Capabilities

- `integration-route-registry`: SQL-backed resolution of (environment, source system, source entity) to an orchestration type, with enabled state and pause policy. Replaces hardcoded entity-to-orchestration dictionaries.
- `orchestration-extensibility`: Orchestration implementations are contributed by the host from a client-owned assembly rather than constructed inside the engine, so that engine code holds no reference to client types and can be packaged independently.
- `message-hold-release`: The Held status, the ingest gate, the Paused/Draining/Live route lifecycle, and the throttled ordered release sweep.
- `message-ingest-dedupe`: Rejection of duplicate inbound messages by `SOURCEID` so that hold windows do not accumulate duplicates that all fire on release, while re-driving messages that were ingested but never processed.
- `transient-failure-retry`: Classification of failures as retryable or terminal, the Retry Scheduled status, bounded exponential backoff, and automatic escalation to Dead Letter once attempts are exhausted.
- `route-control-console`: Dashboard screens and API endpoints for pausing/resuming routes and observing held counts and drain progress.

### Modified Capabilities

- `message-queue-management`: Reprocess must accept messages at status `6` (currently gated to `WHERE STATUS IN (3, 4)`); status filters, status counts, and cleanup must recognise Held.
- `async-orchestration-result`: The controller no longer supports reflection-based dispatch of orchestrations resolved by type name, nor adaptation of legacy `Task<string>` return values. All orchestrations are host-registered and return `OrchestrationResult`.

## Impact

**Schema** (new versioned migrations under `Script/SQL/Migrations/`, applied by `ARM.Integration.DbMigrator`)
- New `ROUTETABLE`.
- New `STATUSTABLE`, seeded, including `6 - Held` and `7 - Retry Scheduled`. Its commented-out definition is deleted from the baseline so it is declared in one place only.
- New `RETRYCOUNT` and `NEXTATTEMPTDATETIME` columns on `MESSAGEQUEUETABLE`.
- Index on `MESSAGEQUEUETABLE(STATUS)` to support the release and retry sweeps and the existing `GROUP BY STATUS` count query, which is a full scan today.
- Unique constraint or index supporting `SOURCEID` dedupe.
- Widen `SPINSERTMESSAGEQUEUE` parameters `@SOURCEENTITY` and `@DESTINATIONENTITY` to match their `nvarchar(50)` columns, and `@DESTINATIONID` to match `nvarchar(150)`.
- Normalise lifecycle timestamps to UTC: change the `CREATEDDATETIME` default to `SYSUTCDATETIME()` and populate `MODIFIEDDATETIME` on insert rather than leaving it NULL.
- `SPINSERTMESSAGEQUEUE` already accepts `@STATUS INT = '0'`, so no procedure change is required to insert at 6.

**Engine** (`ARM.Integration.EIM`)
- `EIMController`: new `InsertAndProcessAsync`, status constants replacing literals `1`/`2`/`3`/`4`, and failure classification in `ProcessMessageAsync`'s catch blocks.
- `EIMController.DispatchOrchestrationAsync`: remove the reflection fallback and the legacy `Task<string>` adapter; unknown orchestration types become an explicit error.
- `OrchestrationRegistry` relocates out of the engine into a client-owned assembly. The engine keeps `IOrchestration` and the existing injected `IReadOnlyDictionary<string, IOrchestration>` contract, which already exists on the `EIMController` constructor.
- New `IRouteResolver` abstraction plus a cached SQL-backed implementation.
- New failure classifier reading `D365FSCException.StatusCode` / `ODataErrorCode`.
- `MessageRequest`: expose `Status` so the insert can specify `6`.
- Remove the unused `Microsoft.Azure.WebJobs` package reference, which drags the legacy in-process Functions SDK into every consumer of the engine.

**Functions host** (`ARM.Integration.EIM.Customer`)
- `QueueCaptureHandler` and `QueueDataEventsHandler`: drop local routing dictionaries and the unsafe `*Active` early-return; call `InsertAndProcessAsync`.
- New release sweep and retry sweep timer functions, mirroring the existing `AwaitingD365SweepHandler` pattern.
- `EIMController.EscalateStuckMessages` is already generic in `fromStatus`/`toStatus` and is reused for retry exhaustion.

**Dashboard** (`ARM.Integration.Dashboard.API`, `ARM.Integration.Dashboard.Frontend`)
- New route control endpoints and repository.
- `MessageQueueRepository.ReprocessAsync` currently calls `OrchestrationRegistry.CreateDefault()` and executes client orchestrations inside the API process. It must receive the orchestration dictionary by injection instead of constructing it.
- `MessageQueueRepository.ReprocessAsync`: allow status 6.
- Status labels sourced from `STATUSTABLE` rather than hardcoded in the frontend, including Retry Scheduled with its attempt count and next attempt time.

**Related defects surfaced but out of scope** (tracked separately)
- Committed Service Bus root key in `ARM.Integration.Test/local.settings.json`.
- No detector for messages stranded at status `0` or `1`. A sweep is cheap given the existing `EscalateStuckMessages`, but its predicate cannot use `MODIFIEDDATETIME` for status `0`: that column is NULL until the first status update, so `NULL < @CUTOFF` evaluates UNKNOWN and the sweep would silently match nothing.
