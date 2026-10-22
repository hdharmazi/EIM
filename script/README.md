# Scripts

## Deployment

| Script | Does |
|---|---|
| `deployment/Deploy-Wilson.ps1` | deploys one configuration (`nonprod`, `dev`, `bat`, `test`): infrastructure, settings, schema, code, verify, health, and the on-demand `cutover`, `rollback` and `remove` |
| `deployment/New-DeploymentPackage.ps1` | builds the package the deploy script consumes (zips, migrator, manifest) |
| `deployment/Test-DeployWilson.ps1` | dependency-free self-test of both scripts; with `-PackagePath`, also checks a built package |

```powershell
# Build a package
.\script\deployment\New-DeploymentPackage.ps1 -Environment nonprod, dev -Version local

# Preview everything for dev
.\script\deployment\Deploy-Wilson.ps1 -Environment dev -Component all -WhatIf -PackagePath artifacts\package\wtc-eim-deploy-local

# Self-test
pwsh -NoProfile -File script/deployment/Test-DeployWilson.ps1
```

The configurations are in `deployment/config/`: `deploy.base.json` plus one `deploy.<name>.json` per
configuration. They are tracked so names and non-secret settings can be reviewed, and they hold no secrets.
Supply the connection strings at run time through `EIM_DEPLOY_EIMDB` and `EIM_DEPLOY_SERVICEBUS`.

The pipeline that runs these is `pipelines/eim.yml`. See [docs/deployment/deployment.md](../docs/deployment/deployment.md).

## Database

- `SQL/Migrations/` contains ordered, one-time migrations.
- `SQL/Repeatable/` contains scripts applied on every migration run.
- `SQL/_scratch/` is never embedded or deployed.

See [database migration guidance](../docs/deployment/database-migrations.md).

## Other utilities

- `Create-AiPullRequest.ps1` supports the feature-branch pull-request pipeline.
- `Repair-TruncatedSourceId.ps1` is a targeted data repair utility.
- `Superseded/` contains historical deployment assets and must not be used for current deployments.
