## Why

The EIM Dashboard front-end (`ARM.Integration.Dashboard.Web`) is a server-rendered ASP.NET MVC + Razor app using jQuery and Bootstrap. The goal is a modern, world-class **pure React** single-page application with **no .NET in the frontend at all**, while keeping the data API in .NET. The React SPA authenticates directly with Entra ID via MSAL.js and calls the .NET API with JWT bearer tokens.

## What Changes

- Build a standalone **Vite + React + TypeScript** SPA (**shadcn/ui + Tailwind CSS**, **TanStack Query/Router/Table**, **React Hook Form + Zod**, **Recharts**) as a new frontend project (`ARM.Integration.Dashboard.Frontend`) with **no .NET**.
- **Remove** the `ARM.Integration.Dashboard.Web` .NET project entirely (MVC, Razor, cookie/BFF auth). **BREAKING**: the server-rendered dashboard and its BFF are removed.
- **Rework `ARM.Integration.Dashboard.API`** to authenticate the SPA: add **JWT bearer** validation (Microsoft.Identity.Web), re-home the `DbUserValidated`/role check into the API, add **CORS**, add **`GET /api/me`**, and enforce **Admin** on user-management endpoints. Retain **`X-Api-Key`** for non-interactive service callers (dual scheme).
- **Auth in the browser** via **MSAL.js**: sign-in redirect, silent token acquisition, bearer attach, `401`→login redirect / `403`→access denied.
- Consolidate per-status navigation into a single **Dashboard** landing screen with status/overview cards. **BREAKING** (IA): standalone status pages removed.
- Port all screens in one pass (big-bang): Message Queue list/detail/cleanup, Environments CRUD, Users CRUD, and Simulation (SSE + Excel import).
- Serve the SPA from an **nginx** static container, wired into docker-compose/Bicep.

## Capabilities

### New Capabilities
- `dashboard-spa-shell`: The standalone React SPA shell — MSAL.js auth, routing, layout (sidebar + topbar), theming, token-attached API client, and the nginx static-hosting contract.
- `dashboard-overview`: The unified Dashboard landing screen aggregating message status counts, recent failures, service-bus/simulation health, and environment/user counts.
- `message-queue-management`: React screens for browsing/filtering messages, message detail (payload view/edit, reprocess, processing steps), and cleanup.
- `environment-management`: React screens for listing, filtering, creating, editing, deleting environment records, with masked value reveal.
- `user-management`: React screens (Admin-only, server-enforced) for listing, creating, editing, deleting dashboard users and roles.
- `simulation-console`: React screen for the integration simulation — scenario picker, dynamic fields, Excel sales-order import, and real-time SSE output streaming with bearer auth + CORS.

### Modified Capabilities
<!-- None: no existing openspec/specs cover the dashboard front-end or the API's auth. -->

## Impact

- **Removed**: `ARM.Integration.Dashboard.Web` (.NET) project — deleted from the solution, docker-compose, and Bicep.
- **New**: `ARM.Integration.Dashboard.Frontend` standalone React project + nginx `Dockerfile`.
- **Changed**: `ARM.Integration.Dashboard.API` — adds JWT bearer auth, user/role + `DbUserValidated` resolution, CORS, `GET /api/me`, Admin enforcement; keeps `X-Api-Key` for service callers.
- **Auth/config**: Entra app registrations — an API app exposing a scope, and a SPA public-client app with redirect URIs; SPA reads client/tenant/scope + API base URL from `VITE_*` env.
- **API surface consumed**: `api/messagequeue` (list, `status-counts`, `recent-failed`, `{id}`, `{id}/log-details`, `{id}/payload`, `{id}/reprocess`, `cleanup`), `api/environment` CRUD, `api/user` CRUD, `api/simulation` (`config`, `run` SSE, `salesorder/parse`), plus new `api/me`.
- **Tooling/ops**: Node/Vite build for the frontend; nginx container image; docker-compose + Bicep updated.
