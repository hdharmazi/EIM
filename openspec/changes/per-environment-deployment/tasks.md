## 1. Implementation

- [x] 1.1 Configuration files (design D2, D3):
  - `script/deployment/config/deploy.base.json`:
    - Delete `sharedSuffix`, the `names` section and `gateway.legacyVerificationTargets`.
    - Add `serviceBus.sku` `"Standard"`, `logAnalytics.retentionDays` `30`, the `sql` object (`sku` `"S0"`, `backupRedundancy` `"Local"`, `firewall` `"appOutboundIps"`, `entraAdmin` `""`, `entraAdminObjectId` `""`, `entraAdminType` `"Group"`), and `automation.triggersEnabled` `false`.
    - Keep `production` `false`, the `dashboard` values (`true`/`"bat"`) and `automation.retiredSettings`.
  - `deploy.bat.json`:
    - `environment` `"bat"`.
    - `names`: `resourceGroup` `d365fsc-integration-nonprod`, `serviceBusNamespace` `asb-eim-wtc-nonprod`, `keyVault` `kv-d365fsc-wtc-nonprod`, `sqlServer` `azql-wtc-sandbox`, `sqlDatabase` `eimdb`, `dashboardApi` `wtc-dashboard-api-dev`, `staticWebApp` `wtc-dashboard-frontend-dev`.
    - `automation.triggersEnabled` `true`.
    - Delete `scope` and `legacy`.
  - `deploy.test.json`: `environment` `"test"`, `uses` `"bat"`, `automation.triggersEnabled` `true`; delete `scope` and `legacy`.
  - `deploy.prd.json`:
    - Fold in `deploy.prdshared.json`: `production` `true`, `names.resourceGroup` `d365fsc-integration-prod`, and the Entra, `excludedUsers` and simulation resets.
    - Add `logAnalytics.retentionDays` `90`, `sql.entraAdmin` `<production sql entra admin group>`, `sql.entraAdminObjectId` `<production sql entra admin group object id>` and `automation.triggersEnabled` `false`.
    - Delete `scope`, `sharedSuffix`, the blank `names.*` keys and `gateway.legacyVerificationTargets`.
  - Delete `deploy.nonprod.json` and `deploy.prdshared.json`.
  - Proven by tasks 2.4 and 2.5.

- [x] 1.2 `script/deployment/Deploy-Wilson.ps1`, legacy and on-demand removal (design D11; same scope as the superseded `remove-legacy-cutover` tasks 1.2 and 1.3):
  - Delete:
    - `cutover`, `rollback` and `remove` from the `-Component` `ValidateSet`
    - `-IncludeDefaultKeys` and `$OnDemandComponents`
    - `$QueueSettingNames`, `$ParityCriticalSettings`, `$ParityIgnoredSettings`, `$RemovableTypes` and `$GatewayKeyFunctions`
    - everything from `Get-CutoverRefusal` to `ConvertTo-LockSeconds`
    - `Get-EffectiveFunctionStates`, `Set-GatedTriggers` and `Move-GatedTriggers`
    - everything from `Get-CutoverStatePath` to `Invoke-RemoveComponent`
    - `Get-LegacyNames`, `Get-TriggerGuardApps` and `Get-LegacyNameConflicts`
    - the `legacy` path in `Get-RequiredConfigPaths`
    - the `legacyVerificationTargets` loop in `Get-DashboardApiSettings`
    - the legacy and retired entries in `Get-VerifyTargets`
    - the matching help text, `.EXAMPLE` blocks and main-body cases
  - Keep `Get-StaleVerificationTargetSettings`, `Get-GatedTriggerState`, `Write-GatedReport`, `Get-MissingGatedSettings` and `Get-QueueNames`.
  - Proven by tasks 2.1 and 2.2.

- [x] 1.3 `Deploy-Wilson.ps1`, configuration model and `uses` (design D2):
  - Delete `$ScopeComponents`, `Get-StampConfigurations`, `Get-SharedConfigurationName`, `Get-StampIsolationProblems` and every `scope` or `sharedSuffix` read.
  - `Get-DeployConfig` fails naming the file when a merged configuration has `scope` or `sharedSuffix`.
  - Add `Get-UsesProblems($Config, $AllConfigs)`. It reports an unknown target, a self or chained target, production using non-production, and an overridden inherited name.
  - Add `Get-IsolationProblems($AllConfigs)` (design D13): a production configuration that uses a non-production one, and a case-insensitive name overlap excluding the queue prefix.
  - Replace `Resolve-Components($Requested, $Scope, $SharedName, $EnvironmentNames)` with `Resolve-Components($Requested, [bool]$IsOwner, [string]$Owner)`:
    - owner set and using set as in design D2
    - refusal of `sql`, `schema`, `gateway`, `backend` and `frontend` for a using configuration, naming the owner
    - `all` excludes `myip`/`myipremove`
  - Set `$ComponentOrder` to the order in design D2. The `ValidateSet` equals `$ComponentOrder` plus `all`.
  - Preflight runs both problem functions before any Azure call.
  - Proven by task 2.1.

- [x] 1.4 `Deploy-Wilson.ps1`, naming (design D3):
  - Rewrite `Get-ResourceNames($Config, $Owner)` with the patterns in the design table: `rg-{w}-{c}-{e}`, `kv-…`, `sql-…`, `sqldb-…`, every suffix the configuration's own `environment`.
  - Inherited keys come from the owner's resolved names when `$Owner` is given.
  - Blank overrides fall back to the pattern.
  - Extend name validation with the Key Vault and SQL server rules.
  - Delete `Get-NamesInUse` if nothing calls it any more.
  - Proven by task 2.1 (name tables for `bat`, `test`, `prd` and a synthetic `prdmck`) and task 2.5.

- [x] 1.5 `Deploy-Wilson.ps1`, `infra` create-if-missing and use-as-found (design D4):
  - Rewrite `Get-InfraPlan($Config, $Names, $Existing, $IsOwner)`:
    - Owners plan: resource group (`az group create`), Service Bus namespace (`serviceBus.sku`), Key Vault (`--enable-rbac-authorization false`, recovering a soft-deleted vault), plan, Log Analytics (`logAnalytics.retentionDays`), gateway, dashboard API (`DOTNETCORE:10.0`, Always On, FTPS off, TLS 1.2, HTTPS only), static web app (Standard), and Automation.
    - Using configurations plan only queues and Automation.
    - Every create is planned only when the resource is missing.
  - `Get-ResourceTags` returns `eim-workload`, `eim-client` and `eim-environment` only.
  - `Get-FunctionAppSteps` and the dashboard site-configuration steps run only for resources that carry `eim-environment`.
  - Every deployed app gets `identity assign`, and an `az keyvault set-policy --secret-permissions get` step for its principal id.
  - Replace `Get-FoundationProblems`:
    - For a using configuration, fail naming each missing owner resource and stating that `infra` and `sql` must run for the owner first.
    - For any configuration, fail on an existing vault with RBAC authorisation.
  - `Get-ExistingResources` also reads tags, Key Vault properties and the SQL server.
  - Proven by task 2.1 (plan assertions on stubbed `$Existing` sets).

- [x] 1.6 `Deploy-Wilson.ps1`, `sql` component (design D5):
  - Add a pure `Get-SqlPlan($Config, $Names, $Existing, $OutboundIps)`:
    - Missing server: create with `--enable-ad-only-auth`, the `--external-admin-*` values and `--minimal-tls-version 1.2`, failing first on a blank or placeholder `sql.entraAdmin`/`sql.entraAdminObjectId`.
    - Missing database: create with `--service-objective` and `--backup-storage-redundancy`.
    - Managed server: `app-outbound-*` rules or `AllowAllWindowsAzureIps`, with stale-rule deletion.
    - Unmanaged server: report and warnings only.
  - Add `Invoke-SqlComponent`, which executes the plan.
  - Preflight fails when `schema` or `sqlusers` is requested and the server has no Entra admin.
  - Proven by task 2.1.

- [x] 1.7 `Deploy-Wilson.ps1`, `sqlusers` component (design D6):
  - Add `Get-SqlConnectionString($Names, $Authentication)` (design D7).
  - Add `Get-AppIdentityClientId($App)`: the ARM `identities/default` `clientId`, falling back to `az ad sp show --query appId`.
  - Add `Invoke-DeployerSql`:
    - an `az account get-access-token --resource https://database.windows.net/` token on `SqlConnection.AccessToken`
    - parameterised commands
    - a sign-in failure rethrown naming the server and identity, without the token
  - Add a pure `Get-DatabaseUserSql`: dynamic SQL with `QUOTENAME` and `CONVERT(varbinary(16), CAST(@clientId AS uniqueidentifier))`, `TYPE = E`, `ALTER ROLE db_datareader/db_datawriter ADD MEMBER`, `GRANT EXECUTE`, the SID-mismatch check, and no `DROP`.
  - Add `Invoke-SqlUsersComponent`:
    - users for the apps the configuration deploys
    - read-back listing
    - `-WhatIf` lists users and roles without connecting
  - Proven by task 2.1 (generated SQL text, app list per configuration, no `DROP`, preview without a connection).

- [x] 1.8 `Deploy-Wilson.ps1`, secrets removed, and the Key Vault Service Bus secret (design D8, D9):
  - Delete:
    - `Get-RequiredSecrets`, `Resolve-DeploySecrets` and `Get-SecretTargetProblems`
    - `Get-SqlServerFromConnection`, `Get-SqlCatalogFromConnection` and `Get-ServiceBusNamespaceFromConnection`
    - every `$Secrets` parameter and every `EIM_DEPLOY_EIMDB`/`EIM_DEPLOY_SERVICEBUS` read
  - `Get-AutomationSettings`, `Get-GatewaySettings` and `Get-DashboardApiSettings` write `EIMDB`/`ConnectionStrings__EIMDB` from `Get-SqlConnectionString` with `Active Directory Managed Identity`.
  - `ServiceBus`/`Simulation__ServiceBusConnectionString` become `@Microsoft.KeyVault(SecretUri=https://<vault>.vault.azure.net/secrets/eim-servicebus/)`.
  - Owner `settings`: write `eim-servicebus` from the namespace's `RootManageSharedAccessKey` through a temp file, only when it is missing or different.
  - Using `settings`: fail when the target's secret is missing.
  - `Invoke-SchemaComponent`:
    - passes `EIMDB_MIGRATOR_CONNECTION` from `Get-SqlConnectionString` with `Active Directory Default`, plus `AZURE_TOKEN_CREDENTIALS=AzureCliCredential`
    - primes the SQL token first
  - Proven by tasks 2.1 and 2.3.

- [x] 1.9 `Deploy-Wilson.ps1`, triggers, simulation and verify (design D10, D13):
  - Replace `Get-GatedSettingsToWrite` with `Get-GatedSettings([string[]]$GatedFunctions, [bool]$TriggersEnabled, [string[]]$KeepDisabled)`; `settings` writes the whole map every run.
  - Add `Get-TriggerConfigProblems($Config)` to preflight.
  - Remove `'AzureWebJobs.*.Disabled'` from `$DriftNameOnlySettings`, so a differing value warns.
  - The simulation prefix must be the owner's or a using configuration's queue prefix.
  - `Get-VerifyTargets` lists only the apps the configuration deploys.
  - `verify` reports Key Vault reference status from `GET {siteId}/config/configreferences/appsettings`, and `health` fails on an unresolved `ServiceBus` or `Simulation__ServiceBusConnectionString`.
  - Proven by task 2.1.

- [x] 1.10 `script/deployment/New-DeploymentPackage.ps1`:
  - Build `dashboard-frontend.<name>.zip` for each packaged configuration without `uses` and with filled Entra values.
  - Rewrite `Get-PackageReadme` for owners and `uses`: no stamp, legacy, cutover, remove or secret-variable text; triggers follow `automation.triggersEnabled`.
  - Proven by tasks 2.1 and 2.2.

- [x] 1.11 Pipelines (design D12):
  - `pipelines/eim.yml`:
    - Stages are `Build` (`environments: bat,test,prd`), `Deploy_bat` and `Deploy_test`.
    - `Deploy_bat`: `eim-wtc-bat`, `gateway,backend,frontend,automation`, `ownsResources: true`.
    - `Deploy_test`: `eim-wtc-test`, `automation`, `ownsResources: false`.
    - Add the `grantDatabaseAccess` parameter.
    - Update the header comments: no secret variables; the SC principal belongs in the SQL admin group.
  - `pipelines/eim-prd.yml`:
    - A single `Deploy_prd` stage: `eim-wtc-prd`, `gateway,backend,frontend,automation`, `ownsResources: true`, `sc-eim-wtc-prd`, `eim-wtc-prd`, `packageSource: eim`.
    - The same parameters as `eim.yml`.
  - `pipelines/templates/deploy-environment.yml`:
    - Add the `ownsResources` and `grantDatabaseAccess` parameters, with a template error for `runMigrations` when `ownsResources` is false.
    - The infrastructure step runs `infra` plus `sql` when `ownsResources`.
    - Add a database step: `myip`, prime the token, then `schema` and/or `sqlusers` in a `try`, and `myipremove` in a `finally`.
    - `env:` maps only `EIM_DEPLOY_CONFIG_OVERRIDES`.
  - `pipelines/README.md` updated.
  - Proven by task 2.6.

- [x] 1.12 Product code (design D7):
  - Add `<PackageReference Include="Microsoft.Data.SqlClient.Extensions.Azure" Version="1.0.0" />` to `ARM.Integration.Core/ARM.Integration.Core.csproj`.
  - In `ARM.Integration.DbMigrator/Program.cs`, change only the usage text: add an `Authentication=Active Directory Default` example.
  - No other C# change. `DefaultFailureClassifier` is unchanged.
  - Proven by task 2.3 and `dotnet build ARM.Integration.Wilson.slnx`.

- [x] 1.13 Documentation:
  - Delete `docs/deployment/resource-rename-cutover.md`.
  - `ARM.Integration.Wilson.slnx`:
    - Remove `resource-rename-cutover.md`, `deploy.nonprod.json` and any `deploy.prdshared.json` entries.
    - Add `deploy.prd.json`, `pipelines/eim-prd.yml` and `docs/deployment/production-go-live.md` if they are missing.
  - Rewrite for owners/`uses`, environment-suffixed names, create-if-missing, `sql`/`sqlusers`, Entra SQL, the Key Vault Service Bus secret, `automation.triggersEnabled`, the bat/test/prd stages and no secret variables:
    - `docs/deployment/deployment.md`
    - `docs/deployment/deploy-from-scratch.md`
    - `docs/deployment/devops-setup.md`: drop the secret variables; add the SC principal to the SQL admin group; note that an isolated environment needs subscription-scope rights to create its resource group; retire `eim-wtc-nonprod`/`eim-wtc-prdshared` as DevOps environments
    - `docs/deployment/production-go-live.md`: one stage, go-live by configuration, the SQL admin group
    - `docs/deployment/database-migrations.md`: the Entra migrator connection; the local SQL-authentication example stays
    - `docs/deployment/d365fsc-gateway.md`: `azf-eim-gateway-wtc-bat`, "Moving a consumer to the shared gateway"
    - `docs/deployment/deployment-plan.md`
    - `README.md`, `docs/README.md` and `script/README.md`
  - `CLAUDE.md` "Deployment" and "Production config" bullets:
    - `eim.yml` runs Build → Deploy_bat → Deploy_test.
    - `eim-prd.yml` runs Deploy_prd.
    - The configurations are `bat`, `test` (uses bat) and `prd`.
    - `automation.triggersEnabled` controls the gated triggers.
    - Drop the `nonprod`/`prdshared` and runbook references.
  - `openspec/config.yaml` context line: "bat (owns the non-production resources), test (uses bat), and prd (isolated production environment, no simulation)".
  - Leave `script/Superseded/README.md` unchanged.
  - Proven by task 2.2.

## 2. Tests

- [x] 2.1 `script/deployment/Test-DeployWilson.ps1`, by dot-sourcing the script:
  - Remove every assertion on scope, stamp, `sharedSuffix`, legacy, cutover, parity, lock, key copy, remove, `Get-GatedSettingsToWrite`, `Get-SecretTargetProblems` and the connection parsers.
  - Add assertions:
    - The `ValidateSet` equals `$ComponentOrder` plus `all`, and `$ComponentOrder` is the order in design D2.
    - `Resolve-Components`:
      - `all` for `bat` yields every component except `myip`/`myipremove`
      - `all` for `test` yields `preflight, infra, settings, sqlusers, automation, verify, health`
      - `schema`, `sql`, `gateway`, `backend` and `frontend` for `test` throw naming `bat`
    - `Get-UsesProblems`: unknown target, self, chain, `prd` uses `bat`, and `test` overriding `names.keyVault` each produce a named problem; the shipped configurations produce none.
    - `Get-IsolationProblems`:
      - `prd` with `names.sqlServer=azql-wtc-sandbox` fails naming the name
      - a synthetic `uat` that uses `prd` fails
      - the shipped configurations pass
    - `Get-ResourceNames` returns the `bat`, `test` and `prd` tables in design D3, and the `prdmck` names for `@{ environment = 'prdmck' }`; a 9-character environment fails Key Vault or storage validation by name.
    - `Get-InfraPlan` creates everything for `prdmck` with nothing existing, and creates nothing on a second plan with everything existing.
      - For `bat` with the kept resources existing and untagged, it issues no create, update, tag or site-config step for them, only `identity assign` and `set-policy`.
      - For `test`, it plans only `test-*` queues and the `test` Automation resources.
    - `Get-SqlPlan`:
      - a missing server with blank `sql.entraAdminObjectId` fails naming both keys
      - a missing server with filled values plans `--enable-ad-only-auth` and `--minimal-tls-version 1.2`
      - an untagged existing server plans no change and warns on an uncovered IP
      - a tagged server plans `app-outbound-*` adds and stale deletes
    - `Get-DatabaseUserSql` contains `TYPE = E`, `QUOTENAME`, `db_datareader`, `db_datawriter` and `GRANT EXECUTE`, and no `DROP`. The `sqlusers` app list is three apps for `bat` and one for `test`.
    - Settings:
      - `Get-AutomationSettings`/`Get-GatewaySettings`/`Get-DashboardApiSettings` produce `EIMDB`/`ConnectionStrings__EIMDB` with `Authentication=Active Directory Managed Identity` and no `Password`.
      - `ServiceBus` and `Simulation__ServiceBusConnectionString` are the `@Microsoft.KeyVault(...eim-servicebus/)` reference.
      - There is no `Gateway__Applications__*` key.
      - A simulation prefix of `prd` for `bat` fails.
    - `Get-GatedSettings`:
      - six `'false'` for `bat`
      - `'true'` for a `keepDisabled` function
      - six `'true'` when `TriggersEnabled` is false
    - `Get-TriggerConfigProblems` reports `"yes"`, a missing key and `RetrySweepHandlr`, and nothing for the shipped configurations.
    - Drift: a differing gated value warns and a missing one fails.
    - `Get-VerifyTargets`: `bat` lists the gateway, the two dashboard resources and `azf-eim-automation-wtc-bat`; `test` lists only `azf-eim-automation-wtc-test`.
    - The script text contains no `resource delete`, no `EIM_DEPLOY_EIMDB`/`EIM_DEPLOY_SERVICEBUS`, and only `config appsettings delete` and `firewall-rule delete` as delete calls.
    - The package README has no stamp, cutover, remove or secret-variable text.
  - Keep and extend the static parser assertion from the superseded `remove-legacy-cutover` task 2.6: every `Verb-Noun` command in `Deploy-Wilson.ps1` is defined in the script or resolved by `Get-Command`.
  - Build every banned word by concatenation.
  - Proof: `pwsh -NoProfile -File script/deployment/Test-DeployWilson.ps1` passes.

- [x] 2.2 `Wilson.Integration.Tests/RepositoryLayoutTests.cs`:
  - Extend `RetiredDeploymentNames` (built by concatenation) with:
    - `azf-d365fsc-gateway-wtc`, `azf-eim-customer-wtc`, `stwtceimcustomer`, `appins-eim-customer-wtc`, `stwtcd365gw`, `appins-d365fsc-gateway-wtc`, `asp-appservice-p0v3-linux`
    - `asp-eim-wtc-nonprod`, `azf-eim-gateway-wtc-nonprod`, `log-eim-wtc-nonprod`
  - Reduce the exclusions to `openspec/changes/`, `script/Superseded/` and `ARM.Integration.D365FSC.Gateway.Tests/`.
  - Add `No_deployment_file_describes_a_cutover`:
    - It scans `script/deployment/**`, `pipelines/**`, `docs/deployment/**`, `README.md`, `docs/README.md`, `script/README.md`, `CLAUDE.md` and `ARM.Integration.Wilson.slnx`.
    - Banned terms: `legacy`, `cutover`, `cut over`, `rollback`, `roll back`, `retiredAutomationApps`, `IncludeDefaultKeys`, `sharedSuffix`, `prdshared`, `Deploy_shared`, `EIM_DEPLOY_EIMDB` and `EIM_DEPLOY_SERVICEBUS`.
    - The rollback terms are exempt only in `docs/deployment/database-migrations.md`.
    - The failure names the file and line.
  - Proof: `dotnet test Wilson.Integration.Tests/Wilson.Integration.Tests.csproj --filter FullyQualifiedName~RepositoryLayoutTests`.

- [x] 2.3 New `Wilson.Integration.Tests/SqlEntraAuthenticationTests.cs`: assert that `SqlAuthenticationProvider.GetProvider(SqlAuthenticationMethod.ActiveDirectoryManagedIdentity)` and `GetProvider(SqlAuthenticationMethod.ActiveDirectoryDefault)` are not null, and that a `SqlConnectionStringBuilder` built from `Get-SqlConnectionString`'s format with `Authentication=Active Directory Managed Identity` parses to `SqlAuthenticationMethod.ActiveDirectoryManagedIdentity`. Proof: `dotnet test Wilson.Integration.Tests/Wilson.Integration.Tests.csproj --filter FullyQualifiedName~SqlEntraAuthenticationTests`. Before task 1.12, it must fail.

- [x] 2.4 `Wilson.Integration.Tests/DeploymentConfigTests.cs`, reshape:
  - `The_expected_configurations_exist` expects exactly `base`, `bat`, `prd` and `test`.
  - `EnvironmentConfigurations` becomes `["bat","test"]` and `ProductionConfigurations` becomes `["prd"]`.
  - `ResourceNames()` mirrors design D3, including `uses`.
  - `NonProductionIdentifyingPaths` becomes `entra.tenantId`, `entra.clientId`, `entra.audience`, `automation.excludedUsers`, `dashboard.simulationEnabled` and `dashboard.simulationQueuePrefix`.
  - Remove:
    - `Each_stamp_has_one_shared_configuration`, `Nonprod_retires_the_dev_automation_app`, `Each_retired_automation_app_is_a_legacy_site` and `Legacy_names_are_never_names_in_use_and_never_the_dashboard`
    - the scope part of `Environment_matches_the_file_name_and_scope_is_set`, renamed `Environment_matches_the_file_name`
    - `Production_configurations_are_exactly_prdshared_and_prd`, replaced by `Production_configuration_is_exactly_prd`
  - `Nonprod_keeps_the_dashboard_names` becomes `Bat_keeps_the_existing_resource_names`, checking the seven `bat` overrides.
  - Proof: `dotnet test Wilson.Integration.Tests/Wilson.Integration.Tests.csproj --filter FullyQualifiedName~DeploymentConfigTests`.

- [x] 2.5 Add to `DeploymentConfigTests.cs`:
  - `Base_has_no_names_and_no_shared_suffix`
  - `No_configuration_has_scope_shared_suffix_or_legacy`: no `scope`, `sharedSuffix`, `legacy`, `gateway.legacyVerificationTargets` or `retiredAutomationApps` in any file
  - `Test_uses_bat_and_overrides_no_inherited_name`
  - `Bat_and_prd_have_no_uses`
  - `Production_and_nonproduction_names_are_disjoint`, rewritten over resolved names
  - `Trigger_switch_is_off_in_base_and_explicit_per_environment`: base `false`; `bat`/`test` `true`; `prd` explicit `false`
  - `KeepDisabled_names_only_gated_functions`
  - `Sql_defaults_mirror_wastequip`: base `sql.sku` `S0`, `backupRedundancy` `Local`, `firewall` `appOutboundIps`, `entraAdminType` `Group`
  - `Prd_sql_admin_is_a_placeholder_or_filled`
  - Keep `No_secrets_in_configuration` across the four files, and `Production_plan_is_P0v3`.
  - Proof: the same filter as task 2.4.

- [x] 2.6 `Wilson.Integration.Tests/PipelineDefinitionTests.cs` and `ProductionPipelineDefinitionTests.cs`:
  - `StageOrder` becomes `["Build","Deploy_bat","Deploy_test"]` and `["Deploy_prd"]`.
  - Replace `Bat_follows_the_shared_stage_directly` with `Bat_follows_the_build_directly`.
  - `Migrations_can_run_only_in_the_shared_stage` becomes `Migrations_can_run_only_in_owner_stages`, for `Deploy_bat` and `Deploy_prd`.
  - Add:
    - `Database_access_is_a_manual_parameter_defaulting_to_false` (both pipelines)
    - `Only_the_owner_stage_runs_the_sql_component` (`ownsResources: true` only on `Deploy_bat`/`Deploy_prd`)
    - `No_pipeline_maps_a_secret_variable` (no `EIM_DEPLOY_EIMDB`/`EIM_DEPLOY_SERVICEBUS`, and `env:` maps only `EIM_DEPLOY_CONFIG_OVERRIDES`)
    - `The_database_step_removes_the_firewall_rule_in_a_finally`
  - The build environments are `bat,test,prd`.
  - `Production_has_the_same_parameters_and_defaults_as_eim` includes `grantDatabaseAccess`.
  - `Every_azure_cli_step_maps_the_deployment_secrets` is replaced by the new mapping test.
  - Proof: `dotnet test Wilson.Integration.Tests/Wilson.Integration.Tests.csproj --filter "FullyQualifiedName~PipelineDefinitionTests|FullyQualifiedName~ProductionPipelineDefinitionTests"`.

- [x] 2.7 Repository grep. Run from the repository root; it must print nothing:

  ```bash
  git ls-files -co --exclude-standard \
    | grep -vE '^openspec/|^script/Superseded/|^ARM\.Integration\.Core/Helpers/EnvironmentStore\.cs$|D365FSCServiceCollectionExtensions\.cs$|SysMessageBusinessEventsContract\.cs$|JsonValueEditor\.tsx$|scenarios\.ts$|MessageQueueRepository\.cs$|RetryScheduleCalculatorTests\.cs$|^docs/deployment/database-migrations\.md$|^script/SQL/Migrations/015_seed_prd_routes\.sql$|node_modules/|RepositoryLayoutTests\.cs$' \
    | xargs grep -inE 'legacy|cut ?over|roll ?back|IncludeDefaultKeys|retiredAutomationApps|resource-rename-cutover|sharedSuffix|prdshared|Deploy_shared|EIM_DEPLOY_EIMDB|EIM_DEPLOY_SERVICEBUS'
  ```

- [x] 2.8 Final check; all must pass:
  - `dotnet build ARM.Integration.Wilson.slnx`
  - `dotnet test ARM.Integration.EIM.Tests/ARM.Integration.EIM.Tests.csproj`
  - `dotnet test ARM.Integration.D365FSC.Gateway.Tests/ARM.Integration.D365FSC.Gateway.Tests.csproj`
  - `dotnet test Wilson.Integration.Tests/Wilson.Integration.Tests.csproj`
  - `pwsh -NoProfile -File script/deployment/Test-DeployWilson.ps1`

## 3. Deploy and verify (manual, user; not Luigi's)

Every Azure action below is first previewed (`-WhatIf`, or the exact `az` command shown), and runs only on the user's go.

- [ ] 3.1 Before the first deployment of Automation code:
  - Stop `azf-eim-customer-wtc-dev`, `azf-eim-customer-wtc-bat` and `azf-eim-customer-wtc-test`, and set every `AzureWebJobs.<fn>.Disabled` on them to `true`.
  - Make sure no local `func start` consumes the `bat-*` or `test-*` queues.
  - Messages wait in the queues (14-day TTL).
- [ ] 3.2 Create or choose the non-production SQL admin Entra group. Members: the operators and the `sc-eim-wtc-nonprod` service principal. Then set it on the server with `az sql server ad-admin create -g d365fsc-integration-nonprod -s azql-wtc-sandbox --display-name <group> --object-id <group object id>`. SQL authentication stays enabled.
- [ ] 3.3 From the operator's machine, signed in as a member of the group:
  1. Run `Deploy-Wilson.ps1 -Environment bat -Component infra,sql -WhatIf`, then without `-WhatIf`. This creates the plan, workspace, gateway and Automation app, assigns identities and sets vault policies.
  2. Repeat for `test` with `-Component infra`.
- [ ] 3.4 Firewall on `azql-wtc-sandbox`: either confirm that "Allow Azure services" is on, or add the outbound addresses of `asp-eim-wtc-bat` that `sql` reported as uncovered. The `sql` re-run must report no uncovered address.
- [ ] 3.5 First database users on the shared `eimdb` (a write to a shared database):
  1. Run `Deploy-Wilson.ps1 -Environment bat -Component myip,sqlusers,myipremove -WhatIf`, review the three users, then run it.
  2. Repeat for `test`, which adds one user.
  3. Check the printed users and roles.
- [ ] 3.6 Run `eim` manually with `applySettings`, `runMigrations` and `deployCode`. `deployInfra` and `grantDatabaseAccess` are optional, since 3.3 and 3.5 already did them. Check:
  - the preflight and `verify` output of `Deploy_bat` and `Deploy_test`
  - Key Vault references resolved
  - six gated functions enabled on both Automation apps
  - no SQL login failures in Application Insights
- [ ] 3.7 Check that the `bat-*`/`test-*` queues drain, `MESSAGEQUEUE` retry and release rows move, and the dashboard loads, signs in and lists messages.
- [ ] 3.8 Remove `EIM_DEPLOY_EIMDB` and `EIM_DEPLOY_SERVICEBUS` from the `eim-wtc-nonprod` and `eim-wtc-prd` variable groups.
- [ ] 3.9 Move `wtc-dashboard-api-dev` to `asp-eim-wtc-bat` with the portal "Change App Service plan". Confirm that the dashboard loads.
- [ ] 3.10 For each consumer of the old gateway, follow `docs/deployment/d365fsc-gateway.md` "Moving a consumer to the shared gateway":
  - Issue a named host key on `azf-eim-gateway-wtc-bat` and run **Set key**.
  - Give the consumer the new base URL.
  - Update `GATEWAYCONSUMER.GATEWAYURL`.
  - Confirm one successful call per consumer.
- [ ] 3.11 After everything runs cleanly, delete the old resources in the portal:
  - `azf-eim-customer-wtc-{dev,bat,test}` with their storage and Insights
  - `azf-d365fsc-gateway-wtc-nonprod` with its Flex plan, `stwtcd365gwnonprod` and `appins-d365fsc-gateway-wtc-nonprod`
  - `asp-appservice-p0v3-linux-dev`

  Check the exact names in the portal first. Keep the resource group, `asb-eim-wtc-nonprod`, `kv-d365fsc-wtc-nonprod`, `azql-wtc-sandbox`/`eimdb`, `wtc-dashboard-api-dev` and `wtc-dashboard-frontend-dev`.
- [ ] 3.12 Delete the old Azure DevOps pipeline definitions, and the DevOps environments `eim-wtc-nonprod` and `eim-wtc-prdshared`. Create `eim-wtc-prd` with approvals and an exclusive lock if it does not exist (`docs/deployment/devops-setup.md`).
- [ ] 3.13 Production, when ready:
  1. Create the production SQL admin group, holding the operators and the `sc-eim-wtc-prd` principal, and fill `sql.entraAdmin`/`sql.entraAdminObjectId` and the other `prd` placeholders by PR.
  2. Run `eim-prd` with `deployInfra` (preview first), then `grantDatabaseAccess`, `applySettings`, `runMigrations` and `deployCode`.
  3. Go-live per `docs/deployment/production-go-live.md`.
- [ ] 3.14 Archive in order: `align-structure-and-deployment`, then `add-production-environment`, then `per-environment-deployment`. Then rename `openspec/specs/resource-naming-cutover/` to `openspec/specs/resource-naming/`.
