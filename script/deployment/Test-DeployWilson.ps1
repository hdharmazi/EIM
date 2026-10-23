<#
.SYNOPSIS
    Dependency-free self-test for Deploy-Wilson.ps1 and New-DeploymentPackage.ps1.

.DESCRIPTION
    Parses the deployment scripts, dot-sources them (their main bodies are guarded, so nothing runs and no Azure
    call is made) and asserts the pure functions: naming, configuration merge, scope and order, infra plan,
    settings, drift, cutover, remove guards, and the packaging helpers. Throws on the first failed assertion
    and exits 1.

    With -PackagePath it also checks a built package (the Build stage runs it after New-DeploymentPackage.ps1).

.EXAMPLE
    pwsh -NoProfile -File script/deployment/Test-DeployWilson.ps1

.EXAMPLE
    pwsh -NoProfile -File script/deployment/Test-DeployWilson.ps1 -PackagePath artifacts/package/wtc-eim-deploy-<version>
#>
#Requires -Version 7.2
param(
    # A built package folder (the one holding Deploy-Wilson.ps1 and artifacts/).
    [string]$PackagePath
)

$ErrorActionPreference = 'Stop'
# Dot-sourcing Deploy-Wilson.ps1 binds its own -PackagePath parameter in this scope, so keep ours first.
$packageToCheck = $PackagePath
$selfTestRoot = $PSScriptRoot
$deployScriptPath = Join-Path $selfTestRoot 'Deploy-Wilson.ps1'
$packageScriptPath = Join-Path $selfTestRoot 'New-DeploymentPackage.ps1'
$metadataFixture = Join-Path $selfTestRoot 'testdata/functions.metadata'
$script:AssertionCount = 0
$tempRoots = [System.Collections.Generic.List[string]]::new()

function Assert-True($Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
    $script:AssertionCount++
}

function Assert-Equal($Expected, $Actual, [string]$Message) {
    $e = (@($Expected) | ForEach-Object { "$_" }) -join ' | '
    $a = (@($Actual) | ForEach-Object { "$_" }) -join ' | '
    if ($e -cne $a) { throw "FAIL: $Message`n  expected: $e`n  actual:   $a" }
    $script:AssertionCount++
}

function Assert-Throws([scriptblock]$Block, [string]$Pattern, [string]$Message) {
    $thrown = $null
    try { & $Block | Out-Null } catch { $thrown = $_.Exception.Message }
    if ($null -eq $thrown) { throw "FAIL: $Message (nothing was thrown)" }
    if ($Pattern -and $thrown -notmatch $Pattern) { throw "FAIL: $Message`n  expected a message matching: $Pattern`n  actual: $thrown" }
    $script:AssertionCount++
}

function New-TempFolder {
    $path = Join-Path ([IO.Path]::GetTempPath()) "wtc-deploy-selftest-$([guid]::NewGuid())"
    New-Item -ItemType Directory -Path $path | Out-Null
    $tempRoots.Add($path)
    $path
}

function New-Setting([string]$Name, [string]$Value) { [pscustomobject]@{ name = $Name; value = $Value } }

function Get-ZipEntryNames([string]$ZipPath) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try { @($zip.Entries | ForEach-Object { $_.FullName -replace '\\', '/' }) } finally { $zip.Dispose() }
}

function Get-ZipEntryText([string]$ZipPath, [string]$EntryName) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $entry = $zip.Entries | Where-Object { ($_.FullName -replace '\\', '/') -eq $EntryName } | Select-Object -First 1
        if (-not $entry) { return $null }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    finally { $zip.Dispose() }
}

try {
    # ---------------------------------------------------------------------------------------------
    Write-Host '== parse'
    foreach ($file in $deployScriptPath, $packageScriptPath, $PSCommandPath) {
        $tokens = $null
        $errors = $null
        [System.Management.Automation.Language.Parser]::ParseFile($file, [ref]$tokens, [ref]$errors) | Out-Null
        Assert-True (@($errors).Count -eq 0) "$(Split-Path $file -Leaf) has parse errors: $(@($errors | ForEach-Object { "line $($_.Extent.StartLineNumber): $($_.Message)" }) -join '; ')"
    }

    Write-Host '== secrets never on an az command line'
    foreach ($file in $deployScriptPath, $packageScriptPath) {
        $leaf = Split-Path $file -Leaf
        $lines = Get-Content $file
        $violations = for ($i = 0; $i -lt $lines.Count; $i++) {
            $line = $lines[$i]
            if ($line -match '--settings\s+"?\$') { "line $($i + 1) passes app settings inline instead of through a temp file" }
            if ($line -match '--connection\b') { "line $($i + 1) passes a connection string as an argument" }
            $azCall = $line -match '(Invoke-Az\w*|Test-Az|\baz\s+[a-z])'
            if ($azCall -and $line -match '\$env:EIM_DEPLOY_|Secrets\.|Secrets\[|EIMDB_MIGRATOR') { "line $($i + 1) puts a secret on an az command line" }
        }
        Assert-Equal @() @($violations) "$leaf keeps secrets off command lines"
    }

    # ---------------------------------------------------------------------------------------------
    . $deployScriptPath
    . $packageScriptPath
    # Nothing in the self-test may start a process or call Azure.
    function Invoke-Native { throw 'Invoke-Native must not be called by the self-test' }
    function Invoke-AzProcess { throw 'az must not be called by the self-test' }
    function Invoke-Az { throw 'az must not be called by the self-test' }
    function Invoke-AzJson { throw 'az must not be called by the self-test' }
    function Test-Az { throw 'az must not be called by the self-test' }
    function Invoke-Tool { throw 'Invoke-Tool must not be called by the self-test' }

    $gatedSix = @('AwaitingD365SweepHandler', 'QueueCaptureCustomer', 'QueueDataEventsHandler', 'QueueMessageQueueBusinessEventHandler', 'ReleaseSweepHandler', 'RetrySweepHandler')
    $configNames = Get-ConfigurationNames $ConfigDirectory
    $configs = @{}
    foreach ($name in $configNames) { $configs[$name] = Get-DeployConfig $name $ConfigDirectory }

    # ---------------------------------------------------------------------------------------------
    Write-Host '== configurations and names'
    Assert-Equal @('bat', 'nonprod', 'test') $configNames 'the configured names are read from deploy.*.json (base excluded); there is no dev configuration'
    Assert-Equal 'shared' $configs.nonprod.scope 'nonprod is the shared scope'
    foreach ($name in 'bat', 'test') { Assert-Equal 'environment' $configs[$name].scope "$name is an environment scope" }
    Assert-Throws { Get-DeployConfig 'dev' $ConfigDirectory } "'dev'.*Configured: bat, nonprod, test" 'dev is rejected as an unknown configuration, naming it and the configured names'

    $shared = Get-ResourceNames $configs.nonprod
    $expectedShared = [ordered]@{
        ResourceGroup       = 'd365fsc-integration-nonprod'
        ServiceBusNamespace = 'asb-eim-wtc-nonprod'
        KeyVault            = 'kv-d365fsc-wtc-nonprod'
        SqlServer           = 'azql-wtc-sandbox'
        SqlDatabase         = 'eimdb'
        AppServicePlan      = 'asp-eim-wtc-nonprod'
        LogAnalytics        = 'log-eim-wtc-nonprod'
        GatewayApp          = 'azf-eim-gateway-wtc-nonprod'
        GatewayStorage      = 'stwtceimgwnonprod'
        GatewayInsights     = 'appins-eim-gateway-wtc-nonprod'
        DashboardApi        = 'wtc-dashboard-api-dev'
        StaticWebApp        = 'wtc-dashboard-frontend-dev'
    }
    foreach ($key in $expectedShared.Keys) { Assert-Equal $expectedShared[$key] $shared.$key "nonprod $key" }

    foreach ($name in 'bat', 'test') {
        $n = Get-ResourceNames $configs[$name]
        Assert-Equal "azf-eim-automation-wtc-$name" $n.AutomationApp "$name AutomationApp"
        Assert-Equal "stwtceimauto$name" $n.AutomationStorage "$name AutomationStorage"
        Assert-Equal "appins-eim-automation-wtc-$name" $n.AutomationInsights "$name AutomationInsights"
        Assert-Equal $name $n.QueuePrefix "$name QueuePrefix"
        foreach ($key in 'ResourceGroup', 'ServiceBusNamespace', 'SqlServer', 'SqlDatabase', 'AppServicePlan', 'LogAnalytics', 'GatewayApp') {
            Assert-Equal $expectedShared[$key] $n.$key "$name $key (shared resource)"
        }
    }

    # No configuration derives (or overrides to) a dev Automation resource.
    $devAutomationNames = @('azf-eim-automation-wtc-dev', 'stwtceimautodev', 'appins-eim-automation-wtc-dev')
    foreach ($name in $configNames) {
        $derived = foreach ($names in (Get-ResourceNames $configs[$name]), (Get-ResourceNames $configs[$name] -NoOverrides)) { $names.PSObject.Properties | ForEach-Object { "$($_.Value)" } }
        Assert-Equal @() @($derived | Where-Object { $devAutomationNames -contains $_ }) "$name derives no dev Automation resource name"
    }

    $patterns = Get-ResourceNames $configs.nonprod -NoOverrides
    Assert-Equal 'rg-eim-wtc-nonprod' $patterns.ResourceGroup 'pattern resource group'
    Assert-Equal 'sbns-eim-wtc-nonprod' $patterns.ServiceBusNamespace 'pattern Service Bus namespace'
    Assert-Equal 'app-eim-dashboard-api-wtc-nonprod' $patterns.DashboardApi 'pattern dashboard API'
    Assert-Equal 'swa-eim-dashboard-wtc-nonprod' $patterns.StaticWebApp 'pattern static web app'

    $inUse = @(foreach ($name in $configNames) { Get-NamesInUse $configs[$name] }) | Sort-Object -Unique
    foreach ($kept in 'wtc-dashboard-api-dev', 'wtc-dashboard-frontend-dev', 'app-eim-dashboard-api-wtc-nonprod', 'azf-eim-automation-wtc-bat', 'azql-wtc-sandbox') {
        Assert-True ($inUse -contains $kept) "names in use include '$kept'"
    }
    foreach ($name in $configNames) {
        Assert-Equal @() @(Get-LegacyNameConflicts (Get-LegacyNames $configs[$name]) $inUse) "$name has no legacy name equal to a name in use"
    }
    Assert-True (@(Get-LegacyNameConflicts @('azf-eim-gateway-wtc-nonprod') $inUse).Count -eq 1) 'a legacy name equal to a name in use is a conflict'

    # A new configuration file is accepted without code changes; unknown names are rejected.
    $fixtureDir = New-TempFolder
    Copy-Item (Join-Path $ConfigDirectory 'deploy.base.json') $fixtureDir
    '{ "environment": "qa", "scope": "environment" }' | Set-Content (Join-Path $fixtureDir 'deploy.qa.json')
    '{ "environment": "abcdefghijk", "scope": "environment" }' | Set-Content (Join-Path $fixtureDir 'deploy.abcdefghijk.json')
    '{ "environment": "abcdefghijklm", "scope": "environment" }' | Set-Content (Join-Path $fixtureDir 'deploy.abcdefghijklm.json')
    '{ "environment": "other", "scope": "environment" }' | Set-Content (Join-Path $fixtureDir 'deploy.mismatch.json')
    $qa = Get-DeployConfig 'qa' $fixtureDir
    Assert-Equal 'azf-eim-automation-wtc-qa' (Get-ResourceNames $qa).AutomationApp 'a new deploy.qa.json is accepted and named by convention'
    Assert-Equal @('environment', 'preflight', 'infra', 'settings', 'automation', 'verify', 'health') (@($qa.scope) + @(Resolve-Components @('all') $qa.scope $qa.sharedSuffix)) 'the new configuration gets the environment scope components'
    Assert-Throws { Get-DeployConfig 'staging' $ConfigDirectory } "'staging'.*bat, nonprod, test" 'an unknown configuration is rejected, naming it and the configured names'
    Assert-Throws { Get-DeployConfig 'base' $ConfigDirectory } "'base'" 'base is not a configuration'
    Assert-Throws { Get-DeployConfig 'mismatch' $fixtureDir } "declares environment 'other'" 'environment must equal the file name'
    Assert-Throws { Get-ResourceNames (Get-DeployConfig 'abcdefghijk' $fixtureDir) } "Function App name 'azf-eim-automation-wtc-abcdefghijk' exceeds 32" 'a long environment name breaks the Function App length rule'
    Assert-Throws { Get-ResourceNames (Get-DeployConfig 'abcdefghijklm' $fixtureDir) } "Storage account name 'stwtceimautoabcdefghijklm'" 'a longer environment name breaks the storage rule'

    # ---------------------------------------------------------------------------------------------
    Write-Host '== configuration merge'
    $target = '{ "a": { "x": 1, "y": 2 }, "list": [1, 2], "s": "base" }' | ConvertFrom-Json
    Merge-ConfigObject $target ('{ "a": { "y": 3 }, "list": [9], "s": "env" }' | ConvertFrom-Json) '' -Quiet
    Assert-Equal 1 $target.a.x 'objects merge key by key (kept key)'
    Assert-Equal 3 $target.a.y 'objects merge key by key (patched key)'
    Assert-Equal @(9) $target.list 'lists are replaced, not merged'
    Assert-Equal 'env' $target.s 'scalars are replaced'

    $overridesFile = Join-Path $fixtureDir 'overrides.json'
    '{ "gateway": { "maxRequestBytes": 1 }, "automation": { "retryMaxAttempts": "7", "keepDisabled": ["RetrySweepHandler"] } }' | Set-Content $overridesFile
    $layered = Get-DeployConfig 'bat' $ConfigDirectory $overridesFile '{ "automation": { "retryMaxAttempts": "9" } }'
    Assert-Equal 9 $layered.automation.retryMaxAttempts 'EIM_DEPLOY_CONFIG_OVERRIDES is applied last'
    Assert-Equal 1 $layered.gateway.maxRequestBytes 'the overrides file is applied'
    Assert-Equal @('RetrySweepHandler') $layered.automation.keepDisabled 'the overrides file replaces lists'
    Assert-Equal 6 @($layered.automation.gatedFunctions).Count 'unpatched keys survive the overrides'
    Assert-Equal '30' $layered.automation.retryBaseDelaySeconds 'sibling keys survive an object patch'
    Assert-Equal 5 (Get-DeployConfig 'bat' $ConfigDirectory $null '$(EIM_DEPLOY_CONFIG_OVERRIDES)').automation.retryMaxAttempts 'an undefined pipeline variable is ignored'
    Assert-Throws { Get-DeployConfig 'bat' $ConfigDirectory $null '{ "environment": "test" }' } "cannot change 'environment'" 'overrides cannot change the environment'
    Assert-Throws { Get-DeployConfig 'bat' $ConfigDirectory $null '[1]' } 'must be a JSON object' 'overrides must be an object'

    # ---------------------------------------------------------------------------------------------
    Write-Host '== components, scope and order'
    Assert-Equal @('preflight', 'infra', 'settings', 'automation', 'verify', 'health') (Resolve-Components @('all') 'environment' 'nonprod') "'all' for an environment configuration"
    Assert-Equal @('preflight', 'infra', 'settings', 'myip', 'schema', 'myipremove', 'gateway', 'backend', 'frontend', 'verify', 'health') (Resolve-Components @('all') 'shared' 'nonprod') "'all' for the shared configuration"
    Assert-Equal @('preflight', 'settings', 'automation', 'health') (Resolve-Components @('health', 'automation', 'settings') 'environment' 'nonprod') 'requested components run in the fixed order, after preflight'
    Assert-Equal @('verify', 'health') (Resolve-Components @('health', 'verify') 'shared' 'nonprod') 'read-only components need no preflight'
    Assert-Equal @('preflight', 'gateway', 'backend', 'frontend') (Resolve-Components @('frontend', 'gateway', 'backend') 'shared' 'nonprod') 'shared code components in order'
    Assert-Equal @('myipremove') (Resolve-Components @('myipremove') 'shared' 'nonprod') "'myipremove' on its own runs without preflight, so a failing preflight cannot leave the firewall rule open"
    Assert-Equal @('preflight', 'myip', 'schema', 'myipremove') (Resolve-Components @('myip', 'schema', 'myipremove') 'shared' 'nonprod') "'myip' and 'schema' still get preflight"
    Assert-Equal @('preflight', 'myipremove', 'gateway') (Resolve-Components @('gateway', 'myipremove') 'shared' 'nonprod') "'myipremove' with a changing component still gets preflight"
    foreach ($component in 'schema', 'gateway', 'backend', 'frontend', 'myip', 'myipremove') {
        Assert-Throws { Resolve-Components @($component) 'environment' 'nonprod' } "'$component' is a shared component.*-Environment nonprod" "'$component' is rejected for bat, naming the shared configuration"
    }
    Assert-Throws { Resolve-Components @('automation') 'shared' 'nonprod' } "'automation' is a per-environment component.*environment configurations" "'automation' is rejected for nonprod"
    Assert-Equal @('cutover') (Resolve-Components @('cutover') 'environment' 'nonprod') 'cutover runs on its own'
    Assert-Throws { Resolve-Components @('remove', 'automation') 'environment' 'nonprod' } 'run on their own' 'on-demand components cannot be combined'
    Assert-True (-not ((Resolve-Components @('all') 'environment' 'nonprod') -contains 'cutover')) "'all' never includes cutover"

    Assert-Equal @('EIM_DEPLOY_EIMDB', 'EIM_DEPLOY_SERVICEBUS') (Get-RequiredSecrets @('preflight', 'settings') 'environment') 'settings needs both secrets'
    Assert-Equal @('EIM_DEPLOY_EIMDB') (Get-RequiredSecrets @('preflight', 'myip', 'schema', 'myipremove') 'shared') 'schema needs only EIMDB'
    Assert-Equal @() (Get-RequiredSecrets @('preflight', 'automation') 'environment') 'code-only runs need no secrets'
    Assert-Equal @('dbmigrator/ARM.Integration.DbMigrator.exe', 'gateway.zip', 'dashboard-api.zip', 'dashboard-frontend.nonprod.zip') (Get-RequiredArtifacts @('schema', 'gateway', 'backend', 'frontend') $configs.nonprod) 'package artifacts per component'
    Assert-Equal @('wilson-automation.zip') (Get-RequiredArtifacts @('preflight', 'automation') $configs.bat) 'automation artifact'

    $frontendProblems = @(Get-PlaceholderProblems $configs.nonprod @('preflight', 'frontend'))
    Assert-True (($frontendProblems -join ' ') -match 'entra\.tenantId' -and ($frontendProblems -join ' ') -match 'entra\.clientId') 'frontend needs the Entra tenant and client'
    Assert-True (($frontendProblems -join ' ') -notmatch 'entra\.audience|excludedUsers') 'frontend does not need the audience or excluded users'
    Assert-True ((@(Get-PlaceholderProblems $configs.bat @('preflight', 'settings')) -join ' ') -match 'automation\.excludedUsers') 'Automation settings need excludedUsers'
    Assert-Equal @("'subscriptionId' is still a placeholder; fill it in the configuration or EIM_DEPLOY_CONFIG_OVERRIDES") @(Get-PlaceholderProblems $configs.bat @('preflight', 'automation')) 'a code-only run needs only the subscription'

    # ---------------------------------------------------------------------------------------------
    Write-Host '== infra plan'
    $allExisting = @{ Plan = $true; LogAnalytics = $true; ServiceBusNamespace = $true; GatewayStorage = $true; GatewayInsights = $true; GatewayApp = $true; DashboardApi = $true; StaticWebApp = $true; Queues = @{} }
    $planExisting = @(Get-InfraPlan $configs.nonprod $shared $allExisting)
    $existingText = ($planExisting | ForEach-Object { $_.Args -join ' ' }) -join "`n"
    Assert-True ($existingText -notmatch 'wtc-dashboard-api-dev|wtc-dashboard-frontend-dev') 'existing dashboard resources get no infra command at all'
    Assert-True (@($planExisting | Where-Object { $_.Label -like 'create *' }).Count -eq 0) 'nothing is created when every shared resource exists'
    Assert-True ($existingText -match 'functionapp config set -g d365fsc-integration-nonprod -n azf-eim-gateway-wtc-nonprod --always-on true --ftps-state Disabled --min-tls-version 1.2') 'the gateway properties converge on every run'
    Assert-True (@($planExisting | Where-Object { $_.Kind -eq 'runtime' -and $_.Args[0] -eq 'azf-eim-gateway-wtc-nonprod' }).Count -eq 1) 'the gateway runtime is checked on every run'

    $noneExisting = @{ Plan = $false; LogAnalytics = $false; ServiceBusNamespace = $true; Queues = @{} }
    $planNew = @(Get-InfraPlan $configs.nonprod $shared $noneExisting)
    $newText = ($planNew | ForEach-Object { $_.Args -join ' ' }) -join "`n"
    Assert-True ($newText -match 'appservice plan create -g d365fsc-integration-nonprod -n asp-eim-wtc-nonprod -l centralus --is-linux --sku P0V3 --tags eim-workload=eim eim-client=wtc eim-scope=shared eim-environment=nonprod') 'the shared plan is created, tagged'
    Assert-True ($newText -match 'Microsoft\.OperationalInsights/workspaces') 'the Log Analytics workspace is created'
    Assert-True ($newText -match 'storage account create -g d365fsc-integration-nonprod -n stwtceimgwnonprod .*--min-tls-version TLS1_2 .*--tags eim-workload=eim') 'the gateway storage is created, tagged'
    Assert-True ($newText -match 'functionapp create -g d365fsc-integration-nonprod -n azf-eim-gateway-wtc-nonprod --plan asp-eim-wtc-nonprod --storage-account stwtceimgwnonprod --runtime dotnet-isolated --runtime-version 10') 'the gateway is created on the shared plan'
    Assert-True ($newText -match 'webapp create -g d365fsc-integration-nonprod -n wtc-dashboard-api-dev --plan asp-eim-wtc-nonprod --runtime DOTNETCORE:10.0') 'an absent dashboard API is created on the shared plan'
    Assert-True ($newText -match 'webapp update -g d365fsc-integration-nonprod -n wtc-dashboard-api-dev --https-only true') 'a created dashboard API is HTTPS only'
    Assert-True ($newText -match 'staticwebapp create -g d365fsc-integration-nonprod -n wtc-dashboard-frontend-dev -l centralus --sku Standard') 'an absent static web app is created (Standard)'
    $insightsStep = $planNew | Where-Object Kind -eq 'insights' | Select-Object -First 1
    Assert-Equal 'web' $insightsStep.Body.properties.Application_Type 'Insights is created workspace-based (body)'
    foreach ($step in @($planExisting) + @($planNew)) {
        $text = $step.Args -join ' '
        Assert-True ($text -notmatch '\bdelete\b|serverFarmId|appservice plan update|--flexconsumption|FlexConsumption|webapp update .*--set') "infra never deletes, re-plans or uses Flex: $text"
    }

    $batNames = Get-ResourceNames $configs.bat
    $batPlan = @(Get-InfraPlan $configs.bat $batNames @{ Plan = $true; LogAnalytics = $true; ServiceBusNamespace = $true; Queues = @{ 'bat-d365fsc-dataevent' = $true } })
    $batText = ($batPlan | ForEach-Object { $_.Args -join ' ' }) -join "`n"
    foreach ($queue in 'bat-d365fsc-businessevents', 'bat-d365fsc-to-capture', 'bat-capture-to-d365fsc', 'bat-capture-to-d365fsc-response') {
        Assert-True ($batText -match "servicebus queue create -g d365fsc-integration-nonprod --namespace-name asb-eim-wtc-nonprod -n $queue --max-size 1024 --default-message-time-to-live P14D --lock-duration PT60S") "bat creates queue $queue"
    }
    Assert-True ($batText -notmatch 'queue create .*-n bat-d365fsc-dataevent ') 'an existing queue is not recreated'
    Assert-True ($batText -match 'functionapp create -g d365fsc-integration-nonprod -n azf-eim-automation-wtc-bat --plan asp-eim-wtc-nonprod --storage-account stwtceimautobat') 'bat creates its Automation app on the shared plan'
    Assert-True ($batText -match 'eim-scope=environment eim-environment=bat') 'bat resources are tagged with the environment'
    Assert-True ($batText -notmatch 'appservice plan create|gateway|dashboard|staticwebapp') 'bat creates no shared resource'
    $batMissing = @(Get-InfraPlan $configs.bat $batNames @{ Plan = $false; LogAnalytics = $true; ServiceBusNamespace = $true; Queues = @{} })
    Assert-True (@($batMissing | Where-Object { $_.Kind -eq 'require' -and $_.Label -match 'asp-eim-wtc-nonprod.*nonprod' }).Count -eq 1) 'bat requires the shared plan from nonprod'

    # ---------------------------------------------------------------------------------------------
    Write-Host '== settings'
    $fakeSecrets = @{ EIMDB = 'fake-eimdb-value'; ServiceBus = 'fake-servicebus-value' }
    $automation = Get-AutomationSettings $configs.bat $batNames $fakeSecrets 'fake-insights'
    $expectedAutomationNames = @(
        'FUNCTIONS_WORKER_RUNTIME', 'APPLICATIONINSIGHTS_CONNECTION_STRING', 'ServiceBus', 'EIMDB',
        'D365FscDataEventQueue', 'D365BusinessEventQueue', 'D365ToCaptureQueue', 'CaptureToD365Queue', 'CaptureToD365QueueResponse',
        'AwaitingD365SweepSchedule', 'AwaitingD365StuckTimeoutHours', 'ReleaseSweepSchedule', 'ReleaseSweepBatchSize',
        'RetrySweepSchedule', 'RetrySweepBatchSize', 'RetryMaxAttempts', 'RetryBaseDelaySeconds', 'RetryMaxDelaySeconds',
        'DefaultEnvironment', 'ExcludedUsers')
    Assert-Equal $expectedAutomationNames @($automation.Keys) 'Automation setting names'
    Assert-True (-not $automation.Contains('PollSchedule')) 'Automation settings never include PollSchedule'
    Assert-Equal 'bat-capture-to-d365fsc-response' $automation.CaptureToD365QueueResponse 'Automation queues use the environment prefix'
    Assert-Equal 'bat' $automation.DefaultEnvironment 'DefaultEnvironment is the configuration name'

    $retiring = Get-DeployConfig 'bat' $ConfigDirectory $null '{ "automation": { "retiredSettings": ["PollSchedule", "RetryMaxAttempts"] } }'
    Assert-True (-not (Get-AutomationSettings $retiring $batNames $fakeSecrets 'fake-insights').Contains('RetryMaxAttempts')) 'a retired setting is never written'
    $emptyValue = Get-DeployConfig 'bat' $ConfigDirectory $null '{ "automation": { "retryMaxAttempts": "" } }'
    Assert-Throws { Get-AutomationSettings $emptyValue $batNames $fakeSecrets 'fake-insights' } "'RetryMaxAttempts' for 'azf-eim-automation-wtc-bat'" 'an empty setting value fails, naming the setting and app'

    Assert-Equal @('PollSchedule') @(Get-RetiredSettingsToDelete @('PollSchedule') @((New-Setting 'PollSchedule' '0 */5 * * * *'), (New-Setting 'EIMDB' 'x'))) 'PollSchedule is deleted when present'
    Assert-Equal @() @(Get-RetiredSettingsToDelete @('PollSchedule') @((New-Setting 'EIMDB' 'x'))) 'nothing is deleted when PollSchedule is absent'

    $gated = Get-GatedSettingsToWrite $gatedSix @((New-Setting 'AzureWebJobs.RetrySweepHandler.Disabled' 'false'))
    Assert-True (-not $gated.Contains('AzureWebJobs.RetrySweepHandler.Disabled')) 'an existing gated value is never overwritten'
    Assert-Equal 5 $gated.Count 'the other gated settings are written'
    Assert-True (@($gated.Values | Where-Object { $_ -ne 'true' }).Count -eq 0) 'gated settings are written as true'
    Assert-Equal @('AzureWebJobs.QueueCaptureCustomer.Disabled') (Get-MissingGatedSettings @('RetrySweepHandler', 'QueueCaptureCustomer') @((New-Setting 'AzureWebJobs.RetrySweepHandler.Disabled' 'true'))) 'missing gated settings are named'

    $gatewaySettings = Get-GatewaySettings $configs.nonprod $shared $fakeSecrets 'fake-insights'
    Assert-Equal @('FUNCTIONS_WORKER_RUNTIME', 'APPLICATIONINSIGHTS_CONNECTION_STRING', 'EIMDB', 'Gateway__BaseUrl', 'Gateway__MaxRequestBytes', 'Gateway__IdempotencyRetention', 'Gateway__IdempotencyPurgeSchedule') @($gatewaySettings.Keys) 'gateway setting names'
    Assert-Equal 'https://azf-eim-gateway-wtc-nonprod.azurewebsites.net' $gatewaySettings.Gateway__BaseUrl 'gateway base URL from the new app name'

    $dashboard = Get-DashboardApiSettings $configs.nonprod $shared $fakeSecrets
    Assert-Equal @('ASPNETCORE_ENVIRONMENT', 'ConnectionStrings__EIMDB', 'AzureAd__TenantId', 'AzureAd__ClientId', 'AzureAd__Audience',
        'Simulation__ServiceBusConnectionString', 'Simulation__D365FscDataEventQueue', 'Simulation__CaptureToD365Queue', 'Simulation__CaptureToD365ResponseQueue',
        'Simulation__D365ToCaptureQueue', 'Gateway__BaseUrl', 'Gateway__AppName', 'Gateway__Applications__0__Name', 'Gateway__Applications__0__BaseUrl') @($dashboard.Keys) 'dashboard API setting names'
    Assert-Equal 'azf-eim-gateway-wtc-nonprod' $dashboard.Gateway__AppName 'the dashboard points at the new gateway'
    # The legacy name is read from the config: outside the configs and the runbook it must not be written.
    Assert-Equal $configs.nonprod.legacy.gatewayApp $dashboard.Gateway__Applications__0__Name 'the legacy gateway stays verifiable during the overlap'
    Assert-Equal 'bat-d365fsc-dataevent' $dashboard.Simulation__D365FscDataEventQueue 'simulation queues use dashboard.simulationQueuePrefix'
    Assert-Equal 'bat-capture-to-d365fsc-response' $dashboard.Simulation__CaptureToD365ResponseQueue 'the simulation response queue'
    Assert-Equal @('Gateway__Applications__1__Name') @(Get-StaleVerificationTargetSettings $dashboard @((New-Setting 'Gateway__Applications__0__Name' 'x'), (New-Setting 'Gateway__Applications__1__Name' 'y'), (New-Setting 'Other' 'z'))) 'verification targets no longer configured are deleted'

    Write-Host '== drift'
    $expected = Get-AutomationSettings $configs.bat $batNames @{ EIMDB = '<EIM_DEPLOY_EIMDB>'; ServiceBus = '<EIM_DEPLOY_SERVICEBUS>' } '<insights>'
    $current = @($expected.Keys | ForEach-Object { New-Setting $_ "$($expected[$_])" })
    $current = @($current | Where-Object name -notin 'EIMDB', 'ServiceBus', 'APPLICATIONINSIGHTS_CONNECTION_STRING') + @(
        (New-Setting 'EIMDB' 'real-db-value'), (New-Setting 'ServiceBus' 'real-sb-value'), (New-Setting 'APPLICATIONINSIGHTS_CONNECTION_STRING' 'real-insights'),
        (New-Setting 'PollSchedule' '0 */5 * * * *'))
    $drift = Get-SettingsDrift 'azf-eim-automation-wtc-bat' $expected $current @('PollSchedule')
    Assert-Equal 0 $drift.Failures.Count 'a retired setting never fails the drift check'
    Assert-Equal 1 $drift.Warnings.Count 'a retired setting is a warning (secrets and Insights compare by name only)'
    Assert-True ($drift.Warnings[0] -match "retired setting 'PollSchedule'") 'the warning names the retired setting'

    $changed = @($current | ForEach-Object { if ($_.name -eq 'RetryMaxAttempts') { New-Setting 'RetryMaxAttempts' '99' } elseif ($_.name -ne 'ExcludedUsers' -and $_.name -ne 'D365ToCaptureQueue') { $_ } })
    $drift = Get-SettingsDrift 'azf-eim-automation-wtc-bat' $expected $changed @('PollSchedule')
    Assert-Equal 1 $drift.Failures.Count 'missing settings fail'
    Assert-True ($drift.Failures[0] -match 'azf-eim-automation-wtc-bat: missing D365ToCaptureQueue, ExcludedUsers; a manual run with applySettings') 'the failure names the app and every missing setting'
    Assert-True (($drift.Warnings -join ' ') -match "'RetryMaxAttempts' differs") 'a different value warns'
    Assert-True ((@($drift.Failures) + @($drift.Warnings) -join ' ') -notmatch 'real-db-value|real-sb-value|99|0 \*/5') 'drift messages never contain values'

    # ---------------------------------------------------------------------------------------------
    Write-Host '== cutover'
    $legacyStates = [ordered]@{ AwaitingD365SweepHandler = $true; QueueCaptureCustomer = $false; QueueDataEventsHandler = $true; QueueMessageQueueBusinessEventHandler = $true; ReleaseSweepHandler = $false; RetrySweepHandler = $true }
    $plan = @(Get-CutoverPlan $legacyStates $gatedSix @('RetrySweepHandler'))
    Assert-Equal @('True', 'False', 'True', 'True', 'False', 'True') @($plan.Enabled) 'cutover mirrors the legacy states (keepDisabled does not override a legacy state)'
    $plan = @(Get-CutoverPlan $null $gatedSix @('RetrySweepHandler'))
    Assert-Equal @('True', 'True', 'True', 'True', 'True', 'False') @($plan.Enabled) 'with no legacy app everything except keepDisabled is enabled'
    $partial = [ordered]@{ AwaitingD365SweepHandler = $false; QueueCaptureCustomer = $null }
    $plan = @(Get-CutoverPlan $partial @('AwaitingD365SweepHandler', 'QueueCaptureCustomer', 'RetrySweepHandler') @('RetrySweepHandler'))
    Assert-Equal @('False', 'True', 'False') @($plan.Enabled) 'a function missing on the legacy app falls back to keepDisabled'

    $legacySettings = @(
        (New-Setting 'EIMDB' 'db-value-1'), (New-Setting 'ServiceBus' 'sb-value-1'),
        (New-Setting 'D365FscDataEventQueue' 'bat-d365fsc-dataevent'), (New-Setting 'D365BusinessEventQueue' 'bat-d365fsc-businessevents'),
        (New-Setting 'D365ToCaptureQueue' 'bat-d365fsc-to-capture'), (New-Setting 'CaptureToD365Queue' 'bat-capture-to-d365fsc'),
        (New-Setting 'CaptureToD365QueueResponse' 'bat-capture-to-d365fsc-response'), (New-Setting 'RetryMaxAttempts' '5'),
        (New-Setting 'PollSchedule' '0 */5 * * * *'), (New-Setting 'WEBSITE_RUN_FROM_PACKAGE' '1'), (New-Setting 'AzureWebJobsStorage' 'storage-1'),
        (New-Setting 'APPLICATIONINSIGHTS_CONNECTION_STRING' 'insights-1'), (New-Setting 'AzureWebJobs.RetrySweepHandler.Disabled' 'false'))
    $newSettings = @($legacySettings | Where-Object name -notin 'PollSchedule', 'AzureWebJobsStorage', 'APPLICATIONINSIGHTS_CONNECTION_STRING', 'AzureWebJobs.RetrySweepHandler.Disabled') +
    @((New-Setting 'AzureWebJobsStorage' 'storage-2'), (New-Setting 'APPLICATIONINSIGHTS_CONNECTION_STRING' 'insights-2'), (New-Setting 'AzureWebJobs.RetrySweepHandler.Disabled' 'true'))
    $parity = Test-CutoverParity $legacySettings $newSettings @('PollSchedule')
    Assert-Equal 0 $parity.Failures.Count 'parity passes; PollSchedule, storage, Insights and gates are ignored'
    Assert-Equal 0 $parity.Warnings.Count 'parity has no warnings for identical settings'

    $replace = { param($Name, $Value) @($newSettings | ForEach-Object { if ($_.name -eq $Name) { New-Setting $Name $Value } else { $_ } }) }
    foreach ($critical in 'EIMDB', 'ServiceBus', 'D365FscDataEventQueue', 'CaptureToD365QueueResponse') {
        $parity = Test-CutoverParity $legacySettings (& $replace $critical 'other-value-2') @('PollSchedule')
        Assert-True (($parity.Failures -join ' ') -match "'$critical' differs") "a $critical difference fails the cutover"
    }
    $parity = Test-CutoverParity $legacySettings (& $replace 'RetryMaxAttempts' '7') @('PollSchedule')
    Assert-Equal 0 $parity.Failures.Count 'a non-critical difference does not fail'
    Assert-True (($parity.Warnings -join ' ') -match "'RetryMaxAttempts' differs") 'a non-critical difference warns'
    $parity = Test-CutoverParity $legacySettings @($newSettings | Where-Object name -ne 'D365ToCaptureQueue') @('PollSchedule')
    Assert-True (($parity.Failures -join ' ') -match "'D365ToCaptureQueue' is on the legacy app but missing") 'a missing setting fails'
    $parity = Test-CutoverParity $legacySettings @($newSettings | Where-Object name -ne 'D365ToCaptureQueue') @()
    Assert-True (($parity.Failures -join ' ') -match "'PollSchedule'") 'without retiredSettings PollSchedule would fail (the ignore comes from the config)'
    $parity = Test-CutoverParity $legacySettings (& $replace 'EIMDB' 'other-db-secret') @('PollSchedule')
    Assert-True ((@($parity.Failures) + @($parity.Warnings) -join ' ') -notmatch 'db-value-1|other-db-secret|sb-value') 'parity messages never contain values'

    Assert-Equal 90 (ConvertTo-LockSeconds 'PT1M30S') 'ISO 8601 lock duration'
    Assert-Equal 60 (ConvertTo-LockSeconds '00:01:00') 'TimeSpan lock duration'
    Assert-Equal 60 (ConvertTo-LockSeconds '') 'default lock duration'

    $hostKeys = [pscustomobject]@{ _master = 'm'; default = 'd'; consumerA = 'a' }
    Assert-Equal @('consumerA') @((Select-CopyableKeys $hostKeys).Keys) 'master and default keys are not copied'
    Assert-Equal @('default', 'consumerA') @((Select-CopyableKeys $hostKeys -IncludeDefault).Keys) '-IncludeDefaultKeys copies default'

    # Move-GatedTriggers with the Azure calls stubbed. PowerShell resolves commands through the caller's scopes, so
    # stubs defined inside & { } replace the real functions for that block only.
    $stateDir = New-TempFolder
    $statePath = Join-Path $stateDir 'wtc-eim-cutover-bat.json'
    $movePlan = @(Get-CutoverPlan $null $gatedSix @('RetrySweepHandler'))
    $moveCalls = [System.Collections.Generic.List[string]]::new()
    $rerunHint = './Deploy-Wilson.ps1 -Environment bat'
    & {
        function Set-GatedTriggers([string]$ResourceGroup, [string]$App, $Plan) {
            $recorded = if (Test-Path $statePath) { @((Get-Content $statePath -Raw | ConvertFrom-Json).enabled) -join ',' } else { '(no state file)' }
            $moveCalls.Add("$App [$recorded]")
        }
        function Wait-GatedDisabled { }
        function Get-QueueLockSeconds { 0 }
        function Start-Sleep { }
        Move-GatedTriggers $batNames 'legacy-app' 'new-app' $gatedSix $movePlan @('QueueCaptureCustomer', 'RetrySweepHandler') $statePath $false $rerunHint
    }
    Assert-Equal @('legacy-app [QueueCaptureCustomer,RetrySweepHandler]', 'new-app [QueueCaptureCustomer,RetrySweepHandler]') $moveCalls 'cutover records the legacy states before disabling, then enables on the new app'
    Assert-True (-not (Test-Path $statePath)) 'the cutover state file is deleted once the new app is set'

    # A rerun after an interruption (both apps disabled) uses the recorded file, then deletes it.
    @{ enabled = @('QueueCaptureCustomer'); from = 'legacy-app' } | ConvertTo-Json | Set-Content $statePath -Encoding utf8NoBOM
    & {
        function Set-GatedTriggers { }
        function Wait-GatedDisabled { }
        function Get-QueueLockSeconds { 0 }
        function Start-Sleep { }
        Move-GatedTriggers $batNames 'legacy-app' 'new-app' $gatedSix $movePlan @() $statePath $false $rerunHint
    }
    Assert-True (-not (Test-Path $statePath)) 'a rerun deletes the state file it used once the new app is set'

    # A failure before the new app is set keeps the file for the rerun.
    $moveCalls.Clear()
    & {
        function Set-GatedTriggers([string]$ResourceGroup, [string]$App, $Plan) {
            $moveCalls.Add($App)
            if ($App -eq 'new-app') { throw 'simulated failure' }
        }
        function Wait-GatedDisabled { }
        function Get-QueueLockSeconds { 0 }
        function Start-Sleep { }
        Assert-Throws { Move-GatedTriggers $batNames 'legacy-app' 'new-app' $gatedSix $movePlan @('QueueCaptureCustomer') $statePath $false $rerunHint } 'Cutover failed: simulated failure(?s).*Rerun: .*-Component cutover' 'a failed cutover names the rerun'
    }
    Assert-Equal @('legacy-app', 'new-app') $moveCalls 'the failure happened while setting the new app'
    Assert-True (Test-Path $statePath) 'a failed cutover keeps the state file for the rerun'
    Assert-Equal @('QueueCaptureCustomer') @((Get-Content $statePath -Raw | ConvertFrom-Json).enabled) 'the kept file records the legacy states'

    # ---------------------------------------------------------------------------------------------
    Write-Host '== remove'
    # The retired dev Automation app and the old dev plan are nonprod's legacy entries.
    $retiredDev = @(
        [pscustomobject]@{ Name = 'azf-eim-customer-wtc-dev'; Type = 'Microsoft.Web/sites'; Exists = $true; HostedApps = @() }
        [pscustomobject]@{ Name = 'asp-appservice-p0v3-linux-dev'; Type = 'Microsoft.Web/serverfarms'; Exists = $true; HostedApps = @('azf-eim-customer-wtc-dev', 'wtc-dashboard-api-dev') }
        [pscustomobject]@{ Name = 'stwtceimcustomerdev'; Type = 'Microsoft.Storage/storageAccounts'; Exists = $true; HostedApps = @() }
        [pscustomobject]@{ Name = 'appins-eim-customer-wtc-dev'; Type = 'Microsoft.Insights/components'; Exists = $false; HostedApps = @() }
    )
    $removeArgs = @{ Environment = 'nonprod'; Entries = $retiredDev; NamesInUse = $inUse; IsPipeline = $false; Preview = $true; MarkerExists = $false }
    $decision = Test-RemoveAllowed @removeArgs
    Assert-Equal 0 $decision.Refusals.Count 'a preview with legacy entries only is allowed'
    Assert-Equal @('azf-eim-customer-wtc-dev', 'stwtceimcustomerdev') @($decision.Proceed.Name) 'sites go first, then storage; the busy plan is not deleted'
    Assert-True (@($decision.Kept | Where-Object { $_.Name -eq 'asp-appservice-p0v3-linux-dev' -and $_.Reason -match 'wtc-dashboard-api-dev' }).Count -eq 1) 'a plan still hosting an app is kept, naming the app'
    Assert-True (@($decision.Kept | Where-Object { $_.Name -eq 'appins-eim-customer-wtc-dev' -and $_.Reason -match 'not found' }).Count -eq 1) 'an absent entry is reported as not found'

    $emptyPlan = @($retiredDev[0], [pscustomobject]@{ Name = 'asp-appservice-p0v3-linux-dev'; Type = 'Microsoft.Web/serverfarms'; Exists = $true; HostedApps = @('azf-eim-customer-wtc-dev') })
    Assert-Equal @('azf-eim-customer-wtc-dev', 'asp-appservice-p0v3-linux-dev') @((Test-RemoveAllowed @removeArgs -Entries $emptyPlan).Proceed.Name) 'a plan hosting only deleted sites is deleted last'

    Assert-True (((Test-RemoveAllowed @removeArgs -IsPipeline $true).Refusals -join ' ') -match 'TF_BUILD') 'remove refuses in a pipeline'
    Assert-True (((Test-RemoveAllowed @removeArgs -Preview $false).Refusals -join ' ') -match "remove -WhatIf") 'remove refuses without a prior -WhatIf marker'
    Assert-True (((Test-RemoveAllowed @removeArgs -ConfirmDisabled $true).Refusals -join ' ') -match 'Confirm') 'remove refuses -Confirm:$false'
    Assert-True (((Test-RemoveAllowed @removeArgs -EnabledLegacyFunctions @('QueueCaptureCustomer')).Refusals -join ' ') -match 'QueueCaptureCustomer') 'remove refuses while legacy triggers are enabled, naming them'
    Assert-True (((Test-RemoveAllowed @removeArgs -Preview $false -MarkerExists $true -CheckConfirmation $true -Confirmation 'bat').Refusals -join ' ') -match "did not match 'nonprod'") 'remove refuses a wrong confirmation'
    Assert-Equal 0 (Test-RemoveAllowed @removeArgs -Preview $false -MarkerExists $true -CheckConfirmation $true -Confirmation 'nonprod').Refusals.Count 'the right confirmation after a preview is accepted'
    $retiredRefusal = (Test-RemoveAllowed @removeArgs -EnabledLegacyFunctions @('azf-eim-customer-wtc-dev/QueueCaptureCustomer')).Refusals -join ' '
    Assert-True ($retiredRefusal -match 'azf-eim-customer-wtc-dev/QueueCaptureCustomer' -and $retiredRefusal -match 'retired') 'remove refuses while a retired app has an enabled gated function, naming the app, the function and the retired case'
    $inUseEntry = @([pscustomobject]@{ Name = 'wtc-dashboard-api-dev'; Type = 'Microsoft.Web/sites'; Exists = $true; HostedApps = @() })
    Assert-True (((Test-RemoveAllowed @removeArgs -Entries $inUseEntry).Refusals -join ' ') -match "'wtc-dashboard-api-dev' is a name in use") 'remove refuses a name in use (the kept dashboard API)'
    $swaEntry = @([pscustomobject]@{ Name = 'some-old-swa'; Type = 'Microsoft.Web/staticSites'; Exists = $true; HostedApps = @() })
    Assert-True (((Test-RemoveAllowed @removeArgs -Entries $swaEntry).Refusals -join ' ') -match "'Microsoft.Web/staticSites', which remove never deletes") 'remove refuses a static web app (type allow-list)'
    $vaultEntry = @([pscustomobject]@{ Name = 'some-old-vault'; Type = 'Microsoft.KeyVault/vaults'; Exists = $true; HostedApps = @() })
    Assert-True ((Test-RemoveAllowed @removeArgs -Entries $vaultEntry).Refusals.Count -eq 1) 'remove refuses any type outside the allow-list'

    # Trigger guard: the legacy Automation app per environment, plus nonprod's retired Automation apps.
    Assert-Equal @('azf-eim-customer-wtc-dev') @(Get-TriggerGuardApps $configs.nonprod) 'nonprod guards the retired dev Automation app'
    Assert-Equal @('azf-eim-customer-wtc-bat') @(Get-TriggerGuardApps $configs.bat) 'bat guards its legacy Automation app'
    Assert-Equal @('azf-eim-customer-wtc-test') @(Get-TriggerGuardApps $configs.test) 'test guards its legacy Automation app'
    $guardFixture = '{ "legacy": { "automationApp": "app-a", "retiredAutomationApps": ["app-a", " ", "app-b", ""] } }' | ConvertFrom-Json
    Assert-Equal @('app-a', 'app-b') @(Get-TriggerGuardApps $guardFixture) 'duplicate and blank guard apps collapse to one entry each'
    Assert-Equal @() @(Get-TriggerGuardApps ('{ "scope": "environment" }' | ConvertFrom-Json)) 'no legacy section, no guard apps'

    $nonprodLegacy = @(Get-LegacyNames $configs.nonprod)
    foreach ($name in 'azf-eim-customer-wtc-dev', 'stwtceimcustomerdev', 'appins-eim-customer-wtc-dev') {
        Assert-True ($nonprodLegacy -contains $name) "nonprod legacy names include '$name'"
    }
    foreach ($app in @($configs.nonprod.legacy.retiredAutomationApps)) {
        Assert-True (@($configs.nonprod.legacy.resources | Where-Object { $_.name -eq $app -and $_.type -eq 'Microsoft.Web/sites' }).Count -eq 1) "retired app '$app' is also a Microsoft.Web/sites legacy resource"
    }

    # Invoke-RemoveComponent with Azure stubbed: the guard reads every trigger-guard app, stopped or not.
    $markerDir = New-TempFolder
    # The legacy gateway name is read from the config: outside the configs and the runbook it must not be written.
    $legacyGateway = $configs.nonprod.legacy.gatewayApp
    $nonprodEntries = @(
        [pscustomobject]@{ Name = $legacyGateway; Type = 'Microsoft.Web/sites'; Exists = $true; Id = 'id-gw'; HostedApps = @() }
        [pscustomobject]@{ Name = 'stwtcd365gwnonprod'; Type = 'Microsoft.Storage/storageAccounts'; Exists = $true; Id = 'id-gwst'; HostedApps = @() }
        [pscustomobject]@{ Name = 'appins-d365fsc-gateway-wtc-nonprod'; Type = 'Microsoft.Insights/components'; Exists = $true; Id = 'id-gwai'; HostedApps = @() }
        [pscustomobject]@{ Name = 'azf-eim-customer-wtc-dev'; Type = 'Microsoft.Web/sites'; Exists = $true; Id = 'id-dev'; HostedApps = @() }
        [pscustomobject]@{ Name = 'stwtceimcustomerdev'; Type = 'Microsoft.Storage/storageAccounts'; Exists = $true; Id = 'id-devst'; HostedApps = @() }
        [pscustomobject]@{ Name = 'appins-eim-customer-wtc-dev'; Type = 'Microsoft.Insights/components'; Exists = $false; Id = $null; HostedApps = @() }
        [pscustomobject]@{ Name = 'asp-appservice-p0v3-linux-dev'; Type = 'Microsoft.Web/serverfarms'; Exists = $true; Id = 'id-plan'; HostedApps = @('azf-eim-customer-wtc-dev', 'wtc-dashboard-api-dev') }
    )
    $removeOutput = [System.Collections.Generic.List[string]]::new()
    $guardReads = [System.Collections.Generic.List[string]]::new()
    $savedTfBuild = $env:TF_BUILD
    try {
        $env:TF_BUILD = $null
        $runRemove = {
            param($Config, $Names, [hashtable]$SettingsByApp)
            $Preview = $true
            $ConfirmDisabled = $false
            function Get-RemoveMarkerPath([string]$Environment) { Join-Path $markerDir "remove-$Environment.marker" }
            function Get-RemoveEntries { if ($Config.environment -eq 'nonprod') { $nonprodEntries } else { @() } }
            function Test-WebApp([string]$ResourceGroup, [string]$Name) { $guardReads.Add($Name); $SettingsByApp.ContainsKey($Name) }
            # A stopped app cannot list its functions: only the gated settings decide.
            function Invoke-AzProcess { [pscustomobject]@{ ExitCode = 1; StdOut = ''; StdErr = 'stopped' } }
            function Get-AppSettingList([string]$ResourceGroup, [string]$Name) { @($SettingsByApp[$Name]) }
            function Write-Host { $removeOutput.Add("$args") }
            Invoke-RemoveComponent $Config $Names
        }
        $allDisabled = @($gatedSix | ForEach-Object { New-Setting "AzureWebJobs.$_.Disabled" 'true' })
        $oneEnabled = @($allDisabled | ForEach-Object { if ($_.name -eq 'AzureWebJobs.QueueCaptureCustomer.Disabled') { New-Setting $_.name 'false' } else { $_ } })

        Assert-Throws { & $runRemove $configs.nonprod $shared @{ 'azf-eim-customer-wtc-dev' = $oneEnabled } } 'azf-eim-customer-wtc-dev/QueueCaptureCustomer' "remove for nonprod refuses while QueueCaptureCustomer is enabled on the retired app"
        Assert-Equal @('azf-eim-customer-wtc-dev') @($guardReads) 'the nonprod guard reads the retired app'
        Assert-True (@($removeOutput | Where-Object { $_ -match 'would delete' }).Count -eq 0) 'a refused remove lists nothing for deletion'
        Assert-True (-not (Test-Path (Join-Path $markerDir 'remove-nonprod.marker'))) 'a refused remove records no preview'

        $stoppedRefusal = $null
        try { & $runRemove $configs.nonprod $shared @{ 'azf-eim-customer-wtc-dev' = @() } } catch { $stoppedRefusal = $_.Exception.Message }
        Assert-True ($null -ne $stoppedRefusal -and @($gatedSix | Where-Object { $stoppedRefusal -notmatch "azf-eim-customer-wtc-dev/$_" }).Count -eq 0) 'a stopped retired app without Disabled settings is refused, naming every enabled function'

        $removeOutput.Clear()
        & $runRemove $configs.nonprod $shared @{ 'azf-eim-customer-wtc-dev' = $allDisabled }
        $listed = @($removeOutput | Where-Object { $_ -match 'would delete' })
        foreach ($name in 'azf-eim-customer-wtc-dev', 'stwtceimcustomerdev', $legacyGateway, 'stwtcd365gwnonprod', 'appins-d365fsc-gateway-wtc-nonprod') {
            Assert-True (@($listed | Where-Object { $_ -match [regex]::Escape($name) }).Count -eq 1) "with the retired app disabled, remove -WhatIf for nonprod lists '$name'"
        }
        Assert-True (@($removeOutput | Where-Object { $_ -match 'keep .*appins-eim-customer-wtc-dev .*not found' }).Count -eq 1) 'an absent retired Insights is reported as not found'
        Assert-True (@($removeOutput | Where-Object { $_ -match 'keep .*asp-appservice-p0v3-linux-dev .*wtc-dashboard-api-dev' }).Count -eq 1) 'the old dev plan is kept while the dashboard API is on it'
        Assert-True (Test-Path (Join-Path $markerDir 'remove-nonprod.marker')) 'an allowed preview records the marker'

        $guardReads.Clear()
        Assert-Throws { & $runRemove $configs.bat (Get-ResourceNames $configs.bat) @{ 'azf-eim-customer-wtc-bat' = $oneEnabled } } 'azf-eim-customer-wtc-bat/QueueCaptureCustomer' 'remove for bat refuses while its legacy app consumes, naming the function'
        Assert-Equal @('azf-eim-customer-wtc-bat') @($guardReads) 'the bat guard reads only its legacy app'
    }
    finally { $env:TF_BUILD = $savedTfBuild }

    # ---------------------------------------------------------------------------------------------
    Write-Host '== verify targets'
    $verifyNonprod = @(Get-VerifyTargets $configs.nonprod $shared)
    $retiredTarget = @($verifyNonprod | Where-Object { $_.Name -eq 'azf-eim-customer-wtc-dev' })
    Assert-Equal 1 $retiredTarget.Count 'verify for nonprod reports the retired dev Automation app'
    Assert-True ($retiredTarget[0].Kind -eq 'app' -and $retiredTarget[0].Gated -and $retiredTarget[0].Label -match 'retired') 'the retired app is reported with its gated trigger states and a retired label'
    Assert-Equal @('azf-eim-gateway-wtc-nonprod', $configs.nonprod.legacy.gatewayApp, 'wtc-dashboard-api-dev', 'wtc-dashboard-frontend-dev', 'azf-eim-customer-wtc-dev') @($verifyNonprod.Name) 'verify for nonprod: gateways, dashboard, then retired apps'
    Assert-Equal @('azf-eim-automation-wtc-bat', 'azf-eim-customer-wtc-bat') @((Get-VerifyTargets $configs.bat (Get-ResourceNames $configs.bat)).Name) 'verify for bat: the new and legacy Automation apps'

    # ---------------------------------------------------------------------------------------------
    Write-Host '== SQL firewall helpers'
    Assert-Equal 'eim-deploy-4711' (Get-FirewallRuleName '4711' 'someone') 'pipeline firewall rule name'
    Assert-Equal 'AllowDev-someone' (Get-FirewallRuleName '' 'someone') 'operator firewall rule name'
    Assert-Equal 'azql-wtc-sandbox' (Get-SqlServerFromConnection 'Server=tcp:azql-wtc-sandbox.database.windows.net,1433;Initial Catalog=eimdb;User ID=<sql user>;Password=<sql password>') 'server from Server=tcp:'
    Assert-Equal 'azql-wtc-sandbox' (Get-SqlServerFromConnection 'Data Source=azql-wtc-sandbox.database.windows.net;Initial Catalog=eimdb') 'server from Data Source='
    Assert-Throws { Get-SqlServerFromConnection 'Host=elsewhere;Secret=SECRETVALUE123' } '^(?!.*SECRETVALUE123)' 'an unreadable connection string fails without echoing it'

    # 'myipremove' runs without preflight, so it resolves EIM_DEPLOY_EIMDB itself.
    $savedEnv = @{ EIM_DEPLOY_EIMDB = $env:EIM_DEPLOY_EIMDB; BUILD_BUILDID = $env:BUILD_BUILDID }
    $savedEimdb = $script:Secrets.EIMDB
    $azCalls = [System.Collections.Generic.List[string]]::new()
    try {
        $script:Secrets.EIMDB = $null
        $env:EIM_DEPLOY_EIMDB = 'Server=tcp:azql-wtc-sandbox.database.windows.net,1433;Initial Catalog=eimdb'
        $env:BUILD_BUILDID = '4711'
        & {
            function Invoke-AzProcess([string[]]$Arguments) { $azCalls.Add($Arguments -join ' '); [pscustomobject]@{ ExitCode = 0; StdOut = ''; StdErr = '' } }
            Invoke-MyIpComponent $configs.nonprod $shared -Remove
        }
        Assert-Equal @('sql server firewall-rule delete -g d365fsc-integration-nonprod -s azql-wtc-sandbox -n eim-deploy-4711 -o none') $azCalls "'myipremove' alone reads the secret and deletes the agent's rule"

        $script:Secrets.EIMDB = $null
        $env:EIM_DEPLOY_EIMDB = $null
        & {
            function Test-Interactive { $false }
            Assert-Throws { Invoke-MyIpComponent $configs.nonprod $shared -Remove } 'EIM_DEPLOY_EIMDB is not set' "'myipremove' without the secret fails, naming it"
        }
    }
    finally {
        $env:EIM_DEPLOY_EIMDB = $savedEnv.EIM_DEPLOY_EIMDB
        $env:BUILD_BUILDID = $savedEnv.BUILD_BUILDID
        $script:Secrets.EIMDB = $savedEimdb
    }

    # ---------------------------------------------------------------------------------------------
    Write-Host '== packaging helpers'
    $metadataJson = Get-Content $metadataFixture -Raw
    Assert-Equal $gatedSix (Get-QueueAndTimerFunctions $metadataJson) 'the fixture yields exactly the six queue and timer functions'
    Assert-Equal @() @(Compare-GatedFunctions (Get-QueueAndTimerFunctions $metadataJson) @($configs.bat.automation.gatedFunctions)) 'the fixture matches automation.gatedFunctions'
    Assert-True ((@(Compare-GatedFunctions (@($gatedSix) + 'NewQueueHandler') $gatedSix) -join ' ') -match "'NewQueueHandler' is not in automation.gatedFunctions") 'a new queue function not in the config is named'
    Assert-True ((@(Compare-GatedFunctions @($gatedSix | Select-Object -Skip 1) $gatedSix) -join ' ') -match "'AwaitingD365SweepHandler', which the package does not contain") 'a configured function missing from the package is named'
    Assert-True ((Get-FunctionNames $metadataJson) -contains 'HealthProbe') 'Get-FunctionNames lists HTTP functions too'
    $readme = Get-PackageReadme 'v1' 'abc1234' @('nonprod', 'bat', 'test')
    Assert-True ($readme -match 'Deploy-Wilson\.ps1 -Environment nonprod -Component all -WhatIf') 'README.txt carries the operator steps'
    Assert-True ($readme -cnotmatch '\bdev\b') 'README.txt names no dev environment'
    $packageAst = [System.Management.Automation.Language.Parser]::ParseFile($packageScriptPath, [ref]$null, [ref]$null)
    $environmentParam = $packageAst.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'Environment' }
    $defaultEnvironments = @($environmentParam.DefaultValue.FindAll({ param($node) $node -is [System.Management.Automation.Language.StringConstantExpressionAst] }, $true) | ForEach-Object { $_.Value })
    Assert-Equal @('nonprod', 'bat', 'test') $defaultEnvironments 'New-DeploymentPackage.ps1 packages nonprod, bat and test by default'

    $zipSource = New-TempFolder
    New-Item -ItemType Directory -Path (Join-Path $zipSource '.azurefunctions') | Out-Null
    'x' | Set-Content (Join-Path $zipSource '.azurefunctions/function.deps.json')
    '{}' | Set-Content (Join-Path $zipSource 'host.json')
    $zipPath = Join-Path (New-TempFolder) 'probe.zip'
    New-Zip $zipSource $zipPath
    $entries = Get-ZipEntryNames $zipPath
    Assert-True (@($entries | Where-Object { $_ -like '.azurefunctions/*' }).Count -gt 0) "New-Zip keeps .azurefunctions/ (entries: $($entries -join ', '))"
    Assert-True ($entries -contains 'host.json') 'New-Zip keeps top-level files'

    # ---------------------------------------------------------------------------------------------
    if ($packageToCheck) {
        Write-Host "== package $packageToCheck"
        $artifactsDir = Join-Path $packageToCheck 'artifacts'
        foreach ($required in 'Deploy-Wilson.ps1', 'README.txt', 'config/deploy.base.json', 'config/deploy.nonprod.json', 'artifacts/manifest.json', 'artifacts/gateway.zip', 'artifacts/dashboard-api.zip', 'artifacts/wilson-automation.zip', 'artifacts/dbmigrator/ARM.Integration.DbMigrator.exe') {
            Assert-True (Test-Path (Join-Path $packageToCheck $required)) "the package has no '$required'"
        }
        $automationZip = Join-Path $artifactsDir 'wilson-automation.zip'
        $automationEntries = Get-ZipEntryNames $automationZip
        Assert-True (@($automationEntries | Where-Object { $_ -like '.azurefunctions/*' }).Count -gt 0) "wilson-automation.zip has no '.azurefunctions/' entry"
        $packagedMetadata = Get-ZipEntryText $automationZip 'functions.metadata'
        Assert-True ($null -ne $packagedMetadata) "wilson-automation.zip has no 'functions.metadata' entry"
        $packagedFunctions = Get-FunctionNames $packagedMetadata
        foreach ($function in $gatedSix) { Assert-True ($packagedFunctions -contains $function) "wilson-automation.zip functions.metadata does not list '$function'" }
        foreach ($function in $packagedFunctions) { Assert-True ($gatedSix -contains $function) "wilson-automation.zip functions.metadata lists '$function', which is not a gated function" }
        $manifest = Get-Content (Join-Path $artifactsDir 'manifest.json') -Raw | ConvertFrom-Json
        Assert-Equal $gatedSix @($manifest.automationQueueAndTimerFunctions | Sort-Object) 'manifest.json automationQueueAndTimerFunctions'
        Assert-True (@($manifest.gatewayFunctions).Count -gt 0) 'manifest.json lists the gateway functions'
        foreach ($name in @($manifest.frontend)) {
            Assert-True (Test-Path (Join-Path $artifactsDir "dashboard-frontend.$name.zip")) "the manifest lists a frontend for '$name' but the package has no dashboard-frontend.$name.zip"
        }
        foreach ($zip in 'gateway.zip', 'wilson-automation.zip') {
            Assert-True ((Get-ZipEntryNames (Join-Path $artifactsDir $zip)) -contains 'host.json') "$zip has no 'host.json' entry"
        }
    }

    Write-Host "Test-DeployWilson: $($script:AssertionCount) assertions passed$(if ($packageToCheck) { ' (including the package checks)' })"
}
catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host "Test-DeployWilson: failed after $($script:AssertionCount) passing assertions"
    exit 1
}
finally {
    foreach ($path in $tempRoots) { Remove-Item $path -Recurse -Force -ErrorAction SilentlyContinue }
}
exit 0
