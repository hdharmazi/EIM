## 1. Implementation

All script changes are in `script/deployment/Deploy-Wilson.ps1`. Every new parameter is trailing, with a default of `@()`, so existing callers keep working. Proof for each task in this section is `pwsh -NoProfile -File script/deployment/Test-DeployWilson.ps1`, together with the matching cases in section 2. `Get-AppOutboundIps`, `Invoke-SqlComponent`, `Get-SqlPlan` and `Get-SqlUserApps` do not change (design D9).

- [x] 1.1 Replace `$InheritedNameKeys` with `$LockedNameKeys` (10 keys, including `appServicePlan`) and `$OverridableNameKeys` (`keyVault`, `logAnalytics`), as listed in design D1. Add `$OwnedResourceKeys = @{ keyVault = 'KeyVault'; logAnalytics = 'LogAnalytics' }`. Proof: tests 2.1 and 2.2.
- [x] 1.2 In `Get-UsesProblems`, loop over `$LockedNameKeys` only, with the message text unchanged. Proof: test 2.2.
- [x] 1.3 In `Get-ResourceNames`, when `$Owner` is set:
  - copy every locked key from the owner;
  - copy an overridable key from the owner only when `names.<key>` is blank;
  - when the override equals the owner's name ignoring case, use the owner's spelling.

  Proof: test 2.1.
- [x] 1.4 Add the pure function `Get-OwnedNameKeys($Config, $Owner)` (design D2). In the main block, compute `$ownedKeys` after `$names`, and print " (own)" after each owned name in the name listing. Proof: test 2.1.
- [x] 1.5 In `Get-RequiredConfigPaths`, `Get-PlaceholderProblems` and `Get-OfflinePreflightProblems`, add `[string[]]$OwnedKeys = @()`. For `infra`, require `logAnalytics` when it is owned. Nothing is added for `keyVault`. Pass `$ownedKeys` from the main block. Proof: test 2.3.
- [x] 1.6 Move the vault, plan and workspace create steps of `Get-InfraPlan` into a helper, `Get-FoundationResourceSteps($Config, $Names, $Existing, [string[]]$Keys, $TagArgs, $Tags)`, with labels and arguments unchanged. Then add `[string[]]$OwnedKeys = @()` to `Get-InfraPlan`:
  - owners call the helper with `keyVault`, `appServicePlan` and `logAnalytics`, as today;
  - a using configuration calls it with `$OwnedKeys` (only `keyVault` and/or `logAnalytics`), before the queues. It never gets a plan step.

  Proof: test 2.4, and the existing prdmck, bat and test plan assertions still pass.
- [x] 1.7 In `Get-ExistingResources`, add `[string[]]$OwnedKeys = @()`. For a using configuration, probe `KeyVault` and `LogAnalytics` (with `Managed`) for each owned key. Never probe `Plan` for a using configuration. Proof: test 2.7 (stubbed az calls).
- [x] 1.8 In `Get-FoundationProblems`, add `[string[]]$OwnedKeys = @()`. A missing owned resource becomes the note "<resource> '<name>' does not exist; 'infra' will create it". A missing plan, and every other missing inherited resource, stays a problem naming the owner. The RBAC check is unchanged. Proof: test 2.5.
- [x] 1.9 In `Invoke-PreflightComponent`, add `[string[]]$OwnedKeys = @()`:
  - pass it to `Get-FoundationProblems`;
  - skip the owner-secret check when `keyVault` is owned.

  Pass `$ownedKeys` from the main block. Proof: test 2.7.
- [x] 1.10 Add the pure function `Get-PlacementWarnings($Names, [string]$InsightsWorkspaceId)` (design D4). It returns one warning when the id is not blank and its last segment is not, ignoring case, `$Names.LogAnalytics`, naming `$Names.AutomationInsights`, the current workspace and the resolved one. In `Invoke-InfraComponent`, add `[string[]]$OwnedKeys = @()`:
  - pass it to `Get-ExistingResources` and `Get-InfraPlan`;
  - for a using configuration that owns `logAnalytics` and whose Automation Insights exists, after the steps run (or under preview, after listing them), read its `properties.WorkspaceResourceId` (`az resource show ... --resource-type Microsoft.Insights/components`) and print the warning through `Write-DeployWarning`. The warning never fails the run.

  Pass `$ownedKeys` from the main block. Proof: tests 2.6 and 2.7.
- [x] 1.11 In `Invoke-SettingsComponent`, add `[string[]]$OwnedKeys = @()`. A using configuration that owns `keyVault` calls `Sync-ServiceBusSecret $Names` before the Automation settings (design D6). A configuration that inherits the vault keeps the existing "run 'settings' for '<owner>' first" check. Pass `$ownedKeys` from the main block. Proof: test 2.7.
- [x] 1.12 Add the pure function `Format-ReferenceStatus($Reference)`, which returns `"<status> (<vaultName>)"`, or `<status>` when `vaultName` is blank (design D7). Use it in `Write-ReferenceReport`. `health` does not change. Proof: test 2.8.
- [x] 1.13 Update the comments that describe inheritance:
  - the header of `Get-ResourceNames` (locked and overridable keys);
  - the `$IsOwner` note above `Get-InfraPlan`;
  - the summary above `Get-FoundationProblems`.

  Proof: review only.

## 2. Tests

Self-test cases go in `script/deployment/Test-DeployWilson.ps1`, in the sections named. Proof for 2.1 to 2.9: `pwsh -NoProfile -File script/deployment/Test-DeployWilson.ps1` passes.

- [x] 2.1 `== names`:
  - The shipped `test` still resolves to `$expectedTest`. This already exists; keep it.
  - `test` with `{ "names": { "keyVault": "kv-eim-wtc-test" } }` resolves `KeyVault` to `kv-eim-wtc-test`, and every other property equals `$expectedTest`, including `AppServicePlan` `asp-eim-wtc-bat`.
  - `test` with `{ "names": { "logAnalytics": "log-eim-wtc-test" } }` resolves `LogAnalytics` to `log-eim-wtc-test`, and every other property equals `$expectedTest`.
  - `{ "names": { "logAnalytics": "" } }` on `test` resolves to `log-eim-wtc-bat`, not `log-eim-wtc-test`.
  - `{ "names": { "keyVault": "KV-D365FSC-WTC-NONPROD" } }` resolves to `kv-d365fsc-wtc-nonprod`.
  - `Get-OwnedNameKeys` returns:
    - `@('keyVault')` for the vault override, and `@('logAnalytics')` for the workspace override;
    - `@()` for the shipped `test`, for the blank override, for the equal override, and for `bat` with no owner.
  - `test` with `names.keyVault` `kv_eim_wtc_test` throws, naming the vault.
- [x] 2.2 `== uses and isolation`:
  - Replace the line-241 assertion (`names.keyVault` is now allowed). `test` setting `names.sqlServer` is named: "'test' sets names.sqlServer, which it inherits from 'bat'".
  - `test` setting `names.appServicePlan` to `asp-eim-wtc-test` is named: "'test' sets names.appServicePlan, which it inherits from 'bat'".
  - Loop over every key in `$LockedNameKeys`: each is named. Assert `$LockedNameKeys.Count` is 10.
  - Loop over every key in `$OverridableNameKeys`: none gives a uses problem. Assert it is exactly `keyVault`, `logAnalytics`.
  - Assert `$LockedNameKeys + $OverridableNameKeys` equals the twelve keys of the old list, with no key in both.
  - Isolation: an `$allConfigs` copy whose `test` sets `names.keyVault` to `kv-eim-wtc-prd` gives "production configuration 'prd' and non-production configuration 'test' both use the name 'kv-eim-wtc-prd'".
- [x] 2.3 `== placeholders and offline preflight`:
  - `Get-RequiredConfigPaths @('infra') $false @('logAnalytics')` contains `logAnalytics` and not `appServicePlan`.
  - `Get-RequiredConfigPaths @('infra') $false @('keyVault')` and `... @()` contain neither.
  - A `test` with an owned workspace and `logAnalytics.retentionDays` set to a placeholder reports the placeholder for `infra`.
- [x] 2.4 `== infra plan`:
  - `test` with an owned vault and nothing existing except the RG and namespace plans these creates, in order: `create Key Vault kv-eim-wtc-test (access policies)`, then the test queues, `create storage stwtceimautotest`, `create Application Insights appins-eim-automation-wtc-test (workspace-based, log-eim-wtc-bat)` and `create Function App azf-eim-automation-wtc-test on asp-eim-wtc-bat`.
  - The vault step has kind `keyvault`, `--enable-rbac-authorization false`, `-g d365fsc-integration-nonprod` and the tag `eim-environment=test`.
  - The only policy step targets `-n kv-eim-wtc-test` for `azf-eim-automation-wtc-test`.
  - No step names `kv-d365fsc-wtc-nonprod`.
  - `test` with an owned workspace plans `create Log Analytics workspace log-eim-wtc-test (30 days)`, Insights on `log-eim-wtc-test`, and the Function App `on asp-eim-wtc-bat`.
  - No plan for a using configuration, with any `$OwnedKeys`, contains a `create App Service plan` step.
  - The plan with `$OwnedKeys` set and the vault and workspace existing (`Managed`) creates nothing.
  - The plan with the equal override (`Get-OwnedNameKeys` returns `@()`) equals the shipped `test` plan.
  - The "plan deletes nothing" loop includes both new plans.
- [x] 2.5 `== foundation`:
  - `Get-FoundationProblems` for `test` with `kv-eim-wtc-test` missing and `@('keyVault')` has no problem, and has the note "Key Vault 'kv-eim-wtc-test' does not exist; 'infra' will create it".
  - The same with `asp-eim-wtc-bat` also missing still fails, naming `asp-eim-wtc-bat` and `bat`.
  - `log-eim-wtc-test` missing with `@('logAnalytics')` is a note, not a problem.
  - An owned vault with `KeyVaultRbac` set fails, naming `kv-eim-wtc-test`.
- [x] 2.6 `== infra plan` (placement), with `Get-PlacementWarnings` and `test` resolved with `names.logAnalytics` `log-eim-wtc-test`:
  - a workspace id ending `/workspaces/log-eim-wtc-bat` gives one warning naming `appins-eim-automation-wtc-test`, `log-eim-wtc-bat` and `log-eim-wtc-test`;
  - a matching id (with different case) gives none;
  - a blank id gives none.
- [x] 2.7 `== components with stubbed Azure calls`. The stubbed `az` calls record their arguments, as in the existing cases:
  - `Invoke-SettingsComponent` for `test` with `@('keyVault')` calls `keyvault secret` on `kv-eim-wtc-test` and writes `ServiceBus` as `@Microsoft.KeyVault(SecretUri=https://kv-eim-wtc-test.vault.azure.net/secrets/eim-servicebus/)`.
  - Without owned keys it still throws "run 'settings' for 'bat' first" when the secret is missing.
  - `Invoke-PreflightComponent` for `test` with `@('keyVault')` does not check the secret in `kv-d365fsc-wtc-nonprod`.
  - `Get-ExistingResources` for `test` with `@('keyVault')` probes `keyvault show ... -n kv-eim-wtc-test`, and makes no `appservice plan show` and no workspace probe.
  - `Get-ExistingResources` for `test` with `@('logAnalytics')` probes `log-eim-wtc-test` and makes no `appservice plan show`.
  - `Invoke-InfraComponent` for `test` with `@('logAnalytics')`, with the Automation Insights existing and linked to `log-eim-wtc-bat`, prints the placement warning and does not throw.
  - Under `== settings` (pure): the `Get-AutomationSettings` `ServiceBus` value for `test` with the owned vault references `kv-eim-wtc-test`, and `Get-DashboardApiSettings` for `bat` still references `kv-d365fsc-wtc-nonprod`.
- [x] 2.8 `== Key Vault references and verify targets`:
  - `Format-ReferenceStatus` on `{ properties: { status: 'Resolved', vaultName: 'kv-eim-wtc-test' } }` returns `Resolved (kv-eim-wtc-test)`;
  - a blank `vaultName` returns `Resolved`.
- [x] 2.9 `== script text`: the script contains no `keyvault delete-policy`, and its only `delete` calls are still `config appsettings delete` and `sql server firewall-rule delete`. Extend the existing check if it does not already cover `delete-policy`.
- [x] 2.10 In `Wilson.Integration.Tests/DeploymentConfigTests.cs`:
  - Replace `InheritedNameKeys` with `LockedNameKeys` (10 keys, including `appServicePlan`) and `OverridableNameKeys` (`keyVault`, `logAnalytics`), mirroring the script.
  - In `ResourceNames`, copy the locked keys always, and copy an overridable key only when the configuration's `names.<key>` is blank. An equal override takes the owner's value.
  - Keep `Test_resolves_to_bat_resources_and_its_own_automation`, iterating over both lists.
  - Rename `Test_uses_bat_and_overrides_no_inherited_name` to `Test_uses_bat_and_overrides_no_locked_name`, checking `LockedNameKeys`.
  - Add `Overridable_key_vault_resolves_to_its_own_name`: a merged `test` with `names.keyVault = "kv-eim-wtc-test"` resolves the vault to it, and every locked key, including `appServicePlan`, resolves to bat's.
  - Add `Disjoint_names_check_catches_an_owned_vault_shared_with_production`: `test` with `names.keyVault = "kv-eim-wtc-prd"` intersects `NamesInUse(Merged("prd"))`.

  Proof: `dotnet test Wilson.Integration.Tests/Wilson.Integration.Tests.csproj`.

## 3. Documentation

- [x] 3.1 `docs/deployment/deployment.md`:
  - "Configurations: owners and `uses`": state that the ten locked names, including the App Service plan, are always borrowed, and that `keyVault` and `logAnalytics` may be set to make them the configuration's own. Explain in one sentence why the plan is locked (the Automation app keeps the owner's outbound addresses, so the owner's SQL firewall still covers it). Add the example from proposal.md. Change the preflight bullet to "overrides a locked name".
  - Note that the owner's dashboard API and gateway are unaffected.
  - "Service Bus": an owned vault gets its own copy of `eim-servicebus` from the owner's namespace through its own `settings`. After a key rotation, both configurations run `settings`.
  - Component table: `infra` for `uses` (owned vault or workspace, created in the owner's group; workspace placement warning) and `settings` for `uses` (the secret when the vault is owned).
  - Deployer rights from design D8.
  - "Names": a blank overridable name inherits; it does not derive.
  - "Adding an environment": mention the option.

  Proof: review. `pwsh -NoProfile -File script/deployment/Test-DeployWilson.ps1` still passes, in case it checks doc text.
- [x] 3.2 `script/README.md`: one sentence saying that a configuration with `uses` may own its Key Vault and Log Analytics workspace through `names` (see deployment.md). Proof: review.
- [x] 3.3 `CLAUDE.md`: no change. The deployment bullet ("`test` (`"uses": "bat"`)") stays accurate. Confirm by review; do not edit.

## 4. Final checks

- [x] 4.1 `dotnet build ARM.Integration.Wilson.slnx`
- [x] 4.2 `dotnet test ARM.Integration.EIM.Tests/ARM.Integration.EIM.Tests.csproj`
- [x] 4.3 `dotnet test ARM.Integration.D365FSC.Gateway.Tests/ARM.Integration.D365FSC.Gateway.Tests.csproj`
- [x] 4.4 `dotnet test Wilson.Integration.Tests/Wilson.Integration.Tests.csproj`
- [x] 4.5 `pwsh -NoProfile -File script/deployment/Test-DeployWilson.ps1`
- [x] 4.6 `openspec validate uses-overridable-names`
- [x] 4.7 Confirm that `script/deployment/config/deploy.test.json`, `pipelines/*.yml` and `script/deployment/New-DeploymentPackage.ps1` are unchanged, and that `Get-AppOutboundIps`, `Invoke-SqlComponent`, `Get-SqlPlan` and `Get-SqlUserApps` are unchanged in the diff (`git diff`).

## 5. Deploy and verify (manual, not for Luigi; only if the user later gives `test` its own vault)

- [ ] 5.1 Add `"names": { "keyVault": "kv-eim-wtc-test" }` to `deploy.test.json` in a separate change. Run `Deploy-Wilson.ps1 -Environment test -Component preflight,infra,settings -WhatIf` and check two things:
  - the preview lists `create Key Vault kv-eim-wtc-test (access policies)` and the policy on it;
  - it would write `eim-servicebus` in `kv-eim-wtc-test`.
- [ ] 5.2 Run `eim` with `deployInfra` and `applySettings`. In `Deploy_test`, `verify` shows `ServiceBus  Resolved (kv-eim-wtc-test)` for `azf-eim-automation-wtc-test`, and `health` passes.
- [ ] 5.3 Check in the portal:
  - `wtc-dashboard-api-dev` still references `kv-d365fsc-wtc-nonprod`;
  - `azf-eim-automation-wtc-test` is still on `asp-eim-wtc-bat`;
  - the old `test` access policy on `kv-d365fsc-wtc-nonprod` is still present. Remove it by hand if wanted.
