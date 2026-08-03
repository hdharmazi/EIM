## 1. Database

- [x] 1.1 Add generic `CONFIGURATIONTABLE` DDL to `Script/SQL/Install.sql` (composite PK `(CATEGORY, NAME)`, `DISPLAYNAME`, JSON `VALUE`, `ISACTIVE`, `CREATEDDATETIME` defaults)
- [x] 1.2 Add seed `INSERT` rows for category `SimulationEnvironment` (BAT, DEV) with JSON `VALUE` using current `appsettings.json` queue values
- [x] 1.3 Add an incremental migration script (create table + seed) for existing databases

## 2. API — Model & Repository (generic)

- [x] 2.1 Add `ConfigurationRecord` model (Category, Name, DisplayName, `Dictionary<string,string> Fields`, IsActive) with JSON (de)serialization to/from the `VALUE` column
- [x] 2.2 Create `ConfigurationRepository` mirroring `EnvironmentRepository` (raw `SqlCommand`): `GetByCategoryAsync(category, activeOnly)`, `GetByKeyAsync(category, name)`, `CreateAsync`, `UpdateAsync`, `DeleteAsync`
- [x] 2.3 Register `ConfigurationRepository` in DI (`Program.cs`)

## 3. API — Controller & Simulation wiring

- [x] 3.1 Create `ConfigurationController`: `GET /api/configuration?category={category}` (active only, any validated user)
- [x] 3.2 Add admin-only CRUD to the controller: `GET/POST/PUT/DELETE /api/configuration/{category}/{name}` (guard like Users), with duplicate-name-within-category conflict handling
- [x] 3.3 Add a well-known `SimulationEnvironment` category constant and `Environment` (config name) field to `SimulationRequest`
- [x] 3.4 Update `SimulationService` to resolve the four queue names from the selected `SimulationEnvironment` config's fields (via `ConfigurationRepository`) instead of `IConfiguration`, keeping `SbConn` from config; return a clear error if the config or a required field is missing
- [x] 3.5 Update `SimulationController.Run` to pass/validate the selected environment; retain `GET /api/simulation/config` for the badge or fold `IsServiceBusConfigured` into the simulation page

## 4. Frontend — Generic configuration admin screen

- [x] 4.1 Create `features/configuration/api.ts` with react-query hooks (list-by-category, get, create, update, delete) mirroring `environment/api.ts`
- [x] 4.2 Create `ConfigurationListPage.tsx` (table of configs, category filter, New/Edit/Delete) mirroring `EnvironmentListPage`
- [x] 4.3 Create `ConfigurationFormPage.tsx` (category, name, display name, active toggle, and a dynamic key/value field editor reusing the `JsonValueEditor` pattern) with validation
- [x] 4.4 Add routes to `router.tsx` wrapped in `RequireAdmin` (list, new, edit)
- [x] 4.5 Add an `adminOnly` nav item "Configuration" under the **Administration** (System Administration) section in `AppShell.tsx`, alongside Users
- [x] 4.6 Move the existing **Environments** nav item from the "Configuration" section into the **Administration** (System Administration) section, and remove the now-empty "Configuration" section in `AppShell.tsx`
- [x] 4.7 Make **Environments** admin-only: mark its nav item `adminOnly`, wrap its routes in `RequireAdmin` in `router.tsx`, and enforce admin authorization on `EnvironmentController` (server-side, like `UserController`)

## 5. Frontend — Simulation page selection

- [x] 5.1 Replace the static configuration card in `SimulationPage.tsx` with an Environment dropdown populated from `GET /api/configuration?category=SimulationEnvironment`
- [x] 5.2 On selection, display the four derived queue names (from the config fields) read-only
- [x] 5.3 Send the selected environment (config name) with the run request; disable Run until an environment is selected
- [x] 5.4 Show an empty-state prompt to create a `SimulationEnvironment` config (with a link/CTA for admins) when the list is empty
- [x] 5.5 Update `features/simulation/api.ts` types to include the environment field and any config-response changes

## 6. Verification

- [x] 6.1 Build API and frontend; resolve type/compile errors
- [ ] 6.2 Manually verify: admin creates/edits/deletes a `SimulationEnvironment` config with dynamic fields; non-admin cannot access the screen
- [ ] 6.3 Manually verify: selecting an environment populates queues and a run reaches the correct queues; Run is blocked with no selection; missing-field error is clear
- [ ] 6.4 Verify seeded configs preserve current behavior after deploy
