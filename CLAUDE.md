# ARM Integration Wilson

Integration services moving data between Capture, Microsoft Dynamics 365 Finance and Supply Chain Management (D365 F&SC), and the operations dashboard. See `README.md` for local setup and deployment links.

## Projects

| Project | Role |
|---|---|
| `ARM.Integration.Core` | Shared helpers and models (e.g. `EntityKey`, `MissingEntityKeyException`) |
| `ARM.Integration.EIM` | Integration engine: orchestration base classes, routing, ingest/hold gates, retry and failure classification |
| `Wilson.Integration.Orchestrations` | Concrete orchestrations (`<Entity>_To_<Target>`), Capture contracts, and `OrchestrationRegistry` |
| `ARM.Integration.EIM.Customer` | Azure Functions worker (Service Bus + timer sweeps) |
| `ARM.Integration.D365FSC.Gateway` | Shared D365 F&SC HTTP gateway (auth, idempotency, forwarding) |
| `ARM.Integration.Dashboard.API` | Dashboard and simulation API; also reprocesses messages |
| `ARM.Integration.Dashboard.Frontend` | React 18 + Vite + TypeScript + Tailwind/Radix UI |
| `ARM.Integration.DbMigrator` | DbUp runner for `script/SQL/Migrations` and `script/SQL/Repeatable` |
| `*.Tests` | xUnit tests for EIM and Gateway |

Stack: .NET 10, Azure Functions isolated worker, Azure Service Bus, Azure SQL (`eimdb`), Node 22.

## Commands

```bash
dotnet build ARM.Integration.Wilson.slnx
dotnet test ARM.Integration.EIM.Tests/ARM.Integration.EIM.Tests.csproj
dotnet test ARM.Integration.D365FSC.Gateway.Tests/ARM.Integration.D365FSC.Gateway.Tests.csproj
npm --prefix ARM.Integration.Dashboard.Frontend run lint     # tsc --noEmit
npm --prefix ARM.Integration.Dashboard.Frontend run build
```

## Conventions and pitfalls

- **Orchestrations**: one class per message type in `Wilson.Integration.Orchestrations/Orchestrations`, registered in `OrchestrationRegistry.CreateDefault()`. The registry is shared by the Functions host and the Dashboard API, so a type missing there breaks both dispatch and reprocessing. New routes also need a seed migration (see `014_seed_customer_address_delete_route.sql`).
- **D365 keys**: build entity keys with `EntityKey`. Missing or blank key values throw `MissingEntityKeyException`, which is classified as terminal. Never send an update/delete with an empty key.
- **D365 payloads**: only send fields the target data entity actually exposes (see the PartyContactsV3 WTC field hotfix).
- **Failure classification**: new exception types must be classified in `DefaultFailureClassifier` (terminal vs transient) and covered in `FailureClassifierTests`.
- **SQL**: versioned scripts go in `script/SQL/Migrations/NNN_description.sql` with the next number. `CREATE OR ALTER` objects go in `script/SQL/Repeatable/R__<object>.sql`. Never rename or edit an applied migration. See `docs/deployment/database-migrations.md`.
- **Secrets**: never commit connection strings, keys, or `local.settings.json` values.
- **Branches**: `feature/<ticket>/<slug>` or `bug/<ticket>/<slug>`. Commit messages start with `#<ticket>` when there is one.

## Spec-driven workflow (OpenSpec)

Specs of current behaviour live in `openspec/specs/<capability>/spec.md`. Every non-trivial change gets a folder in `openspec/changes/<change-id>/` containing `proposal.md`, `design.md`, `tasks.md`, and spec deltas under `specs/`. Completed changes are archived with `/opsx:archive`. Use the `openspec` CLI (`openspec list`, `openspec validate <id>`, `openspec status --change <id>`).

## Agent team

| Agent | Role | Writes to |
|---|---|---|
| `mario` | Architect/planner: designs the change as an OpenSpec proposal | `openspec/changes/<id>/` only |
| `luigi` | Developer: implements `tasks.md` and keeps the build and tests green | Source, tests, SQL, `tasks.md` checkboxes |
| `bowser` | Reviewer: checks the diff against the spec and runs the tests | Nothing (read-only) |

Subagents cannot call each other, so the main session orchestrates:

1. **Plan**: delegate to `mario` with the request and ticket number. Show the user the proposal summary and any open questions, then **wait for approval** before any code is written.
2. **Build**: delegate to `luigi` with the change id.
3. **Review**: delegate to `bowser` with the change id. If the verdict is `CHANGES REQUESTED`, send the findings back to `luigi`, then re-review. After two rounds without approval, stop and ask the user.
4. **Finish**: report the results to the user. Commit, push, PR creation, and `/opsx:archive` happen only when the user asks.
