# ARM Integration Wilson

Integration services for moving data between Capture, Microsoft Dynamics 365 Finance and Supply Chain Management, and the shared operations dashboard.

## Components

| Component | Purpose | Local URL |
|---|---|---|
| `ARM.Integration.Dashboard.API` | Dashboard and simulation API | `http://localhost:5060` |
| `ARM.Integration.Dashboard.Frontend` | React/Vite operations UI | `http://localhost:5173` |
| `Wilson.Integration.Automation` | Service Bus and timer-triggered integration worker | Functions host on port `7071` |
| `ARM.Integration.D365FSC.Gateway` | Shared D365 F&SC HTTP gateway | `http://localhost:7072` |
| `ARM.Integration.DbMigrator` | Applies versioned and repeatable SQL migrations | Command line |

Local Functions use Azurite on ports `10000` through `10002`. Deployed nonprod environments share the Dashboard, Gateway, and `eimdb`; Wilson Automation has separate `dev`, `bat`, and `test` Function Apps.

## Prerequisites

- .NET 10 SDK
- Node.js 22 and npm
- Azurite available on `PATH`
- Access to the required Azure SQL database and Service Bus namespace
- Microsoft Entra application settings for Dashboard authentication

Keep connection strings, keys, and environment-specific settings out of source control. Review each project's development settings before starting; the launch scripts do not create queues, run database migrations, or populate secrets.

## Local setup

Restore .NET and frontend dependencies:

```powershell
dotnet restore ARM.Integration.Wilson.slnx
npm --prefix ARM.Integration.Dashboard.Frontend ci
```

Start the full local stack:

```powershell
.\run.bat
```

This starts Azurite, the Dashboard API and frontend, Wilson Automation (port `7071`), and the D365 F&SC Gateway (port `7072`) in separate windows. To run only the Dashboard:

```powershell
.\run-dashboard.bat
```

### Local settings

Each Functions project reads `local.settings.json`, which is gitignored and never committed. Start from the tracked sample and fill in the placeholders:

```powershell
Copy-Item Wilson.Integration.Automation\local.settings.sample.json Wilson.Integration.Automation\local.settings.json
Copy-Item ARM.Integration.D365FSC.Gateway\local.settings.sample.json ARM.Integration.D365FSC.Gateway\local.settings.json
```

If you already have a `local.settings.json` in the worker's pre-rename folder (the old EIM Customer project), move it to `Wilson.Integration.Automation\local.settings.json` and remove `PollSchedule`, which nothing reads. The old folder then holds only build output and can be deleted.

To keep several settings files side by side, name them `local.settings.<name>.json` (also gitignored) and select one at build time; it is copied to the output as `local.settings.json`:

```powershell
dotnet build Wilson.Integration.Automation -p:LocalSettings=mydev
```

The Automation host refuses to start, naming each missing setting, when a required setting is absent.

## Database migrations

Database migration is required for every new `eimdb`. Creating an Azure SQL database produces an empty catalog; the applications do not create tables at startup. Read [database migration guidance](docs/deployment/database-migrations.md) before deploying or changing schema.

Run the migrator directly with either `--connection` or `EIMDB_MIGRATOR_CONNECTION`:

```powershell
dotnet run --project ARM.Integration.DbMigrator -- --connection "<connection-string>"
```

## Tests

```powershell
dotnet test ARM.Integration.EIM.Tests/ARM.Integration.EIM.Tests.csproj
dotnet test Wilson.Integration.Tests/Wilson.Integration.Tests.csproj
dotnet test ARM.Integration.D365FSC.Gateway.Tests/ARM.Integration.D365FSC.Gateway.Tests.csproj
npm --prefix ARM.Integration.Dashboard.Frontend run lint
```

## Deployment

Non-production is deployed by one pipeline, [`pipelines/eim.yml`](pipelines/eim.yml), running one script,
`script/deployment/Deploy-Wilson.ps1`, with configurations in `script/deployment/config/`. The pipeline builds one
package and promotes it through `Deploy_shared` (gateway, dashboard, migrations), then `dev`, `bat` and `test`
(Wilson Automation). A merge to `main` deploys code only; infrastructure, settings and migrations run on a manual
run.

- [Deployment overview](docs/deployment/deployment.md)
- [Azure DevOps setup](docs/deployment/devops-setup.md)
- [Resource rename and cutover (#5246)](docs/deployment/resource-rename-cutover.md)
- [Deploying from scratch](docs/deployment/deploy-from-scratch.md)
- [Database migrations](docs/deployment/database-migrations.md)
- [D365 F&SC Gateway operations](docs/deployment/d365fsc-gateway.md)
