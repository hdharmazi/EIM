## Why

The simulation console's target queues are hardcoded in `appsettings.json` (`Simulation:D365Queue`, `Simulation:CaptureToD365Queue`, `Simulation:CaptureToD365ResponseQueue`, `Simulation:D365ToCaptureQueue`). Running a simulation against a different environment (BAT, DEV, TEST — which use `bat-*`, `dev-*`, `test-*` queue names on the same Service Bus namespace) requires editing settings and redeploying. Users need to configure named simulation targets in the UI and pick one per run. Rather than a single-purpose table, the configuration should be stored in a **generic, reusable** store so future features/projects can define their own named configurations without new schema.

## What Changes

- Add a new **generic configuration store**: a single table `CONFIGURATIONTABLE` keyed by `(CATEGORY, NAME)` whose editable fields are stored as a JSON `VALUE` (dynamic key/value pairs, not fixed columns). This is reusable across features and future projects.
- Add a generic `/api/configuration` API (list by category for selection + admin CRUD) and an admin-only screen with a key/value editor to manage configurations.
- Model **simulation environments** as configurations under `CATEGORY = "SimulationEnvironment"`, where each config's fields include the four Service Bus queue names (`D365Queue`, `CaptureToD365Queue`, `CaptureToD365ResponseQueue`, `D365ToCaptureQueue`). No connection string is stored — the existing server-side `Simulation:ServiceBusConnectionString` is reused for all environments.
- Update the **Simulation** page: replace the static, read-only configuration card with an **Environment** dropdown listing configurations in the `SimulationEnvironment` category; selecting one auto-populates the four queue names. The chosen environment is sent per run; picking does not change a shared/global setting.
- The simulation Run action is **blocked** until an environment is selected; if none exist, the user is prompted to create one.
- Seed the store from the current `appsettings.json` values so behavior is preserved after deploy.
- Reorganize the sidebar navigation: move the existing **Environments** item into the **System Administration** section (alongside Users and the new Configuration item) and remove the now-empty Configuration nav section. The **Environments** feature becomes **admin-only** (nav, routes, and API), matching the rest of System Administration.

## Capabilities

### New Capabilities
- `configuration-store`: A generic, reusable store of named configurations grouped by category, each with dynamic key/value fields, managed via an admin-only CRUD screen and API.
- `simulation-environment`: Modeling simulation targets as `SimulationEnvironment`-category configurations and selecting one per simulation run in the simulation console.

### Modified Capabilities
<!-- The simulation-console capability lives in the in-progress convert-dashboard-to-react change, not in openspec/specs/. Its environment-selection behavior is covered here under the simulation-environment capability to avoid coupling to an unmerged change. -->

## Impact

- **Database**: new generic table `CONFIGURATIONTABLE`; `Script/SQL/Install.sql` gains the DDL + seed rows for the `SimulationEnvironment` category.
- **API** (`ARM.Integration.Dashboard.API`): new `ConfigurationRecord` model (with a `Fields` dictionary serialized to/from JSON `VALUE`), `ConfigurationRepository`, `ConfigurationController`; `SimulationService`/`SimulationController` resolve queues from the selected configuration's fields instead of `IConfiguration`; `SimulationRequest` gains an `environment` field.
- **Frontend** (`ARM.Integration.Dashboard.Frontend`): new generic `configuration` feature (api hooks, list page, form page with a key/value editor reusing the `JsonValueEditor` pattern), new routes, new admin-only nav item; `SimulationPage.tsx` gains the environment dropdown and per-run wiring.
- **Auth**: CRUD endpoints and screen are admin-only (mirrors Users); listing configurations for selection is available to all validated users.
- **Config**: `Simulation:ServiceBusConnectionString` remains the single server-side connection; the four `Simulation:*Queue` settings become seed defaults rather than the runtime source of truth.
