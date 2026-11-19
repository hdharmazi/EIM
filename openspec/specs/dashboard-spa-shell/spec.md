# dashboard-spa-shell Specification

## Purpose

Define the standalone React SPA shell: no-.NET static hosting, MSAL.js browser authentication, API-side bearer token validation and user resolution, the identity endpoint, and the application navigation shell.
## Requirements

### Requirement: Standalone SPA with no .NET frontend
The system SHALL deliver the dashboard as a standalone React + Vite single-page application containing no .NET. The SPA SHALL be served as static assets (nginx container) with client-side routing fallback to `index.html`, and SHALL call `ARM.Integration.Dashboard.API` directly over HTTPS.

#### Scenario: SPA served statically
- **WHEN** a browser requests the application root or a deep client route
- **THEN** the static host returns `index.html` and the SPA router renders the requested view

#### Scenario: No .NET in the frontend tier
- **WHEN** the frontend is built and deployed
- **THEN** it consists only of static assets served by nginx, with no .NET runtime or BFF involved

### Requirement: Browser authentication via MSAL.js
The SPA SHALL authenticate users with Entra ID using MSAL.js, acquiring an access token for the API and sending it as an `Authorization: Bearer` header on API requests.

#### Scenario: Unauthenticated user signs in
- **WHEN** an unauthenticated user opens the SPA
- **THEN** MSAL initiates an Entra sign-in redirect and, on return, the app has an access token

#### Scenario: Token attached to API calls
- **WHEN** the SPA calls any `/api/*` endpoint
- **THEN** it attaches a valid Entra bearer token acquired (silently when possible) via MSAL

#### Scenario: Expired session triggers re-auth
- **WHEN** an API call returns 401
- **THEN** the SPA triggers an MSAL login redirect rather than failing silently

#### Scenario: Unauthorized user sees access denied
- **WHEN** an API call returns 403 (authenticated but not DB-validated / insufficient role)
- **THEN** the SPA shows an access-denied view

### Requirement: API validates bearer tokens and resolves the user
`ARM.Integration.Dashboard.API` SHALL validate Entra JWT bearer tokens, resolve the caller to a dashboard user (by email), and expose their roles and validation status. Bearer token validation SHALL be the only supported authentication mechanism; the API SHALL NOT accept an API key or any other credential that bypasses Entra. Inactive or unknown users SHALL be denied.

#### Scenario: Valid token accepted
- **WHEN** a request carries a valid Entra bearer token for an active dashboard user
- **THEN** the API authorizes the request and resolves the user's role

#### Scenario: Inactive or unknown user denied
- **WHEN** a request carries a valid token for a user who is inactive or not present in the users table
- **THEN** the API responds 403

#### Scenario: API key is no longer accepted
- **WHEN** a caller sends a request carrying an `X-Api-Key` header and no bearer token
- **THEN** the API rejects the request as unauthenticated

#### Scenario: Every caller resolves to a dashboard user
- **WHEN** any request is authorized
- **THEN** the caller has been resolved to an active row in the users table, with no authorization path that skips that resolution

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
