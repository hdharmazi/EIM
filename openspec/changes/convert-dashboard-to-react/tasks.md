## 1. API auth rework (Dashboard.API)

- [x] 1.1 Add `Microsoft.Identity.Web` JWT bearer validation (audience/scope from Entra) to `Dashboard.API`, kept alongside the existing `X-Api-Key` as a dual scheme
- [x] 1.2 Re-home the `DbUserValidated`/role resolution into the API: resolve the caller by token email against the users table, attach role claims, deny inactive/unknown users with 403
- [x] 1.3 Add CORS allowing the SPA origin(s) (dev + deployed), including the streaming `text/event-stream` response
- [x] 1.4 Add `GET /api/me` returning display name, roles, and `dbUserValidated`
- [x] 1.5 Enforce Admin role on the user-management endpoints server-side

## 2. SPA scaffold (standalone, no .NET)

- [x] 2.1 Scaffold the Vite + React + TypeScript app
- [x] 2.2 Add Tailwind CSS + shadcn/ui, lucide-react, and base theme
- [x] 2.3 Add TanStack Query, React Router, TanStack Table, React Hook Form + Zod, Recharts
- [x] 2.4 Relocate the app to a standalone `ARM.Integration.Dashboard.Frontend` project (no .NET); API base URL + Entra config via `VITE_*` env
- [x] 2.5 Add MSAL (`@azure/msal-browser` + `@azure/msal-react`): `MsalProvider`, login redirect, and silent access-token acquisition for the API scope

## 3. SPA auth & API client

- [x] 3.1 Rework the shared API client to prefix the API base URL and attach the MSAL bearer token; `401` → MSAL login redirect, `403` → access-denied view
- [x] 3.2 Attach the bearer token to the Simulation SSE streaming `fetch`
- [x] 3.3 Bootstrap identity from `/api/me` and expose it to the app (auth-aware context)

## 4. Simulation console (vertical slice — build first)

- [x] 4.1 Build the Simulation screen: config card, scenario picker with dynamic fields, output panel
- [x] 4.2 Implement `POST api/simulation/run` SSE consumption with incremental, color-coded log output
- [ ] 4.3 Verify SSE streams end-to-end with bearer auth + CORS (no buffering)
- [x] 4.4 Implement sales order Excel import via `POST api/simulation/salesorder/parse` and wire parsed data into a run
- [x] 4.5 Add clear-output and run-enable/disable behavior

## 5. Application shell

- [x] 5.1 Build the layout shell: sidebar + topbar (user name, sign-out) + content area + theme
- [x] 5.2 Configure client-side routes for Dashboard, Message Queue (list/detail/cleanup), Environments, Simulation, Users
- [x] 5.3 Gate the Users nav on Admin role from `/api/me` (API still enforces)
- [x] 5.4 Remove per-status nav entries; status drill-downs use query params into the Message Queue list
- [x] 5.5 Wire sign-out to MSAL logout (replace the BFF sign-out link)

## 6. Dashboard overview

- [x] 6.1 Status cards for message counts from `api/messagequeue/status-counts`
- [x] 6.2 Recent-failures panel from `api/messagequeue/recent-failed`
- [x] 6.3 Service Bus / simulation health from `api/simulation/config` and environment/user counts
- [x] 6.4 Status-card clicks navigate to the filtered Message Queue list; manual refresh

## 7. Message Queue screens

- [x] 7.1 Message list with status filter, search, error-row highlighting, payload preview
- [x] 7.2 Initialize the list filter from a status query param (dashboard deep link)
- [x] 7.3 Message detail with JSON pretty-print and processing steps
- [x] 7.4 Payload edit/save via `api/messagequeue/{id}/payload`
- [x] 7.5 Reprocess via `api/messagequeue/{id}/reprocess` with confirmation and result feedback
- [x] 7.6 Cleanup screen: status counts, filtered delete via `api/messagequeue/cleanup` with confirmation

## 8. Environment & User screens

- [x] 8.1 Environment list with filters and masked value reveal
- [x] 8.2 Environment create/edit/delete (RHF + Zod, confirmation)
- [x] 8.3 User list (Admin) from `api/user`
- [x] 8.4 User create/edit/delete with confirmation

## 9. Hosting, cutover & cleanup

- [x] 9.1 Remove Razor `Views/**`, `wwwroot/lib` (jQuery/Bootstrap), `site.js`, and obsolete MVC controllers/services/models
- [x] 9.2 Remove the `ARM.Integration.Dashboard.Web` .NET project entirely (solution, references) — frontend has no .NET
- [x] 9.3 Add an nginx `Dockerfile` for the frontend (SPA fallback) and wire it into docker-compose/Bicep; remove the old `Dashboard.Web` compose/Bicep entries
- [ ] 9.4 Configure runtime API base URL + Entra settings for each environment
- [ ] 9.5 Parity pass: verify each screen matches prior behavior; validate `openspec validate convert-dashboard-to-react` and update repo memory
