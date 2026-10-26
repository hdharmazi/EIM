# Scripts

## Deployment

| Script | Does |
|---|---|
| `deployment/Deploy-Wilson.ps1` | deploys one configuration (`bat`, `test`, `prd`): infrastructure, SQL server, settings, schema, database users, code, verify and health. It creates what is missing and deletes no Azure resource |
| `deployment/New-DeploymentPackage.ps1` | builds the package the deploy script consumes (zips, migrator, manifest) |
| `deployment/Test-DeployWilson.ps1` | dependency-free self-test of both scripts; with `-PackagePath`, also checks a built package |

```powershell
# Build a package
.\script\deployment\New-DeploymentPackage.ps1 -Environment bat, test -Version local

# Preview everything for bat
.\script\deployment\Deploy-Wilson.ps1 -Environment bat -Component all -WhatIf -PackagePath artifacts\package\wtc-eim-deploy-local

# Self-test
pwsh -NoProfile -File script/deployment/Test-DeployWilson.ps1
```

The configurations are in `deployment/config/`: `deploy.base.json` plus one `deploy.<name>.json` per
configuration. `bat` and `prd` own their resources; `test` has `"uses": "bat"` and deploys only its queues and
Automation app. A configuration with `uses` may also own its Key Vault and Log Analytics workspace by setting
`names.keyVault` or `names.logAnalytics` (see deployment.md). They are tracked so names and non-secret settings can be reviewed, and they hold no secrets. The
script needs none either: the apps reach SQL with their managed identities, the deployer signs in with its
`az login`, and the Service Bus connection is kept in Key Vault.

The Service Bus queues each configuration creates and binds to are listed in `serviceBusQueues` in
`deploy.base.json`; see [Service Bus queues](../docs/deployment/deployment.md#service-bus-queues).

The pipelines that run these are `pipelines/eim.yml` and `pipelines/eim-prd.yml`. See [docs/deployment/deployment.md](../docs/deployment/deployment.md).

## Database

- `SQL/Migrations/` contains ordered, one-time migrations.
- `SQL/Repeatable/` contains scripts applied on every migration run.
- `SQL/_scratch/` is never embedded or deployed.

See [database migration guidance](../docs/deployment/database-migrations.md).

## Other utilities

- `Create-AiPullRequest.ps1` supports the feature-branch pull-request pipeline.
- `Repair-TruncatedSourceId.ps1` is a targeted data repair utility.
- `Superseded/` contains historical deployment assets and must not be used for current deployments.
