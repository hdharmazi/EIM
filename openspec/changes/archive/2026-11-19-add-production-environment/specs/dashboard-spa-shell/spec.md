## MODIFIED Requirements

### Requirement: Identity endpoint
The API SHALL expose `GET /api/me`, returning the following, derived from the validated token, the user and the API's configuration, for the SPA to adapt its UI:
- the current user's display name
- roles
- `dbUserValidated` status
- a `features` object with a boolean `simulation`, which is `true` only when simulation is enabled on this API

The SPA SHALL treat a missing `features` object or a missing `features.simulation` value as `false`.

#### Scenario: Identity retrieved on bootstrap
- **WHEN** the SPA starts and has a token
- **THEN** it retrieves name, roles, validated flag and features from `/api/me` and adapts navigation and features

#### Scenario: Simulation feature reported
- **WHEN** `Simulation:Enabled` is `true` on the API and a validated user calls `GET /api/me`
- **THEN** the response has `features.simulation` equal to `true`

#### Scenario: Simulation feature off by default
- **WHEN** `Simulation:Enabled` is absent on the API and a validated user calls `GET /api/me`
- **THEN** the response has `features.simulation` equal to `false`

### Requirement: Application shell and navigation
The SPA SHALL provide a consistent shell with a sidebar and topbar, client-side routing, and a theme. Navigation SHALL expose:
- Dashboard, Message Queue and Environments
- Simulation, only when `/api/me` reports `features.simulation` as `true`
- Users, for Admin users only

It SHALL NOT include separate per-status navigation entries.

#### Scenario: Shell renders with navigation
- **WHEN** a validated user loads the SPA and simulation is enabled
- **THEN** the sidebar shows Dashboard, Message Queue, Environments, and Simulation entries and the topbar shows the signed-in user's name and a sign-out action

#### Scenario: Simulation navigation hidden when off
- **WHEN** a validated user loads the SPA and `/api/me` reports `features.simulation` as `false`
- **THEN** the sidebar shows no Simulation entry and the other entries are unchanged

#### Scenario: Admin-only Users navigation
- **WHEN** the signed-in user has the Admin role
- **THEN** the Users navigation entry is shown; otherwise it is hidden and the API still enforces authorization on user endpoints
