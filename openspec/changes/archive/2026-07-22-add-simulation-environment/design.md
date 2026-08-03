## Context

The simulation console (`ARM.Integration.Dashboard.API` + `ARM.Integration.Dashboard.Frontend`) drops test messages onto Service Bus queues so the real Azure Functions integration engine (`ARM.Integration.EIM.Customer`) picks them up. Today the four queue names are read from `IConfiguration` (`Simulation:*Queue` in `appsettings.json`) and `SimulationController.GetConfig()` exposes them read-only. Changing target queues (e.g. from `bat-*` to `dev-*`) requires editing settings and redeploying.

The codebase already has established CRUD patterns to mirror exactly:
- `EnvironmentRepository` / `EnvironmentController` / `EnvironmentRecord` (raw `Microsoft.Data.SqlClient` `SqlCommand`, UPPERCASE `*TABLE` names, JSON `VALUE` blob).
- `UserRepository` / `USERTABLE` (single/composite PK, `ISACTIVE` bit, `CREATEDDATETIME` default `SYSDATETIME()`).
- `JsonValueEditor.tsx` in the `environment` feature already edits a JSON blob as key/value fields — the exact pattern the generic configuration form reuses.
- Frontend feature folders with `api.ts` (react-query hooks), a `*ListPage.tsx`, and a `*FormPage.tsx`; nav in `AppShell.tsx`; routes in `router.tsx`; admin gating via `RequireAdmin`.

All bat/dev/test queues live on the **same** Service Bus namespace, so a single server-side `Simulation:ServiceBusConnectionString` is sufficient — environments differ only by queue name. To avoid a single-purpose schema, the configuration is stored in a **generic, reusable** store so future features/projects can define their own named configurations without new DDL.

## Goals / Non-Goals

**Goals:**
- Provide a generic, reusable store of named configurations grouped by category, with dynamic (JSON) fields, managed by an admin-only CRUD screen.
- Model simulation environments as configurations under the `SimulationEnvironment` category; let any validated user pick one per simulation run, which auto-populates the four queue names.
- Resolve run-time queues from the selected configuration's fields instead of `appsettings.json`, keeping the connection string server-side.
- Preserve existing behavior by seeding the store from current `appsettings.json` values.

**Non-Goals:**
- Storing or editing Service Bus connection strings in the UI/DB.
- Multi-namespace support (single namespace assumed).
- A global/shared "active environment" — selection is strictly per run.
- Moving the Azure Functions integration engine into the dashboard (explicitly kept separate).
- Reworking the existing, unrelated `ENVIRONMENTTABLE` / Environments feature.

## Decisions

### Decision: Generic `CONFIGURATIONTABLE` with dynamic JSON fields (not typed columns)
Store configurations in one reusable table keyed by `(CATEGORY, NAME)`, with editable fields in a JSON `VALUE` column, rather than a single-purpose `SIMULATIONENVIRONMENTTABLE` with fixed queue columns.
- **Why**: Reusable across features/future projects — any new use case picks a new `CATEGORY` and defines its own fields with zero schema change. The field set (e.g. `D365Queue`) becomes editable data, not DB structure.
- **Alternative considered**: Typed per-queue columns — stronger per-field validation and a nicer form, but single-purpose and requires DDL for every new use case.
- **Alternative considered**: Reuse the existing `ENVIRONMENTTABLE` — rejected to avoid mixing simulation config with the runtime engine's connection strings and the D365 "Environments" concept.

```sql
CREATE TABLE [dbo].[CONFIGURATIONTABLE](
    [CATEGORY]        [nvarchar](50)  NOT NULL,  -- e.g. 'SimulationEnvironment'
    [NAME]            [nvarchar](50)  NOT NULL,  -- unique within category (BAT/DEV/TEST)
    [DISPLAYNAME]     [nvarchar](100) NULL,
    [VALUE]           [nvarchar](max) NOT NULL,  -- JSON object of editable key/value fields
    [ISACTIVE]        [bit]           NOT NULL CONSTRAINT [DF_CONFIG_ISACTIVE] DEFAULT (1),
    [CREATEDDATETIME] [datetime]      NOT NULL CONSTRAINT [DF_CONFIG_CREATED]  DEFAULT (SYSDATETIME()),
 CONSTRAINT [PK_CONFIGURATIONTABLE] PRIMARY KEY CLUSTERED ([CATEGORY] ASC, [NAME] ASC)
) ON [PRIMARY]
```

Example `VALUE` for a `SimulationEnvironment` config:
```json
{
  "D365Queue": "bat-d365fsc-dataevent",
  "CaptureToD365Queue": "bat-capture-to-d365fsc",
  "CaptureToD365ResponseQueue": "bat-capture-to-d365fsc-response",
  "D365ToCaptureQueue": "bat-d365fsc-to-capture"
}
```

### Decision: Composite PK `(CATEGORY, NAME)`; no connection string field
`CATEGORY` groups configs by purpose/project; `NAME` is unique within a category and is the dropdown value. Queue names live in JSON fields — the connection string stays as the existing server-side app setting.
- **Why**: All queues share one namespace, so a stored connection string would be redundant and a secret-sprawl risk.

### Decision: `ISACTIVE` flag controls selection visibility
List-by-category for selection returns only `ISACTIVE = 1`; the admin list shows all.
- **Why**: Lets admins retire a config without deleting it — mirrors `USERTABLE.ISACTIVE`, a trusted pattern.

### Decision: Simulation feature owns the field contract
The store is generic; the simulation feature defines the well-known category constant `"SimulationEnvironment"` and the four expected field keys. Optionally, creating a config in that category pre-fills the four keys so admins are not guessing.
- **Why**: Keeps the store reusable while giving the simulation console a stable contract; a missing key surfaces as a clear run-time error.

### Decision: Environment sent per run; server resolves queues
`SimulationRequest` gains an `Environment` (config name) field. `SimulationController.Run`/`SimulationService` look up the `SimulationEnvironment` config by name and read its queue fields; `SbConn` stays from `IConfiguration`.
- **Why**: Per-run selection avoids shared-state collisions between concurrent testers and maps cleanly onto the existing request model.
- **Alternative considered**: A global active-environment setting row — simpler UI state but surprises concurrent users.

### Decision: Generic API surface `/api/configuration`
- `GET /api/configuration?category=SimulationEnvironment` → active configs for the dropdown (any validated user).
- `GET/POST/PUT/DELETE /api/configuration/{category}/{name}` → admin-only CRUD (guarded like Users), with duplicate-name conflict handling within a category.
- The existing `GET /api/simulation/config` is superseded; its `IsServiceBusConfigured` badge may be retained or folded into the simulation page.

### Decision: Frontend generic `configuration` feature, admin-only nav
New feature folder mirroring `environment`/`users`: `api.ts`, `ConfigurationListPage.tsx`, `ConfigurationFormPage.tsx` with a key/value editor reusing the `JsonValueEditor` pattern; routes wrapped in `RequireAdmin`; a new `adminOnly` nav item labeled "Configuration" under the **Administration** (System Administration) section, alongside Users. `SimulationPage.tsx` swaps its static config card for an environment dropdown (configs in the `SimulationEnvironment` category) + derived queue display, and blocks Run until a selection exists.

## Risks / Trade-offs

- **Dynamic JSON fields lose per-field DB validation** → The simulation feature validates the four required keys at run time and reports a clear error; optionally pre-fill the keys when creating a `SimulationEnvironment` config.
- **Naming confusion with existing "Environments"** → The new generic screen is labeled distinctly (e.g. "Configuration") with a separate route/table; place it under Administration, not Configuration-the-nav-section for the D365 environments.
- **Deploy before rows exist / empty state** → Seed BAT + DEV configs from current `appsettings.json` in `Install.sql`; the console blocks Run and prompts to create one when the list is empty.
- **Selected environment deleted mid-session** → Server validates the config name on Run and returns a clear error if it no longer exists; the dropdown refetches on the list query.
- **Removing `GET /api/simulation/config` could break the config badge** → Retain the endpoint or fold `IsServiceBusConfigured` into the simulation page rather than deleting outright.
- **Queue name typos in admin form** → Fields are free-text (no live Service Bus enumeration in scope); mitigated by seeding known-good defaults and clear key labels. Live enumeration is a possible future enhancement.

## Migration Plan

1. Add `CONFIGURATIONTABLE` DDL + seed rows (BAT, DEV in category `SimulationEnvironment`, from current `appsettings.json`) to `Script/SQL/Install.sql`; provide an incremental script for existing databases.
2. Deploy API with the new `ConfigurationController`/`ConfigurationRepository`/`ConfigurationRecord` and `SimulationRequest.Environment`; keep `Simulation:*Queue` settings as seed defaults.
3. Deploy frontend with the new admin configuration screen and the simulation dropdown.
4. Verify a run against a seeded `SimulationEnvironment` config reaches the correct queues.
- **Rollback**: revert API/frontend; the table can remain unused. No destructive changes to existing tables.
