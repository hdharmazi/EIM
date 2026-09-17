# Scripts

## Deployment

`deployment/Deploy-Eim.ps1` is the manual nonprod deployment orchestrator. It accepts `dev`, `bat`, or `test` and deploys selected components in dependency order.

```powershell
# Preview all components for dev
.\script\deployment\Deploy-Eim.ps1 -Environment dev -Component all -WhatIf

# Deploy one component
.\script\deployment\Deploy-Eim.ps1 -Environment bat -Component function
```

Environment files under `deployment/config/` are tracked so resource names and non-secret settings
can be reviewed. Keep their connection-string fields as placeholders. Supply credentials at runtime
through `EIM_DEPLOY_EIMDB` and `EIM_DEPLOY_SERVICEBUS`; never commit real secret values.

`deployment/Deploy-ZipWithPublishProfile.ps1` is the generic Kudu ZIP-deploy helper used by Azure DevOps pipelines.

## Database

- `SQL/Migrations/` contains ordered, one-time migrations.
- `SQL/Repeatable/` contains scripts applied on every migration run.
- `SQL/_scratch/` is never embedded or deployed.

See [database migration guidance](../docs/deployment/database-migrations.md).

## Other utilities

- `Create-AiPullRequest.ps1` supports the feature-branch pull-request pipeline.
- `Repair-TruncatedSourceId.ps1` is a targeted data repair utility.
- `Superseded/` contains historical deployment assets and must not be used for current deployments.
