## Context

`ARM.Integration.Dashboard.Web` was a server-rendered ASP.NET Core MVC + Razor app (jQuery + Bootstrap) that forwarded data operations to `ARM.Integration.Dashboard.API` using an `X-Api-Key` header, with Entra ID cookie auth and a `DbUserValidated` claim/authorization gate.

The requirement is a **pure React frontend with no .NET at all**, keeping the API in .NET. This removes the .NET web/BFF tier entirely: the SPA is a standalone static app that authenticates directly with Entra via MSAL.js and calls the .NET API with JWT bearer tokens.

## Goals / Non-Goals

**Goals:**
- A standalone React + Vite + TypeScript SPA (shadcn/ui + Tailwind) with **no .NET hosting or BFF**.
- Browser-based auth via **MSAL.js** (Entra ID), sending Entra access tokens to the API.
- Rework `Dashboard.API` to validate **JWT bearer tokens**, enforce roles, and own the `DbUserValidated` check.
- Consolidate status views into one Dashboard landing screen.
- Migrate all screens at once (big-bang), Simulation SSE proven first.

**Non-Goals:**
- Keeping any .NET in the frontend tier (the `Dashboard.Web` project is removed).
- Redesigning the database or the API's business/data logic.
- Adding new business features beyond parity + the consolidated dashboard.

## Decisions

### Decision 1: Pure SPA + MSAL.js + JWT on the API (Option B)
The frontend is a standalone React SPA with no .NET. Authentication is performed in the browser with **MSAL.js** against Entra ID; the SPA acquires an access token for the API and sends it as `Authorization: Bearer <token>`. `Dashboard.API` validates the token, resolves the user, and enforces authorization.
- **Why:** the frontend must contain no .NET, which rules out the cookie-based BFF. Direct SPA→API with bearer tokens is the standard pattern for a static SPA + API.
- **Consequence:** the `DbUserValidated`/role logic moves out of the removed BFF and into the API.

### Decision 2: Remove the `Dashboard.Web` .NET project
The `ARM.Integration.Dashboard.Web` project (MVC, YARP proxy, cookie auth, `/api/me`) is deleted. The React app becomes a standalone project (`ARM.Integration.Dashboard.Frontend`).

### Decision 3: Stack — Vite + React + TypeScript + shadcn/ui + Tailwind
- Auth: **@azure/msal-browser + @azure/msal-react**.
- Data fetching/caching: **TanStack Query**.
- Routing: **React Router**. Tables: **TanStack Table**. Forms: **React Hook Form + Zod**. Charts: **Recharts**. Icons: **lucide-react**.

### Decision 4: API auth rework (`Dashboard.API`)
- Add **Microsoft.Identity.Web** JWT bearer validation (audience = API app registration).
- Add a `DbUserValidated`/role resolution step from the token's email against the users table (re-homed from the old BFF claims transformation), exposed to authorization.
- Keep `X-Api-Key` support for non-interactive service callers (dual scheme): JWT for the SPA, API key for service-to-service. Endpoints requiring a user identity require JWT.
- Add **CORS** allowing the SPA origin(s).
- Add a **`GET /api/me`** endpoint returning name, roles, and `dbUserValidated` from the validated token/user.
- Enforce **Admin** role on the user-management endpoints server-side.

### Decision 5: SPA auth flow and 401 handling
- MSAL handles sign-in (redirect flow) and silent token acquisition. A shared fetch wrapper attaches the bearer token and, on `401`, triggers an MSAL login redirect; on `403`, shows an access-denied view.

### Decision 6: Hosting — nginx static container
The built SPA is served by a small **nginx container** (SPA fallback to `index.html`), deployed like the other services via docker-compose/Bicep. API base URL and Entra client/tenant/scope are injected via build-time `VITE_*` env (or a runtime `config.js`).
- **Alternative considered:** Azure Static Web Apps — rejected to avoid a separate deployment paradigm and redundant built-in auth.

### Decision 7: Consolidated Dashboard IA
Per-status nav entries collapse into one Dashboard with status cards from `api/messagequeue/status-counts` + `recent-failed`, plus environment/user counts and simulation `config` health. Drill-downs use a status query param into the Message Queue list.

### Decision 8: Simulation SSE first (vertical slice)
The Simulation screen (`POST api/simulation/run`, `text/event-stream`) is the highest-risk port and is built first. With direct SPA→API calls (no proxy), SSE uses a streaming `fetch` reader with the bearer token; CORS must allow the streaming response.

## Risks / Trade-offs

- **Tokens in the browser** → Use short-lived access tokens via MSAL, silent renewal, no token persistence beyond MSAL's cache; rely on Entra + API validation.
- **API auth rework regressions** → Add JWT alongside existing API key (dual scheme) so service callers keep working; test both paths.
- **CORS + SSE** → Ensure CORS allows the SPA origin and the streaming `text/event-stream` response; verify with the Simulation slice first.
- **`DbUserValidated` re-home** → Port the exact active/role lookup semantics from the old claims transformation into the API; deny inactive/unknown users with 403.
- **Two app registrations / scopes** → SPA (public client, redirect URIs) and API (exposes a scope/audience); document the Entra config.
- **Big-bang cutover** → Screen-by-screen parity checklist against the old Razor views.

## Migration Plan

1. Rework `Dashboard.API`: JWT bearer (Microsoft.Identity.Web) + dual scheme with API key, user/role resolution + `DbUserValidated`, CORS, `GET /api/me`, Admin enforcement on user endpoints.
2. Relocate the React app to a standalone `ARM.Integration.Dashboard.Frontend` project (no .NET).
3. Add MSAL.js auth (provider, login redirect, token acquisition, bearer attach, 401→login/403→denied).
4. Point the SPA data layer at the API base URL (env-configured); remove BFF/proxy assumptions.
5. Validate the Simulation SSE slice end-to-end (bearer + CORS + streaming).
6. Verify all screens against parity.
7. Add the nginx `Dockerfile` + wire into docker-compose/Bicep; remove the `Dashboard.Web` project from the solution/compose/Bicep.
- **Rollback:** the API's JWT is additive (API key retained); reverting the frontend project and API auth additions restores prior behavior.

## Open Questions

- Confirm the Entra app registration model: single API app exposing a scope consumed by a separate SPA public-client app (recommended), including redirect URIs and the requested scope value.
- Confirm allowed SPA origin(s) for CORS across dev and each deployed environment.
