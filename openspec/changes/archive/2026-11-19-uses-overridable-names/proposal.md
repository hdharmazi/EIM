## Why

Ticket #5246. A configuration with `uses` (today only `test`, which uses `bat`) inherits all twelve owner names. Preflight stops when it sets any of them. The user wants `test` to have its own Key Vault while it keeps sharing `bat`'s resource group, Service Bus namespace, SQL database, App Service plan, gateway and dashboard:

```json
{ "environment": "test", "uses": "bat", "names": { "keyVault": "kv-eim-wtc-test" }, "automation": { "triggersEnabled": true } }
```

Two inherited names serve only the using configuration's own Automation app and nothing the owner runs on its behalf: the Key Vault (the `ServiceBus` reference) and the Log Analytics workspace (behind the Automation Insights). They can safely belong to that configuration when it asks for them.

The App Service plan stays locked (user decision). A using configuration's Automation app on its own plan would get its own outbound addresses, and only the owner's `sql` can allow them on the shared SQL server. Keeping the plan locked keeps the SQL firewall exactly as it is.

This change builds on `per-environment-deployment`, which is implemented and reviewed but not yet archived. It must be archived after that change (see design.md, "Archive order").

## What Changes

- The twelve inherited name keys split into two groups:
  - **Locked** (preflight still refuses an override, message unchanged): `resourceGroup`, `serviceBusNamespace`, `sqlServer`, `sqlDatabase`, `appServicePlan`, `gatewayApp`, `gatewayStorage`, `gatewayInsights`, `dashboardApi`, `staticWebApp`.
  - **Overridable**: `keyVault`, `logAnalytics`.
- For an overridable key, a blank or missing `names.<key>` inherits the owner's resolved name, as today. An override equal to the owner's name (ignoring case) is a no-op and behaves as inherited.
- An overridable key set to a different name becomes an **owned** resource of the using configuration:
  - `infra` creates it in the owner's resource group when it is missing, tagged `eim-environment=<using configuration>`, using the configuration's own `logAnalytics.retentionDays`. An existing one is used as found, as for owners.
  - With an owned vault:
    - `infra` grants the using configuration's Automation identity secret `get` on that vault. The grant is additive, as today.
    - `settings` writes the `eim-servicebus` secret into that vault from the owner's namespace, only when it is missing or differs. This replaces the "run `settings` for the owner first" check for that configuration.
    - The Automation app's `ServiceBus` setting references the owned vault.
  - With an owned workspace, the Automation Insights resource is created in that workspace.
- Preflight treats a missing owned resource as a note ("`infra` will create it"), not as a failure. The RBAC-mode vault check applies to an owned vault too.
- `infra` warns, and changes nothing, when the existing Automation Insights resource of a using configuration that owns its workspace is linked to a different workspace.
- In `verify`, the Key Vault reference report shows the vault each reference resolves against.
- The owner's shared apps do not change. The dashboard API keeps the owner's vault. The gateway keeps the owner's workspace. Every Automation app stays on the owner's plan, so the owner's `sql` firewall coverage does not change.
- Production isolation is unchanged and now also covers override names. A non-production override that equals a production name fails preflight.
- The shipped `deploy.test.json` is not changed: `test` keeps inheriting `bat`'s vault and workspace. The docs explain the option.

There are no **BREAKING** changes. Every shipped configuration resolves to exactly the same names and infra plan as before.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `deployment-automation`: these requirements change so that a using configuration can own its vault and workspace:
  - "Resource names derive from the environment"
  - "A configuration can use another environment's resources"
  - "Function App hosting baseline"
  - "Missing resources are created and existing resources are used as found"
  - "Preflight checks the foundation resources"
  - "The Service Bus connection is held in the Key Vault"
  - "The deployment script deletes no Azure resource" (no access policy, vault or workspace is removed when a configuration stops naming it)

  Several of these requirements are added or rewritten by the unarchived change `per-environment-deployment`.

`sql-identity-access` does not change.

## Impact

- `script/deployment/Deploy-Wilson.ps1`:
  - `$InheritedNameKeys` is split into `$LockedNameKeys` and `$OverridableNameKeys`.
  - New pure helpers: `Get-OwnedNameKeys`, `Get-FoundationResourceSteps`, `Get-PlacementWarnings` and `Format-ReferenceStatus`.
  - Functions that change: `Get-UsesProblems`, `Get-ResourceNames`, `Get-RequiredConfigPaths`, `Get-PlaceholderProblems`, `Get-OfflinePreflightProblems`, `Get-InfraPlan`, `Get-FoundationProblems`, `Get-ExistingResources`, `Invoke-PreflightComponent`, `Invoke-InfraComponent`, `Invoke-SettingsComponent`, `Write-ReferenceReport`, and the main block.
  - Not changed: `Get-AppOutboundIps`, `Invoke-SqlComponent`, `Get-SqlPlan`, `Get-SqlUserApps`.
- `script/deployment/Test-DeployWilson.ps1`: new and changed self-test cases.
- `Wilson.Integration.Tests/DeploymentConfigTests.cs`: the C# mirror of the key lists and of the name resolver.
- Docs: `docs/deployment/deployment.md` and `script/README.md`.
- No change to:
  - the configuration files, the package (`New-DeploymentPackage.ps1`) or the pipelines (`pipelines/*.yml`)
  - application code, SQL migrations or the dashboard
- Azure: nothing changes until a using configuration sets an overridable key. When one does, its next `infra` creates the resource and its next `settings` writes the secret.

## Non-goals

- Overriding a locked key: the resource group, namespace, SQL server or database, App Service plan, gateway, or dashboard.
- Changing the SQL firewall coverage, or letting the owner's `sql` read any app of another configuration.
- Giving `test` its own vault in this change. `deploy.test.json` stays as it is.
- Relinking an existing Insights resource to another workspace. The script warns, and the relink is manual.
- Deleting or cleaning up anything:
  - the owner-vault access policy a using configuration held before it switched to its own vault
  - a vault or workspace the configuration no longer names
- Pointing the owner's dashboard API or gateway at a using configuration's vault or workspace.
- Allowing a configuration with `uses` to run `sql`, `schema`, `gateway`, `backend` or `frontend`.
- Chains of `uses`, or more than one level of ownership.
- The Service Bus queue configuration. That is a separate change.
- Pipeline, package or variable-group changes.
