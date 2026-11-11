<#
.SYNOPSIS
    Dependency-free self-test for Deploy-Wilson.ps1 and New-DeploymentPackage.ps1.

.DESCRIPTION
    Parses the deployment scripts, dot-sources them (their main bodies are guarded, so nothing runs and no Azure
    call is made) and asserts the pure functions: configurations, "uses" and isolation, naming, component order,
    infra and SQL plans, database users, settings, gated triggers, drift, Key Vault references, verify targets,
    and the packaging helpers. Components that would call Azure run only with stubs or under -WhatIf. Throws on
    the first failed assertion and exits 1.

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
    $allConfigs = Get-AllDeployConfigs $null $ConfigDirectory
    # Synthetic configurations: the shipped base plus a file with only an environment (and "uses").
    $fixtureDir = New-TempFolder
    Copy-Item (Join-Path $ConfigDirectory 'deploy.base.json') $fixtureDir
    foreach ($name in 'qa', 'prdmck') { "{ `"environment`": `"$name`" }" | Set-Content (Join-Path $fixtureDir "deploy.$name.json") }
    '{ "environment": "uat", "uses": "prd" }' | Set-Content (Join-Path $fixtureDir 'deploy.uat.json')
    $removedSuffixKey = 'shared' + 'Suffix'

    # ---------------------------------------------------------------------------------------------
    Write-Host '== configurations'
    Assert-Equal @('bat', 'prd', 'test') $configNames 'the configured names are read from deploy.*.json (base excluded): bat, prd and test'
    Assert-Equal @('bat', 'prd', 'test') @($allConfigs.Keys) 'Get-AllDeployConfigs reads every configuration'
    Assert-Equal @('prd') @($configNames | Where-Object { $configs[$_].production -eq $true }) 'prd is the only production configuration'
    Assert-Equal 'bat' $configs.test.uses 'test uses bat'
    foreach ($name in 'bat', 'prd') { Assert-True ([string]::IsNullOrWhiteSpace("$($configs[$name].uses)")) "$name has no uses (an owner)" }
    Assert-Throws { Get-DeployConfig 'dev' $ConfigDirectory } "'dev'.*Configured: bat, prd, test" 'dev is rejected as an unknown configuration, naming it and the configured names'
    Assert-Throws { Get-DeployConfig 'base' $ConfigDirectory } "No deployment configuration named 'base'" 'base is not a configuration'
    Assert-Throws { Get-DeployConfig 'qa' $fixtureDir $null '{ "scope": "environment" }' } "sets 'scope', which is no longer supported" 'a configuration with scope is rejected'
    Assert-Throws { Get-DeployConfig 'qa' $fixtureDir $null "{ `"$removedSuffixKey`": `"x`" }" } "sets '$removedSuffixKey', which is no longer supported" 'a configuration with the removed suffix key is rejected'
    Assert-Equal 'qa' (Get-DeployConfig 'qa' $fixtureDir).environment 'a file with only an environment is a complete configuration'

    # ---------------------------------------------------------------------------------------------
    Write-Host '== names'
    $batNames = Get-ResourceNames $configs.bat
    $expectedBat = [ordered]@{
        ResourceGroup       = 'd365fsc-integration-nonprod'
        ServiceBusNamespace = 'asb-eim-wtc-nonprod'
        KeyVault            = 'kv-d365fsc-wtc-nonprod'
        SqlServer           = 'azql-wtc-sandbox'
        SqlDatabase         = 'eimdb'
        AppServicePlan      = 'asp-eim-wtc-bat'
        LogAnalytics        = 'log-eim-wtc-bat'
        GatewayApp          = 'azf-eim-gateway-wtc-bat'
        GatewayStorage      = 'stwtceimgwbat'
        GatewayInsights     = 'appins-eim-gateway-wtc-bat'
        DashboardApi        = 'wtc-dashboard-api-dev'
        StaticWebApp        = 'wtc-dashboard-frontend-dev'
        AutomationApp       = 'azf-eim-automation-wtc-bat'
        AutomationStorage   = 'stwtceimautobat'
        AutomationInsights  = 'appins-eim-automation-wtc-bat'
        QueuePrefix         = 'bat'
    }
    Assert-Equal @($expectedBat.Keys) @($batNames.PSObject.Properties.Name) 'the resolved names have exactly these keys'
    foreach ($key in $expectedBat.Keys) { Assert-Equal $expectedBat[$key] $batNames.$key "bat $key" }

    $testNames = Get-ResourceNames $configs.test $configs.bat
    $expectedTest = [ordered]@{}
    foreach ($key in $expectedBat.Keys) { $expectedTest[$key] = $expectedBat[$key] }
    $expectedTest.AutomationApp = 'azf-eim-automation-wtc-test'
    $expectedTest.AutomationStorage = 'stwtceimautotest'
    $expectedTest.AutomationInsights = 'appins-eim-automation-wtc-test'
    $expectedTest.QueuePrefix = 'test'
    foreach ($key in $expectedTest.Keys) { Assert-Equal $expectedTest[$key] $testNames.$key "test $key (inherited from bat unless its own)" }

    # A configuration with "uses" may own its Key Vault and Log Analytics workspace; every other name stays bat's.
    $testWith = { param([string]$Json) Get-DeployConfig 'test' $ConfigDirectory $null $Json 6>$null }
    $ownVaultConfig = & $testWith '{ "names": { "keyVault": "kv-eim-wtc-test" } }'
    $ownVaultNames = Get-ResourceNames $ownVaultConfig $configs.bat
    foreach ($key in $expectedTest.Keys) {
        Assert-Equal $(if ($key -eq 'KeyVault') { 'kv-eim-wtc-test' } else { $expectedTest[$key] }) $ownVaultNames.$key "test with its own vault: $key"
    }
    Assert-Equal 'asp-eim-wtc-bat' $ownVaultNames.AppServicePlan 'test with its own vault stays on the bat plan'
    $ownWorkspaceConfig = & $testWith '{ "names": { "logAnalytics": "log-eim-wtc-test" } }'
    $ownWorkspaceNames = Get-ResourceNames $ownWorkspaceConfig $configs.bat
    foreach ($key in $expectedTest.Keys) {
        Assert-Equal $(if ($key -eq 'LogAnalytics') { 'log-eim-wtc-test' } else { $expectedTest[$key] }) $ownWorkspaceNames.$key "test with its own workspace: $key"
    }
    $blankWorkspaceConfig = & $testWith '{ "names": { "logAnalytics": "" } }'
    Assert-Equal 'log-eim-wtc-bat' (Get-ResourceNames $blankWorkspaceConfig $configs.bat).LogAnalytics 'a blank overridable name inherits bat''s workspace, not log-eim-wtc-test'
    $equalVaultConfig = & $testWith '{ "names": { "keyVault": "KV-D365FSC-WTC-NONPROD" } }'
    $equalVaultNames = Get-ResourceNames $equalVaultConfig $configs.bat
    Assert-Equal 'kv-d365fsc-wtc-nonprod' $equalVaultNames.KeyVault 'an override equal to bat''s vault takes bat''s spelling'
    Assert-Equal @('keyVault') @(Get-OwnedNameKeys $ownVaultConfig $configs.bat) 'test owns its vault'
    Assert-Equal @('logAnalytics') @(Get-OwnedNameKeys $ownWorkspaceConfig $configs.bat) 'test owns its workspace'
    Assert-Equal @() @(Get-OwnedNameKeys $configs.test $configs.bat) 'the shipped test owns nothing'
    Assert-Equal @() @(Get-OwnedNameKeys $blankWorkspaceConfig $configs.bat) 'a blank override owns nothing'
    Assert-Equal @() @(Get-OwnedNameKeys $equalVaultConfig $configs.bat) 'an override equal to bat''s name owns nothing'
    Assert-Equal @() @(Get-OwnedNameKeys $configs.bat $null) 'an owner has no owned keys'
    $ownListing = @(Format-NameListing $ownVaultNames @('keyVault'))
    Assert-Equal @('KeyVault') @($ownListing | Where-Object { $_ -match ' \(own\)$' } | ForEach-Object { ($_.Trim() -split '\s+')[0] }) 'the name listing marks only the owned vault (own)'
    Assert-True (@($ownListing | Where-Object { $_ -match '^\s+KeyVault\s+kv-eim-wtc-test \(own\)$' }).Count -eq 1) "the listing reads 'KeyVault kv-eim-wtc-test (own)'"
    Assert-Equal @() @(Format-NameListing $testNames @() | Where-Object { $_ -match '\(own\)' }) 'the shipped test listing marks nothing (own)'
    Assert-Equal $ownListing.Count @(Format-NameListing $testNames).Count 'the listing has one line per name'
    Assert-Throws { Get-ResourceNames (& $testWith '{ "names": { "keyVault": "kv_eim_wtc_test" } }') $configs.bat } "Key Vault name 'kv_eim_wtc_test'" 'an invalid owned vault name is named'

    $prdNames = Get-ResourceNames $configs.prd
    $expectedPrd = [ordered]@{
        ResourceGroup       = 'd365fsc-integration-prod'
        ServiceBusNamespace = 'sbns-eim-wtc-prd'
        KeyVault            = 'kv-eim-wtc-prd'
        SqlServer           = 'sql-eim-wtc-prd'
        SqlDatabase         = 'sqldb-eim-wtc-prd'
        AppServicePlan      = 'asp-eim-wtc-prd'
        LogAnalytics        = 'log-eim-wtc-prd'
        GatewayApp          = 'azf-eim-gateway-wtc-prd'
        GatewayStorage      = 'stwtceimgwprd'
        GatewayInsights     = 'appins-eim-gateway-wtc-prd'
        DashboardApi        = 'app-eim-dashboard-api-wtc-prd'
        StaticWebApp        = 'swa-eim-dashboard-wtc-prd'
        AutomationApp       = 'azf-eim-automation-wtc-prd'
        AutomationStorage   = 'stwtceimautoprd'
        AutomationInsights  = 'appins-eim-automation-wtc-prd'
        QueuePrefix         = 'prd'
    }
    foreach ($key in $expectedPrd.Keys) { Assert-Equal $expectedPrd[$key] $prdNames.$key "prd $key" }

    # A configuration with only an environment derives a complete, isolated set of names.
    $mck = Get-DeployConfig 'prdmck' $fixtureDir
    $mckNames = Get-ResourceNames $mck
    $expectedMck = [ordered]@{
        ResourceGroup       = 'rg-eim-wtc-prdmck'
        ServiceBusNamespace = 'sbns-eim-wtc-prdmck'
        KeyVault            = 'kv-eim-wtc-prdmck'
        SqlServer           = 'sql-eim-wtc-prdmck'
        SqlDatabase         = 'sqldb-eim-wtc-prdmck'
        AppServicePlan      = 'asp-eim-wtc-prdmck'
        LogAnalytics        = 'log-eim-wtc-prdmck'
        GatewayApp          = 'azf-eim-gateway-wtc-prdmck'
        GatewayStorage      = 'stwtceimgwprdmck'
        GatewayInsights     = 'appins-eim-gateway-wtc-prdmck'
        DashboardApi        = 'app-eim-dashboard-api-wtc-prdmck'
        StaticWebApp        = 'swa-eim-dashboard-wtc-prdmck'
        AutomationApp       = 'azf-eim-automation-wtc-prdmck'
        AutomationStorage   = 'stwtceimautoprdmck'
        AutomationInsights  = 'appins-eim-automation-wtc-prdmck'
        QueuePrefix         = 'prdmck'
    }
    foreach ($key in $expectedMck.Keys) { Assert-Equal $expectedMck[$key] $mckNames.$key "prdmck $key" }
    Assert-Equal 'kv-eim-wtc-prdmck' (Get-ResourceNames (Get-DeployConfig 'prdmck' $fixtureDir $null '{ "names": { "keyVault": "" } }')).KeyVault 'a blank override falls back to the derived name'
    Assert-Equal 'kv-other' (Get-ResourceNames (Get-DeployConfig 'prdmck' $fixtureDir $null '{ "names": { "keyVault": "kv-other" } }')).KeyVault 'a filled override wins'

    # Name limits. Nine characters still fit every rule; ten break the Function App limit (32), thirteen the
    # storage limit (24), fourteen the Key Vault limit (24) too.
    $named = { param($Environment) [pscustomobject]@{ client = 'wtc'; workload = 'eim'; environment = $Environment } }
    Assert-Equal 'azf-eim-automation-wtc-abcdefghi' (Get-ResourceNames (& $named 'abcdefghi')).AutomationApp 'a nine-character environment fits every name rule'
    Assert-Throws { Get-ResourceNames (& $named 'abcdefghij') } "Function App name 'azf-eim-automation-wtc-abcdefghij' exceeds 32" 'a ten-character environment fails the Function App limit, naming the app'
    Assert-Throws { Get-ResourceNames (& $named 'abcdefghijklm') } "storage account name 'stwtceimautoabcdefghijklm'" 'a thirteen-character environment fails the storage rule, naming the account'
    Assert-Throws { Get-ResourceNames (& $named 'abcdefghijklmn') } "Key Vault name 'kv-eim-wtc-abcdefghijklmn'" 'a fourteen-character environment fails the Key Vault rule, naming the vault'
    Assert-Throws { Get-ResourceNames (Get-DeployConfig 'qa' $fixtureDir $null '{ "names": { "sqlServer": "Sql_Server" } }') } "SQL server name 'Sql_Server'" 'an invalid SQL server name is named'
    Assert-Equal @() @(Get-NameProblems $batNames) 'the bat names pass every rule'

    # ---------------------------------------------------------------------------------------------
    Write-Host '== uses and isolation'
    Assert-Equal @() @(foreach ($name in $configNames) { Get-UsesProblems $configs[$name] $allConfigs }) 'the shipped configurations have no uses problem'
    Assert-Equal @() @(Get-IsolationProblems $allConfigs) 'the shipped configurations are isolated'
    $usesProblem = { param($Config) (@(Get-UsesProblems $Config $allConfigs) -join ' ') }
    Assert-True ((& $usesProblem ([pscustomobject]@{ environment = 'qa'; uses = 'nope' })) -match "'qa' uses 'nope', an unknown target") 'an unknown uses target is named'
    Assert-True ((& $usesProblem ([pscustomobject]@{ environment = 'qa'; uses = 'qa' })) -match "'qa' uses itself") 'a configuration using itself is named'
    Assert-True ((& $usesProblem ([pscustomobject]@{ environment = 'qa'; uses = 'test' })) -match "'qa' uses 'test', which itself uses 'bat'; chains are not allowed") 'a chain is named'
    Assert-True ((& $usesProblem (Get-DeployConfig 'prd' $ConfigDirectory $null '{ "uses": "bat" }')) -match "production configuration 'prd' uses the non-production configuration 'bat'") 'prd using bat is named'
    Assert-True ((& $usesProblem (Get-DeployConfig 'test' $ConfigDirectory $null '{ "names": { "sqlServer": "sql-other" } }')) -match "'test' sets names.sqlServer, which it inherits from 'bat'") 'test overriding a locked name is named'
    Assert-True ((& $usesProblem (Get-DeployConfig 'test' $ConfigDirectory $null '{ "names": { "appServicePlan": "asp-eim-wtc-test" } }')) -match "'test' sets names.appServicePlan, which it inherits from 'bat'") 'the App Service plan stays locked'
    Assert-Equal '' (& $usesProblem (Get-DeployConfig 'test' $ConfigDirectory $null '{ "names": { "automationApp": "azf-other" } }')) 'test may override a name it owns'
    Assert-Equal 10 $LockedNameKeys.Count 'ten locked name keys'
    foreach ($key in $LockedNameKeys) {
        $locked = [pscustomobject]@{ environment = 'test'; uses = 'bat'; names = [pscustomobject]@{ $key = 'x-other' } }
        Assert-True ((& $usesProblem $locked) -match "'test' sets names\.$key, which it inherits from 'bat'") "a locked $key is named"
    }
    Assert-Equal @('keyVault', 'logAnalytics') $OverridableNameKeys 'the overridable name keys'
    foreach ($key in $OverridableNameKeys) {
        $overridden = [pscustomobject]@{ environment = 'test'; uses = 'bat'; names = [pscustomobject]@{ $key = 'x-other' } }
        Assert-Equal '' (& $usesProblem $overridden) "an overridable $key gives no uses problem"
    }
    $previousKeys = @('resourceGroup', 'serviceBusNamespace', 'keyVault', 'sqlServer', 'sqlDatabase', 'appServicePlan', 'logAnalytics',
        'gatewayApp', 'gatewayStorage', 'gatewayInsights', 'dashboardApi', 'staticWebApp')
    Assert-Equal @($previousKeys | Sort-Object) @(@($LockedNameKeys) + @($OverridableNameKeys) | Sort-Object) 'the locked and overridable keys are the twelve inherited keys'
    Assert-Equal @() @($LockedNameKeys | Where-Object { $OverridableNameKeys -contains $_ }) 'no key is both locked and overridable'

    $sharedSql = Get-AllDeployConfigs (Get-DeployConfig 'prd' $ConfigDirectory $null '{ "names": { "sqlServer": "azql-wtc-sandbox" } }') $ConfigDirectory
    Assert-True ((@(Get-IsolationProblems $sharedSql) -join ' ') -match "production configuration 'prd' and non-production configuration 'bat' both use the name 'azql-wtc-sandbox'") 'prd sharing the non-production SQL server is named'
    $upperSql = Get-AllDeployConfigs (Get-DeployConfig 'prd' $ConfigDirectory $null '{ "names": { "sqlServer": "AZQL-WTC-SANDBOX" } }') $ConfigDirectory
    Assert-True (@(Get-IsolationProblems $upperSql).Count -gt 0) 'the name comparison ignores case'
    $withUat = Get-AllDeployConfigs $null $ConfigDirectory
    $withUat['uat'] = Get-DeployConfig 'uat' $fixtureDir
    Assert-True ((@(Get-IsolationProblems $withUat) -join ' ') -match "production configuration 'prd' and non-production configuration 'uat' both use the name 'd365fsc-integration-prod'") 'a non-production uat that uses prd is caught'
    $prdUsesBat = Get-AllDeployConfigs (Get-DeployConfig 'prd' $ConfigDirectory $null '{ "uses": "bat" }') $ConfigDirectory
    Assert-True ((@(Get-IsolationProblems $prdUsesBat) -join ' ') -match "production configuration 'prd' uses the non-production configuration 'bat'") 'isolation also names prd using bat'
    $samePrefix = Get-AllDeployConfigs (Get-DeployConfig 'prd' $ConfigDirectory $null '{ "names": { "queuePrefix": "bat" } }') $ConfigDirectory
    Assert-Equal @() @(Get-IsolationProblems $samePrefix) 'the queue prefix is not a resource name'
    $ownedPrdVault = Get-AllDeployConfigs $null $ConfigDirectory
    $ownedPrdVault['test'] = Get-DeployConfig 'test' $ConfigDirectory $null '{ "names": { "keyVault": "kv-eim-wtc-prd" } }' 6>$null
    Assert-True ((@(Get-IsolationProblems $ownedPrdVault) -join ' ') -match "production configuration 'prd' and non-production configuration 'test' both use the name 'kv-eim-wtc-prd'") 'an owned vault named like the production vault is caught'

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
    Write-Host '== components and order'
    $deployAst = [System.Management.Automation.Language.Parser]::ParseFile($deployScriptPath, [ref]$null, [ref]$null)
    $componentParam = $deployAst.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'Component' }
    $validateSet = @($componentParam.Attributes | Where-Object { $_.TypeName.Name -eq 'ValidateSet' } | ForEach-Object { $_.PositionalArguments.Value })
    Assert-Equal (@($ComponentOrder) + 'all') $validateSet 'the -Component ValidateSet is the component order plus all'
    Assert-Equal @('preflight', 'infra', 'sql', 'settings', 'myip', 'schema', 'sqlusers', 'myipremove', 'gateway', 'backend', 'frontend', 'automation', 'verify', 'health') $ComponentOrder 'the component order'
    Assert-Equal @('preflight', 'infra', 'sql', 'settings', 'schema', 'sqlusers', 'gateway', 'backend', 'frontend', 'automation', 'verify', 'health') (Resolve-Components @('all') $true 'bat') "'all' for bat: every component except myip and myipremove"
    Assert-Equal @('preflight', 'infra', 'settings', 'sqlusers', 'automation', 'verify', 'health') (Resolve-Components @('all') $false 'bat') "'all' for test: the components a configuration with uses may run"
    foreach ($ownerOnly in 'schema', 'sql', 'gateway', 'backend', 'frontend') {
        Assert-Throws { Resolve-Components @($ownerOnly) $false 'bat' } "'$ownerOnly' belongs to the owner configuration 'bat'.*-Environment bat" "$ownerOnly for test is refused, naming bat"
    }
    Assert-Equal @('preflight', 'gateway') (Resolve-Components @('gateway') $true 'bat') 'preflight is added before a component that changes something'
    Assert-Equal @('preflight', 'infra', 'sql') (Resolve-Components @('sql', 'infra') $true 'bat') 'components run in the fixed order, whatever the requested order'
    Assert-Equal @('myipremove') (Resolve-Components @('myipremove') $false 'bat') 'myipremove alone runs without preflight'
    Assert-Equal @('verify', 'health') (Resolve-Components @('health', 'verify') $false 'bat') 'read-only components run without preflight'
    Assert-Equal @('preflight', 'myip', 'sqlusers', 'myipremove') (Resolve-Components @('myip', 'sqlusers', 'myipremove') $false 'bat') 'the database step for a configuration with uses'
    Assert-Equal @('preflight', 'infra', 'sql', 'settings', 'myip', 'schema', 'sqlusers', 'myipremove', 'gateway', 'backend', 'frontend', 'automation', 'verify', 'health') (Resolve-Components @('all', 'myip', 'myipremove') $true 'bat') "'all' with the firewall components"
    Assert-Throws { Resolve-Components @('nope') $true 'bat' } "Unknown component 'nope'" 'an unknown component is refused'
    Assert-Throws { Resolve-Components @() $true 'bat' } '-Component is required' 'a component is required'
    Assert-Equal @('dbmigrator/ARM.Integration.DbMigrator.exe', 'gateway.zip', 'dashboard-frontend.bat.zip') (Get-RequiredArtifacts @('schema', 'gateway', 'frontend') $configs.bat) 'artifacts per component; the SPA per configuration'

    # ---------------------------------------------------------------------------------------------
    Write-Host '== placeholders and offline preflight'
    $prdSettings = @(Get-PlaceholderProblems $configs.prd @('preflight', 'settings') $true) -join ' '
    foreach ($path in 'subscriptionId', 'entra.tenantId', 'entra.clientId', 'entra.audience', 'automation.excludedUsers') {
        Assert-True ($prdSettings -match [regex]::Escape("'$path'")) "an owner's settings need $path"
    }
    Assert-True ($prdSettings -notmatch 'sql\.entraAdmin') 'the SQL admin placeholders are not needed for settings'
    $testPlaceholderConfig = & $testWith '{ "entra": { "tenantId": "<tenant-id>", "clientId": "<client-id>", "audience": "<audience>" }, "automation": { "excludedUsers": "<excluded-users>" } }'
    $testSettings = @(Get-PlaceholderProblems $testPlaceholderConfig @('preflight', 'settings') $false) -join ' '
    Assert-True ($testSettings -notmatch 'entra\.') 'a configuration with uses writes no dashboard settings, so needs no entra values'
    Assert-True ($testSettings -match "'automation.excludedUsers'") 'a configuration with uses still needs its Automation values'
    $testPopulatedConfig = & $testWith '{ "automation": { "excludedUsers": "00000000-0000-0000-0000-000000000001;00000000-0000-0000-0000-000000000002" } }'
    $testPopulatedSettings = @(Get-PlaceholderProblems $testPopulatedConfig @('preflight', 'settings') $false) -join ' '
    Assert-True ($testPopulatedSettings -notmatch "'automation.excludedUsers'") 'a configuration with uses accepts populated semicolon-separated Automation excluded users'
    $offline = @(Get-OfflinePreflightProblems $configs.prd @('preflight', 'settings', 'frontend') $allConfigs $true) -join ' '
    Assert-True ($offline -match "'entra.tenantId' is still a placeholder") 'offline preflight reports placeholders'
    Assert-True ($offline -match 'no package at') 'offline preflight reports a missing package for code components'
    $badTriggers = Get-DeployConfig 'bat' $ConfigDirectory $null '{ "automation": { "triggersEnabled": "yes" } }'
    Assert-True ((@(Get-OfflinePreflightProblems $badTriggers @('preflight', 'settings') $allConfigs $true) -join ' ') -match "automation.triggersEnabled is 'yes'") 'offline preflight checks the trigger configuration when settings runs'
    $badIsolation = Get-AllDeployConfigs (Get-DeployConfig 'prd' $ConfigDirectory $null '{ "names": { "keyVault": "kv-d365fsc-wtc-nonprod" } }') $ConfigDirectory
    Assert-True ((@(Get-OfflinePreflightProblems $configs.bat @('preflight', 'verify') $badIsolation $true) -join ' ') -match "both use the name 'kv-d365fsc-wtc-nonprod'") 'offline preflight checks isolation'
    $ownedWorkspacePaths = @(Get-RequiredConfigPaths @('infra') $false @('logAnalytics'))
    Assert-True ($ownedWorkspacePaths -contains 'logAnalytics' -and $ownedWorkspacePaths -notcontains 'appServicePlan') 'infra for a configuration that owns its workspace needs logAnalytics, not appServicePlan'
    $ownedVaultPaths = @(Get-RequiredConfigPaths @('infra') $false @('keyVault'))
    Assert-True ($ownedVaultPaths -notcontains 'logAnalytics' -and $ownedVaultPaths -notcontains 'appServicePlan') 'infra for a configuration that owns its vault needs neither logAnalytics nor appServicePlan'
    $inheritedPaths = @(Get-RequiredConfigPaths @('infra') $false @())
    Assert-True ($inheritedPaths -notcontains 'logAnalytics' -and $inheritedPaths -notcontains 'appServicePlan') 'infra for a configuration that owns nothing needs neither logAnalytics nor appServicePlan'
    $placeholderRetention = Get-DeployConfig 'test' $ConfigDirectory $null '{ "names": { "logAnalytics": "log-eim-wtc-test" }, "logAnalytics": { "retentionDays": "<days>" } }' 6>$null
    Assert-True ((@(Get-PlaceholderProblems $placeholderRetention @('preflight', 'infra') $false @('logAnalytics')) -join ' ') -match "'logAnalytics.retentionDays' is still a placeholder") 'a placeholder retention fails infra when the workspace is owned'
    Assert-True ((@(Get-PlaceholderProblems $placeholderRetention @('preflight', 'infra') $false @()) -join ' ') -notmatch 'logAnalytics') 'a placeholder retention is not needed when the workspace is inherited'

    # ---------------------------------------------------------------------------------------------
    Write-Host '== infra plan'
    $tags = Get-ResourceTags $mck
    Assert-Equal @('eim-workload', 'eim-client', 'eim-environment') @($tags.Keys) 'the resource tags'
    Assert-Equal @('eim', 'wtc', 'prdmck') @($tags.Values) 'the tag values'
    Assert-True (Test-ManagedResource @{ 'eim-environment' = 'bat' }) 'a resource with eim-environment is managed'
    Assert-True (Test-ManagedResource ([pscustomobject]@{ 'EIM-Environment' = 'bat' })) 'the tag name is matched case-insensitively'
    Assert-True (-not (Test-ManagedResource $null)) 'an untagged resource is not managed'
    Assert-True (-not (Test-ManagedResource @{ owner = 'someone' })) 'other tags do not make a resource managed'

    $nothing = @{ Queues = @{}; Managed = @{} }
    $mckPlan = @(Get-InfraPlan $mck $mckNames $nothing $true)
    $mckQueues = @((Get-ServiceBusQueues $mck 'prdmck').Name | ForEach-Object { "create queue $_" })
    $expectedCreates = @(
        'create resource group rg-eim-wtc-prdmck'
        'create Service Bus namespace sbns-eim-wtc-prdmck (Standard)'
        'create Key Vault kv-eim-wtc-prdmck (access policies)'
        'create App Service plan asp-eim-wtc-prdmck (P0V3, Linux)'
        'create Log Analytics workspace log-eim-wtc-prdmck (30 days)'
    ) + $mckQueues + @(
        'create storage stwtceimgwprdmck'
        'create Application Insights appins-eim-gateway-wtc-prdmck (workspace-based, log-eim-wtc-prdmck)'
        'create Function App azf-eim-gateway-wtc-prdmck on asp-eim-wtc-prdmck'
        'create dashboard API app-eim-dashboard-api-wtc-prdmck on asp-eim-wtc-prdmck'
        'create static web app swa-eim-dashboard-wtc-prdmck (Standard)'
        'create storage stwtceimautoprdmck'
        'create Application Insights appins-eim-automation-wtc-prdmck (workspace-based, log-eim-wtc-prdmck)'
        'create Function App azf-eim-automation-wtc-prdmck on asp-eim-wtc-prdmck'
    )
    Assert-Equal $expectedCreates @($mckPlan | Where-Object Label -like 'create *' | ForEach-Object Label) 'an owner with nothing existing creates every resource, in order'
    $vaultStep = @($mckPlan | Where-Object Label -like 'create Key Vault *')[0]
    Assert-Equal 'keyvault' $vaultStep.Kind 'the Key Vault step can recover a soft-deleted vault'
    Assert-True (($vaultStep.Args -join ' ') -match '--enable-rbac-authorization false') 'the Key Vault uses access policies'
    $sbStep = @($mckPlan | Where-Object Label -like 'create Service Bus *')[0]
    Assert-True (($sbStep.Args -join ' ') -match '--sku Standard --min-tls 1\.2') 'the Service Bus namespace takes serviceBus.sku and TLS 1.2'
    Assert-Equal 30 (@($mckPlan | Where-Object Label -like 'create Log Analytics *')[0].Body.properties.retentionInDays) 'Log Analytics takes logAnalytics.retentionDays'
    Assert-Equal 90 (@(Get-InfraPlan $configs.prd $prdNames $nothing $true | Where-Object Label -like 'create Log Analytics *')[0].Body.properties.retentionInDays) 'prd keeps 90 days of logs'
    $apiCreate = @($mckPlan | Where-Object Label -like 'create dashboard API *')[0]
    Assert-True (($apiCreate.Args -join ' ') -match '--runtime DOTNETCORE:10\.0') 'the dashboard API runs .NET 10'
    foreach ($label in 'app-eim-dashboard-api-wtc-prdmck: Always On, FTPS disabled, TLS 1.2', 'app-eim-dashboard-api-wtc-prdmck: HTTPS only', 'azf-eim-gateway-wtc-prdmck: Always On, FTPS disabled, TLS 1.2', 'azf-eim-automation-wtc-prdmck: HTTPS only') {
        Assert-True (@($mckPlan | Where-Object Label -eq $label).Count -eq 1) "a new app gets its baseline: $label"
    }
    foreach ($step in @($mckPlan | Where-Object { $_.Args -contains '--tags' })) {
        $tagValues = @($step.Args | Where-Object { $_ -like 'eim-*=*' })
        Assert-Equal @('eim-workload=eim', 'eim-client=wtc', 'eim-environment=prdmck') $tagValues "$($step.Label) is tagged with the three eim tags only"
    }
    $identities = @($mckPlan | Where-Object { $_.Args[1] -eq 'identity' -and $_.Args[2] -eq 'assign' } | ForEach-Object { "$($_.Args[0]) $($_.Args[6])" })
    Assert-Equal @('functionapp azf-eim-gateway-wtc-prdmck', 'webapp app-eim-dashboard-api-wtc-prdmck', 'functionapp azf-eim-automation-wtc-prdmck') $identities 'an owner assigns an identity to each of its three apps'
    $policies = @($mckPlan | Where-Object Kind -eq 'policy')
    Assert-Equal @('azf-eim-gateway-wtc-prdmck', 'app-eim-dashboard-api-wtc-prdmck', 'azf-eim-automation-wtc-prdmck') @($policies | ForEach-Object { $_.Body.App }) 'each app gets a Key Vault access policy'
    foreach ($policy in $policies) {
        Assert-True (($policy.Args -join ' ') -match '^keyvault set-policy -g rg-eim-wtc-prdmck -n kv-eim-wtc-prdmck --object-id \{principalId\} --secret-permissions get -o none$') "the policy for $($policy.Body.App) grants secret get only"
    }
    # set-policy replaces an identity's secret permissions, so the step sends the union with what it already holds.
    Assert-Equal @('get') (Get-SecretPermissionUnion @()) 'an identity without a policy gets secret get'
    Assert-Equal @('list', 'set', 'get') (Get-SecretPermissionUnion @('List', 'set')) 'existing secret permissions are kept and get is added'
    Assert-True ($null -eq (Get-SecretPermissionUnion @('list', 'Get'))) 'an identity that already has get is left as it is'
    Assert-True ($null -eq (Get-SecretPermissionUnion @('all'))) 'an identity with all secret permissions is left as it is'
    Assert-Equal 'keyvault set-policy -g rg-eim-wtc-prdmck -n kv-eim-wtc-prdmck --object-id 00000000-0000-0000-0000-00000000000a --secret-permissions list set get -o none' `
        ((Resolve-PolicyStepArgs $policies[0].Args '00000000-0000-0000-0000-00000000000a' @('list', 'set', 'get')) -join ' ') 'the policy step carries the principal id and the whole permission union'

    $everything = @{ Queues = @{}; Managed = @{} }
    foreach ($key in 'ResourceGroup', 'ServiceBusNamespace', 'KeyVault', 'Plan', 'LogAnalytics', 'GatewayStorage', 'GatewayInsights', 'GatewayApp', 'DashboardApi', 'StaticWebApp', 'AutomationStorage', 'AutomationInsights', 'AutomationApp') {
        $everything[$key] = $true
        $everything.Managed[$key] = $true
    }
    foreach ($queue in (Get-ServiceBusQueues $mck 'prdmck').Name) { $everything.Queues[$queue] = $true }
    $secondPlan = @(Get-InfraPlan $mck $mckNames $everything $true)
    Assert-Equal @() @($secondPlan | Where-Object Label -like 'create *' | ForEach-Object Label) 'a second plan with everything existing creates nothing'
    Assert-True (@($secondPlan | Where-Object Label -eq 'azf-eim-automation-wtc-prdmck: HTTPS only').Count -eq 1) 'a managed app keeps its baseline converged'
    Assert-Equal 3 @($secondPlan | Where-Object Kind -eq 'policy').Count 'identities and policies are converged on every run'

    # bat: the kept resources exist without eim tags and are used as found.
    $batExisting = @{ ResourceGroup = $true; ServiceBusNamespace = $true; KeyVault = $true; DashboardApi = $true; StaticWebApp = $true; Queues = @{}; Managed = @{} }
    foreach ($queue in (Get-ServiceBusQueues $configs.bat 'bat').Name) { $batExisting.Queues[$queue] = $true }
    $batPlan = @(Get-InfraPlan $configs.bat $batNames $batExisting $true)
    $kept = @('asb-eim-wtc-nonprod', 'kv-d365fsc-wtc-nonprod', 'wtc-dashboard-api-dev', 'wtc-dashboard-frontend-dev')
    $touchingKept = @($batPlan | Where-Object { $stepArgs = $_.Args; @($kept | Where-Object { $stepArgs -contains $_ }).Count -gt 0 })
    $notAccess = @($touchingKept | Where-Object { -not ($_.Kind -eq 'policy' -or ($_.Args[1] -eq 'identity' -and $_.Args[2] -eq 'assign')) } | ForEach-Object Label)
    Assert-Equal @() $notAccess 'bat issues no create, update, tag or site-config step for the kept resources'
    Assert-Equal @('webapp identity assign -g d365fsc-integration-nonprod -n wtc-dashboard-api-dev -o none') @($touchingKept | Where-Object Kind -ne 'policy' | ForEach-Object { $_.Args -join ' ' }) 'the kept dashboard API only gets its identity'
    Assert-Equal 3 @($batPlan | Where-Object Kind -eq 'policy').Count 'bat sets a vault policy for each of its three apps'
    Assert-True (@($batPlan | Where-Object Label -eq 'create resource group d365fsc-integration-nonprod').Count -eq 0) 'the kept resource group is not created'
    foreach ($label in 'create App Service plan asp-eim-wtc-bat (P0V3, Linux)', 'create Log Analytics workspace log-eim-wtc-bat (30 days)', 'create Function App azf-eim-gateway-wtc-bat on asp-eim-wtc-bat', 'create Function App azf-eim-automation-wtc-bat on asp-eim-wtc-bat') {
        Assert-True (@($batPlan | Where-Object Label -eq $label).Count -eq 1) "bat plans: $label"
    }
    Assert-Equal @() @(Get-FunctionAppSteps $configs.bat $batNames 'azf-kept' 'stkept' $true @() $false) 'an existing app without the eim-environment tag gets no baseline'

    # test: only its queues and Automation resources, on bat's plan and vault.
    $testPlan = @(Get-InfraPlan $configs.test $testNames @{ ResourceGroup = $true; ServiceBusNamespace = $true; Queues = @{}; Managed = @{} } $false)
    $expectedTestCreates = @((Get-ServiceBusQueues $configs.test 'test').Name | ForEach-Object { "create queue $_" }) + @(
        'create storage stwtceimautotest'
        'create Application Insights appins-eim-automation-wtc-test (workspace-based, log-eim-wtc-bat)'
        'create Function App azf-eim-automation-wtc-test on asp-eim-wtc-bat'
    )
    Assert-Equal $expectedTestCreates @($testPlan | Where-Object Label -like 'create *' | ForEach-Object Label) 'test plans only its queues and Automation resources'
    Assert-Equal @() @($testPlan | Where-Object { $_.Label -notmatch 'test' } | ForEach-Object Label) 'every test step concerns a test resource'
    Assert-Equal @('azf-eim-automation-wtc-test') @($testPlan | Where-Object Kind -eq 'policy' | ForEach-Object { $_.Body.App }) 'test grants vault access to its Automation app only'

    # test owning its vault or workspace: created in bat's group with test's tags, before the queues; never a plan.
    $testFoundationExisting = { @{ ResourceGroup = $true; ServiceBusNamespace = $true; Queues = @{}; Managed = @{} } }
    $ownVaultPlan = @(Get-InfraPlan $ownVaultConfig $ownVaultNames (& $testFoundationExisting) $false @('keyVault'))
    Assert-Equal (@('create Key Vault kv-eim-wtc-test (access policies)') + $expectedTestCreates) @($ownVaultPlan | Where-Object Label -like 'create *' | ForEach-Object Label) 'test with its own vault creates the vault, then its queues and Automation resources on the bat plan'
    $ownVaultStep = @($ownVaultPlan | Where-Object Label -like 'create Key Vault *')[0]
    Assert-Equal 'keyvault' $ownVaultStep.Kind 'the owned vault step can recover a soft-deleted vault'
    $ownVaultArgs = $ownVaultStep.Args -join ' '
    Assert-True ($ownVaultArgs -match '-g d365fsc-integration-nonprod -n kv-eim-wtc-test ' -and $ownVaultArgs -match '--enable-rbac-authorization false' -and $ownVaultArgs -match 'eim-environment=test') "the owned vault uses access policies, bat's group and test's tag ($ownVaultArgs)"
    Assert-Equal @('keyvault set-policy -g d365fsc-integration-nonprod -n kv-eim-wtc-test --object-id {principalId} --secret-permissions get -o none') @($ownVaultPlan | Where-Object Kind -eq 'policy' | ForEach-Object { $_.Args -join ' ' }) 'the only policy is on the owned vault'
    Assert-Equal @('azf-eim-automation-wtc-test') @($ownVaultPlan | Where-Object Kind -eq 'policy' | ForEach-Object { $_.Body.App }) 'the owned vault policy is for the test Automation app'
    Assert-Equal @() @($ownVaultPlan | Where-Object { $_.Label -match 'kv-d365fsc-wtc-nonprod' -or $_.Args -contains 'kv-d365fsc-wtc-nonprod' } | ForEach-Object Label) 'no step names the bat vault'
    $ownWorkspacePlan = @(Get-InfraPlan $ownWorkspaceConfig $ownWorkspaceNames (& $testFoundationExisting) $false @('logAnalytics'))
    $ownWorkspaceCreates = @($ownWorkspacePlan | Where-Object Label -like 'create *' | ForEach-Object Label)
    Assert-Equal 'create Log Analytics workspace log-eim-wtc-test (30 days)' $ownWorkspaceCreates[0] 'test with its own workspace creates it first, with its own retention'
    Assert-True ($ownWorkspaceCreates -contains 'create Application Insights appins-eim-automation-wtc-test (workspace-based, log-eim-wtc-test)') 'the Automation Insights goes in the owned workspace'
    Assert-True ($ownWorkspaceCreates -contains 'create Function App azf-eim-automation-wtc-test on asp-eim-wtc-bat') 'the Automation app stays on the bat plan'
    Assert-True ((@($ownWorkspacePlan | Where-Object Label -like 'create Log Analytics *')[0].Args -join ' ') -match '-g d365fsc-integration-nonprod -n log-eim-wtc-test ') 'the owned workspace goes in the bat group'
    Assert-Equal 'test' (@($ownWorkspacePlan | Where-Object Label -like 'create Log Analytics *')[0].Body.tags['eim-environment']) 'the owned workspace is tagged eim-environment=test'
    foreach ($owned in @('keyVault'), @('logAnalytics'), @('keyVault', 'logAnalytics'), @('appServicePlan')) {
        $plan = @(Get-InfraPlan $ownVaultConfig $ownVaultNames (& $testFoundationExisting) $false $owned)
        Assert-Equal @() @($plan | Where-Object Label -like 'create App Service plan *' | ForEach-Object Label) "a configuration with uses owning '$($owned -join ', ')' plans no App Service plan"
    }
    $ownedExisting = @{ Queues = @{}; Managed = @{} }
    foreach ($key in 'ResourceGroup', 'ServiceBusNamespace', 'KeyVault', 'LogAnalytics', 'AutomationStorage', 'AutomationInsights', 'AutomationApp') { $ownedExisting[$key] = $true; $ownedExisting.Managed[$key] = $true }
    foreach ($queue in (Get-ServiceBusQueues $configs.test 'test').Name) { $ownedExisting.Queues[$queue] = $true }
    Assert-Equal @() @(Get-InfraPlan $ownVaultConfig $ownVaultNames $ownedExisting $false @('keyVault', 'logAnalytics') | Where-Object Label -like 'create *' | ForEach-Object Label) 'a second run with the owned vault and workspace existing creates nothing'
    # An owned vault made by hand (no eim-environment tag): used as found, no create step, and the policy still converges.
    $unmanagedVault = & $testFoundationExisting
    $unmanagedVault.KeyVault = $true
    $unmanagedVault.Managed.KeyVault = $false
    $unmanagedVaultPlan = @(Get-InfraPlan $ownVaultConfig $ownVaultNames $unmanagedVault $false @('keyVault'))
    Assert-Equal $expectedTestCreates @($unmanagedVaultPlan | Where-Object Label -like 'create *' | ForEach-Object Label) 'an existing untagged owned vault is not created again'
    Assert-Equal @('azf-eim-automation-wtc-test') @($unmanagedVaultPlan | Where-Object { $_.Kind -eq 'policy' -and ($_.Args -join ' ') -match '-n kv-eim-wtc-test ' } | ForEach-Object { $_.Body.App }) 'the access policy on an existing untagged owned vault still converges'
    $existingFoundation = @{ ResourceGroup = $true; ServiceBusNamespace = $true; KeyVault = $true; KeyVaultRbac = $false; Plan = $true; LogAnalytics = $true; SqlServer = $true; SqlDatabase = $true }
    Assert-Equal @() @((Get-FoundationProblems $ownVaultNames $existingFoundation $false 'bat' @('keyVault')).Problems) 'an existing untagged owned vault is no preflight problem'
    $equalPlan = @(Get-InfraPlan $equalVaultConfig $equalVaultNames (& $testFoundationExisting) $false @(Get-OwnedNameKeys $equalVaultConfig $configs.bat))
    Assert-Equal @($testPlan | ForEach-Object { "$($_.Kind) $($_.Label) $($_.Args -join ' ')" }) @($equalPlan | ForEach-Object { "$($_.Kind) $($_.Label) $($_.Args -join ' ')" }) 'an override equal to the bat vault plans exactly the shipped test plan'
    foreach ($plan in @{ Name = 'prdmck'; Steps = $mckPlan }, @{ Name = 'bat'; Steps = $batPlan }, @{ Name = 'test'; Steps = $testPlan }, @{ Name = 'test (own vault)'; Steps = $ownVaultPlan }, @{ Name = 'test (own workspace)'; Steps = $ownWorkspacePlan }) {
        Assert-Equal @() @($plan.Steps | Where-Object { $_.Args -contains 'delete' } | ForEach-Object Label) "the $($plan.Name) plan deletes nothing"
    }

    # An existing Automation Insights on another workspace is warned about, not relinked.
    $wrongWorkspace = @(Get-PlacementWarnings $ownWorkspaceNames '/subscriptions/x/resourceGroups/d365fsc-integration-nonprod/providers/Microsoft.OperationalInsights/workspaces/log-eim-wtc-bat')
    Assert-Equal 1 $wrongWorkspace.Count 'an Insights resource on another workspace gives one warning'
    Assert-True ($wrongWorkspace[0] -match 'appins-eim-automation-wtc-test' -and $wrongWorkspace[0] -match "'log-eim-wtc-bat'" -and $wrongWorkspace[0] -match "'log-eim-wtc-test'") "the warning names the Insights resource and both workspaces ($($wrongWorkspace[0]))"
    Assert-Equal @() @(Get-PlacementWarnings $ownWorkspaceNames '/subscriptions/x/resourceGroups/d365fsc-integration-nonprod/providers/Microsoft.OperationalInsights/workspaces/LOG-EIM-WTC-TEST') 'an Insights resource on the resolved workspace (any case) gives no warning'
    Assert-Equal @() @(Get-PlacementWarnings $ownWorkspaceNames '') 'a blank workspace id gives no warning'

    # ---------------------------------------------------------------------------------------------
    Write-Host '== foundation'
    $missingAll = @{ ResourceGroup = $false; ServiceBusNamespace = $false; KeyVault = $false; Plan = $false; LogAnalytics = $false; SqlServer = $false; SqlDatabase = $false; KeyVaultRbac = $false }
    $ownerResult = Get-FoundationProblems $mckNames $missingAll $true 'prdmck'
    Assert-Equal @() @($ownerResult.Problems) 'an owner with nothing existing has no foundation problem'
    Assert-True (($ownerResult.Notes -join ' ') -match "Key Vault 'kv-eim-wtc-prdmck' does not exist; 'infra' will create it") 'an owner is told infra creates its vault'
    Assert-True (($ownerResult.Notes -join ' ') -match "SQL server 'sql-eim-wtc-prdmck' does not exist; 'sql' will create it") 'an owner is told sql creates its server'
    $usingResult = Get-FoundationProblems $testNames @{ ResourceGroup = $true; ServiceBusNamespace = $true; KeyVault = $false; Plan = $true; LogAnalytics = $true; SqlServer = $false; SqlDatabase = $false } $false 'bat'
    Assert-Equal @(
        "Key Vault 'kv-d365fsc-wtc-nonprod' does not exist; 'infra' and 'sql' must run for 'bat' first"
        "SQL server 'azql-wtc-sandbox' does not exist; 'infra' and 'sql' must run for 'bat' first"
        "database 'eimdb' does not exist; 'infra' and 'sql' must run for 'bat' first"
    ) @($usingResult.Problems) 'a configuration with uses fails on each missing owner resource, naming the owner'
    $rbac = Get-FoundationProblems $batNames @{ ResourceGroup = $true; ServiceBusNamespace = $true; KeyVault = $true; KeyVaultRbac = $true; Plan = $true; LogAnalytics = $true; SqlServer = $true; SqlDatabase = $true } $true 'bat'
    Assert-True (($rbac.Problems -join ' ') -match "Key Vault 'kv-d365fsc-wtc-nonprod' uses RBAC authorisation") 'a vault in RBAC mode fails preflight'
    $batFoundation = { @{ ResourceGroup = $true; ServiceBusNamespace = $true; KeyVault = $true; KeyVaultRbac = $false; Plan = $true; LogAnalytics = $true; SqlServer = $true; SqlDatabase = $true } }
    $vaultMissing = & $batFoundation
    $vaultMissing.KeyVault = $false
    $ownVaultResult = Get-FoundationProblems $ownVaultNames $vaultMissing $false 'bat' @('keyVault')
    Assert-Equal @() @($ownVaultResult.Problems) 'a missing owned vault is no problem'
    Assert-Equal @("Key Vault 'kv-eim-wtc-test' does not exist; 'infra' will create it") @($ownVaultResult.Notes) 'a missing owned vault is a note that infra creates it'
    $vaultAndPlanMissing = & $batFoundation
    $vaultAndPlanMissing.KeyVault = $false
    $vaultAndPlanMissing.Plan = $false
    Assert-Equal @("App Service plan 'asp-eim-wtc-bat' does not exist; 'infra' and 'sql' must run for 'bat' first") @((Get-FoundationProblems $ownVaultNames $vaultAndPlanMissing $false 'bat' @('keyVault')).Problems) 'a missing bat plan still fails when the vault is owned, naming the plan and bat'
    $workspaceMissing = & $batFoundation
    $workspaceMissing.LogAnalytics = $false
    $ownWorkspaceResult = Get-FoundationProblems $ownWorkspaceNames $workspaceMissing $false 'bat' @('logAnalytics')
    Assert-Equal @() @($ownWorkspaceResult.Problems) 'a missing owned workspace is no problem'
    Assert-Equal @("Log Analytics workspace 'log-eim-wtc-test' does not exist; 'infra' will create it") @($ownWorkspaceResult.Notes) 'a missing owned workspace is a note'
    $ownedRbac = & $batFoundation
    $ownedRbac.KeyVaultRbac = $true
    Assert-True (((Get-FoundationProblems $ownVaultNames $ownedRbac $false 'bat' @('keyVault')).Problems -join ' ') -match "Key Vault 'kv-eim-wtc-test' uses RBAC authorisation") 'an owned vault in RBAC mode fails preflight, naming it'

    # ---------------------------------------------------------------------------------------------
    Write-Host '== sql plan'
    $noServer = @{ Server = $false; Database = $false; Managed = $false; Rules = @() }
    $rule = { param($Name, $Start, $End) [pscustomobject]@{ name = $Name; startIpAddress = $Start; endIpAddress = $(if ($End) { $End } else { $Start }) } }
    $halfAdmin = Get-DeployConfig 'prdmck' $fixtureDir $null '{ "sql": { "entraAdmin": "sg-fake-sql-admins" } }'
    Assert-Throws { Get-SqlPlan $halfAdmin $mckNames $noServer @() } "sql\.entraAdmin and sql\.entraAdminObjectId must both be filled" 'a missing server with a blank sql.entraAdminObjectId fails naming both keys'
    Assert-Throws { Get-SqlPlan $configs.prd $prdNames $noServer @() } "sql\.entraAdmin and sql\.entraAdminObjectId.*'<production sql entra admin group>'" 'a missing server with placeholder admin values fails'
    Assert-True ((@(Get-SqlAdminProblems $configs.prd $prdNames) -join ' ') -match "SQL server 'sql-eim-wtc-prd' does not exist") 'the admin problem names the server'
    $fullAdmin = Get-DeployConfig 'prdmck' $fixtureDir $null '{ "sql": { "entraAdmin": "sg-fake-sql-admins", "entraAdminObjectId": "00000000-0000-0000-0000-000000000001" } }'
    $newSql = Get-SqlPlan $fullAdmin $mckNames $noServer @('20.1.2.4', '20.1.2.3', '20.1.2.3')
    $serverArgs = $newSql.Steps[0].Args -join ' '
    Assert-True ($serverArgs -match '^sql server create -g rg-eim-wtc-prdmck -n sql-eim-wtc-prdmck -l centralus --enable-ad-only-auth --external-admin-principal-type Group --external-admin-name sg-fake-sql-admins --external-admin-sid 00000000-0000-0000-0000-000000000001 --minimal-tls-version 1\.2 --tags ') "a missing server is created Entra-only with TLS 1.2 ($serverArgs)"
    Assert-True (($newSql.Steps[1].Args -join ' ') -match '^sql db create -g rg-eim-wtc-prdmck -s sql-eim-wtc-prdmck -n sqldb-eim-wtc-prdmck --service-objective S0 --backup-storage-redundancy Local --tags ') 'a missing database is created with sql.sku and sql.backupRedundancy'
    Assert-Equal @('sql-eim-wtc-prdmck: firewall rule app-outbound-20-1-2-3', 'sql-eim-wtc-prdmck: firewall rule app-outbound-20-1-2-4') @($newSql.Steps | Select-Object -Skip 2 | ForEach-Object Label) 'a new server gets one rule per distinct app outbound IP'
    Assert-Equal 'create database sqldb-eim-wtc-prdmck (S0, Local backups)' (Get-SqlPlan $fullAdmin $mckNames @{ Server = $true; Database = $false; Managed = $true; Rules = @() } @('20.1.2.3')).Steps[0].Label 'a missing database on an existing server is created'

    $untagged = @{ Server = $true; Database = $true; Managed = $false; AdminLogin = ''; AdminSid = ''; AdOnly = $false; Rules = @((& $rule 'AllowDev-someone' '20.1.2.3')) }
    $keptSql = Get-SqlPlan $configs.bat $batNames $untagged @('20.1.2.3', '20.1.2.9')
    Assert-Equal @() @($keptSql.Steps) 'an untagged existing server plans no change'
    Assert-True (($keptSql.Warnings -join ' ') -match 'no firewall rule for the app outbound IPs 20\.1\.2\.9;') 'an uncovered app outbound IP is warned about, covered ones are not'
    Assert-True (($keptSql.Warnings -join ' ') -match "'azql-wtc-sandbox' has no Entra admin") 'a server without an Entra admin is warned about'
    Assert-True (($keptSql.Report -join ' ') -match 'not managed by this script; reported only') 'an untagged server is reported only'
    $adminMismatch = Get-SqlPlan $fullAdmin $mckNames @{ Server = $true; Database = $true; Managed = $false; AdminLogin = 'sg-other'; AdminSid = '00000000-0000-0000-0000-000000000002'; AdOnly = $true; Rules = @() } @()
    Assert-True (($adminMismatch.Warnings -join ' ') -match "but sql\.entraAdminObjectId is '00000000-0000-0000-0000-000000000001'") 'a different Entra admin is warned about'
    Assert-True (($adminMismatch.Report -join ' ') -match 'Entra-only authentication: on') 'Entra-only authentication is reported'
    $azureWide = Get-SqlPlan $configs.bat $batNames @{ Server = $true; Database = $true; Managed = $false; AdminLogin = 'sg'; AdminSid = 'x'; Rules = @((& $rule 'AllowAllWindowsAzureIps' '0.0.0.0')) } @('20.1.2.9')
    Assert-Equal @() @($azureWide.Warnings) 'Allow Azure services covers every app outbound IP'

    $tagged = @{ Server = $true; Database = $true; Managed = $true; Rules = @((& $rule 'app-outbound-20-1-2-3' '20.1.2.3'), (& $rule 'app-outbound-9-9-9-9' '9.9.9.9'), (& $rule 'AllowDev-someone' '1.2.3.4')) }
    $managedSql = Get-SqlPlan $fullAdmin $mckNames $tagged @('20.1.2.3', '20.1.2.4')
    Assert-Equal @(
        'sql server firewall-rule create -g rg-eim-wtc-prdmck -s sql-eim-wtc-prdmck -n app-outbound-20-1-2-4 --start-ip-address 20.1.2.4 --end-ip-address 20.1.2.4 -o none'
        'sql server firewall-rule delete -g rg-eim-wtc-prdmck -s sql-eim-wtc-prdmck -n app-outbound-9-9-9-9 -o none'
    ) @($managedSql.Steps | ForEach-Object { $_.Args -join ' ' }) 'a managed server gets the missing app-outbound rules and loses the stale ones; other rules stay'
    $noIps = Get-SqlPlan $fullAdmin $mckNames $tagged @()
    Assert-Equal @() @($noIps.Steps) 'without app outbound IPs a managed server keeps its rules'
    Assert-True (($noIps.Warnings -join ' ') -match 'no app outbound IPs were found') 'missing app outbound IPs are warned about'
    $azureServices = Get-DeployConfig 'prdmck' $fixtureDir $null '{ "sql": { "firewall": "azureServices" } }'
    Assert-Equal @('sql server firewall-rule create -g rg-eim-wtc-prdmck -s sql-eim-wtc-prdmck -n AllowAllWindowsAzureIps --start-ip-address 0.0.0.0 --end-ip-address 0.0.0.0 -o none') @((Get-SqlPlan $azureServices $mckNames @{ Server = $true; Database = $true; Managed = $true; Rules = @() } @('20.1.2.3')).Steps | ForEach-Object { $_.Args -join ' ' }) 'sql.firewall azureServices allows Azure services'
    foreach ($name in 'bat', 'prd') { Assert-Equal @() @(Get-SqlFirewallProblems $configs[$name]) "the shipped $name sql.firewall is a known mode" }
    $firewallTypo = Get-DeployConfig 'prdmck' $fixtureDir $null '{ "sql": { "firewall": "appOutboundIp" } }'
    Assert-Equal @("sql.firewall is 'appOutboundIp'; it must be one of: appOutboundIps, azureServices") @(Get-SqlFirewallProblems $firewallTypo) 'a misspelt sql.firewall is named with the allowed modes'
    Assert-True ((@(Get-OfflinePreflightProblems $firewallTypo @('preflight', 'sql') $allConfigs $true) -join ' ') -match "sql\.firewall is 'appOutboundIp'") 'the offline preflight fails a misspelt sql.firewall when sql runs'
    Assert-True ((@(Get-OfflinePreflightProblems $firewallTypo @('preflight', 'infra') $allConfigs $true) -join ' ') -notmatch 'sql\.firewall') 'the firewall mode is checked only when sql runs'
    Assert-Throws { Get-SqlPlan $firewallTypo $mckNames @{ Server = $true; Database = $true; Managed = $true; Rules = @() } @('20.1.2.3') } "sql\.firewall is 'appOutboundIp'" 'Get-SqlPlan refuses a misspelt sql.firewall instead of planning app-outbound rules'
    Assert-Equal @('20.1.2.9') @(Get-UncoveredIps @('20.1.2.3', '20.1.2.9') @((& $rule 'range' '20.1.2.0' '20.1.2.5'))) 'a range rule covers the addresses inside it'

    # ---------------------------------------------------------------------------------------------
    Write-Host '== database users'
    Assert-Equal @('azf-eim-gateway-wtc-bat functionapp', 'wtc-dashboard-api-dev webapp', 'azf-eim-automation-wtc-bat functionapp') @(Get-SqlUserApps $batNames $true | ForEach-Object { "$($_.App) $($_.Type)" }) 'bat gets database users for its three apps'
    Assert-Equal @('azf-eim-automation-wtc-test functionapp') @(Get-SqlUserApps $testNames $false | ForEach-Object { "$($_.App) $($_.Type)" }) 'test gets a database user for its Automation app only'
    $userSql = Get-DatabaseUserSql
    foreach ($fragment in 'TYPE = E', 'QUOTENAME(@name)', 'CONVERT(varbinary(16), CAST(@clientId AS uniqueidentifier))', 'ALTER ROLE db_datareader ADD MEMBER', 'ALTER ROLE db_datawriter ADD MEMBER', 'GRANT EXECUTE TO') {
        Assert-True ($userSql.Contains($fragment)) "the database user SQL contains '$fragment'"
    }
    Assert-True ($userSql -match "N'mismatch'") 'a user with another SID is reported, not replaced'
    foreach ($text in $userSql, (Get-DatabaseUserReadSql)) {
        Assert-True ($text -notmatch '(?i)\bDROP\b') 'the database user SQL never drops anything'
        Assert-True ($text -notmatch '\+\s*@name\b|@name\s*\+') 'the user name is never concatenated into SQL; it goes through QUOTENAME or a parameter'
    }
    Assert-Equal @('db_datareader', 'db_datawriter') $DatabaseRoles 'the database roles'

    # ---------------------------------------------------------------------------------------------
    Write-Host '== settings'
    $miSql = 'Server=tcp:azql-wtc-sandbox.database.windows.net,1433;Initial Catalog=eimdb;Authentication=Active Directory Managed Identity;Encrypt=True;TrustServerCertificate=False;Connect Timeout=30;'
    $kvReference = '@Microsoft.KeyVault(SecretUri=https://kv-d365fsc-wtc-nonprod.vault.azure.net/secrets/eim-servicebus/)'
    Assert-Equal $miSql (Get-SqlConnectionString $batNames 'Active Directory Managed Identity') 'the managed identity connection string'
    Assert-True ((Get-SqlConnectionString $batNames 'Active Directory Default') -match ';Authentication=Active Directory Default;') 'the migrator connection string'
    $automation = Get-AutomationSettings $configs.bat $batNames '<insights>'
    $gateway = Get-GatewaySettings $configs.bat $batNames '<insights>'
    $dashboard = Get-DashboardApiSettings $configs.bat $batNames (Get-SimulationTargets $configs.bat $allConfigs)
    Assert-Equal $miSql $automation.EIMDB 'Automation EIMDB uses the managed identity'
    Assert-Equal $miSql $gateway.EIMDB 'gateway EIMDB uses the managed identity'
    Assert-Equal $miSql $dashboard.ConnectionStrings__EIMDB 'dashboard ConnectionStrings__EIMDB uses the managed identity'
    Assert-Equal $kvReference $automation.ServiceBus 'Automation ServiceBus is the Key Vault reference'
    Assert-Equal $kvReference $dashboard.Simulation__ServiceBusConnectionString 'the simulation connection is the Key Vault reference'
    foreach ($set in @{ Name = 'Automation'; Settings = $automation }, @{ Name = 'gateway'; Settings = $gateway }, @{ Name = 'dashboard API'; Settings = $dashboard }) {
        Assert-Equal @() @($set.Settings.Keys | Where-Object { "$($set.Settings[$_])" -match '(?i)Password|SharedAccessKey|User ID' }) "$($set.Name) settings hold no credential"
        Assert-Equal @() @($set.Settings.Keys | Where-Object { $_ -like 'Gateway__Applications__*' }) "$($set.Name) settings have no Gateway__Applications__ key"
    }
    Assert-Equal 'bat-d365fsc-dataevent' $automation.D365FscDataEventQueue 'bat Automation reads the bat queues'
    Assert-True (-not $automation.Contains('PollSchedule')) 'retired settings are not written'
    Assert-Equal 'true' $dashboard.Simulation__Enabled 'simulation is on for bat'
    Assert-Equal 'bat-capture-to-d365fsc' $dashboard.Simulation__CaptureToD365Queue 'the simulation publishes to the configured prefix'
    Assert-Equal 'https://azf-eim-gateway-wtc-bat.azurewebsites.net' $dashboard.Gateway__BaseUrl 'the dashboard verifies keys against the bat gateway'
    Assert-Equal @('bat', 'test') @((Get-SimulationTargets $configs.bat $allConfigs).Keys) 'bat may simulate on its own queues and those of test'
    $simulateTest = Get-DeployConfig 'bat' $ConfigDirectory $null '{ "dashboard": { "simulationQueuePrefix": "test" } }'
    Assert-Equal 'test-d365fsc-dataevent' (Get-DashboardApiSettings $simulateTest $batNames (Get-SimulationTargets $simulateTest $allConfigs)).Simulation__D365FscDataEventQueue 'bat may simulate on the test queues'
    $simulatePrd = Get-DeployConfig 'bat' $ConfigDirectory $null '{ "dashboard": { "simulationQueuePrefix": "prd" } }'
    Assert-Throws { Get-DashboardApiSettings $simulatePrd $batNames (Get-SimulationTargets $simulatePrd $allConfigs) } "simulationQueuePrefix is 'prd'.*bat, test only" 'a simulation prefix that is not bat or test fails'
    $prdDashboard = Get-DashboardApiSettings $configs.prd $prdNames (Get-SimulationTargets $configs.prd $allConfigs)
    Assert-Equal 'false' $prdDashboard.Simulation__Enabled 'simulation is off for prd'
    Assert-Equal @() @($prdDashboard.Keys | Where-Object { $_ -like 'Simulation__*' -and $_ -ne 'Simulation__Enabled' }) 'prd writes no other Simulation__ setting'
    Assert-Equal @('Simulation__ServiceBusConnectionString') @(Get-StaleSimulationSettings $prdDashboard @((New-Setting 'Simulation__Enabled' 'true'), (New-Setting 'Simulation__ServiceBusConnectionString' 'x'))) 'the simulation settings go when simulation is off'
    Assert-Equal @('Gateway__Applications__0__Name') @(Get-StaleVerificationTargetSettings $dashboard @((New-Setting 'Gateway__Applications__0__Name' 'x'), (New-Setting 'Other' 'y'))) 'stale Gateway__Applications__ settings are deleted'
    $testAutomation = Get-AutomationSettings $configs.test $testNames '<insights>'
    Assert-Equal $kvReference $testAutomation.ServiceBus 'test reads the Service Bus connection from the bat vault'
    Assert-Equal $miSql $testAutomation.EIMDB 'test uses the bat database through its own identity'
    Assert-Equal 'test-d365fsc-dataevent' $testAutomation.D365FscDataEventQueue 'test reads the test queues'
    Assert-Equal 'test' $testAutomation.DefaultEnvironment 'test DefaultEnvironment'
    Assert-Equal '@Microsoft.KeyVault(SecretUri=https://kv-eim-wtc-prd.vault.azure.net/secrets/eim-servicebus/)' (Get-AutomationSettings $configs.prd $prdNames '<insights>').ServiceBus 'prd reads the prd vault'
    $ownVaultReference = '@Microsoft.KeyVault(SecretUri=https://kv-eim-wtc-test.vault.azure.net/secrets/eim-servicebus/)'
    Assert-Equal $ownVaultReference (Get-AutomationSettings $ownVaultConfig $ownVaultNames '<insights>').ServiceBus 'test with its own vault reads the Service Bus connection from it'
    Assert-Equal $kvReference (Get-DashboardApiSettings $configs.bat $batNames (Get-SimulationTargets $configs.bat $allConfigs)).Simulation__ServiceBusConnectionString 'the bat dashboard API keeps the bat vault'

    # ---------------------------------------------------------------------------------------------
    Write-Host '== gated triggers'
    $gatedFor = { param($Config) Get-GatedSettings @($Config.automation.gatedFunctions) ($Config.automation.triggersEnabled -eq $true) @($Config.automation.keepDisabled) }
    $batGated = & $gatedFor $configs.bat
    Assert-Equal @($gatedSix | ForEach-Object { "AzureWebJobs.$_.Disabled" }) @($batGated.Keys) 'one setting per gated function'
    Assert-Equal @('false', 'false', 'false', 'false', 'false', 'false') @($batGated.Values) 'bat enables all six gated triggers'
    Assert-Equal @('false', 'false', 'false', 'false', 'false', 'false') @((& $gatedFor $configs.test).Values) 'test enables all six gated triggers'
    Assert-Equal @('true', 'true', 'true', 'true', 'true', 'true') @((& $gatedFor $configs.prd).Values) 'prd keeps all six gated triggers disabled'
    $kept = Get-GatedSettings $gatedSix $true @('RetrySweepHandler')
    Assert-Equal 'true' $kept['AzureWebJobs.RetrySweepHandler.Disabled'] 'a keepDisabled function stays disabled'
    Assert-Equal 5 @($kept.Values | Where-Object { $_ -eq 'false' }).Count 'the other functions are enabled'
    Assert-Equal @('true', 'true', 'true', 'true', 'true', 'true') @((Get-GatedSettings $gatedSix $false @()).Values) 'six disabled when the triggers are off'

    foreach ($name in $configNames) { Assert-Equal @() @(Get-TriggerConfigProblems $configs[$name]) "$name has a valid trigger configuration" }
    Assert-True ((@(Get-TriggerConfigProblems (Get-DeployConfig 'bat' $ConfigDirectory $null '{ "automation": { "triggersEnabled": "yes" } }')) -join ' ') -match "automation.triggersEnabled is 'yes'; it must be the JSON value true or false") 'a string trigger switch is refused'
    $noSwitch = Get-DeployConfig 'bat' $ConfigDirectory
    $noSwitch.automation.PSObject.Properties.Remove('triggersEnabled')
    Assert-True ((@(Get-TriggerConfigProblems $noSwitch) -join ' ') -match 'automation.triggersEnabled is missing') 'a missing trigger switch is refused'
    Assert-True ((@(Get-TriggerConfigProblems (Get-DeployConfig 'bat' $ConfigDirectory $null '{ "automation": { "keepDisabled": ["RetrySweepHandlr"] } }')) -join ' ') -match "keepDisabled lists 'RetrySweepHandlr', which is not in automation.gatedFunctions") 'a misspelt keepDisabled function is named'

    # ---------------------------------------------------------------------------------------------
    Write-Host '== drift'
    $expectedAutomation = Get-AutomationSettings $configs.bat $batNames '<insights>'
    foreach ($key in $batGated.Keys) { $expectedAutomation[$key] = $batGated[$key] }
    $currentAutomation = @($expectedAutomation.Keys | ForEach-Object { New-Setting $_ "$($expectedAutomation[$_])" })
    $clean = Get-SettingsDrift 'app' $expectedAutomation $currentAutomation @('PollSchedule')
    Assert-Equal 0 (@($clean.Failures).Count + @($clean.Warnings).Count) 'matching settings drift nowhere'
    $differing = @($currentAutomation | ForEach-Object {
            if ($_.name -eq 'AzureWebJobs.RetrySweepHandler.Disabled') { New-Setting $_.name 'true' }
            elseif ($_.name -eq 'APPLICATIONINSIGHTS_CONNECTION_STRING') { New-Setting $_.name 'InstrumentationKey=other' }
            else { $_ }
        }) + @(New-Setting 'PollSchedule' 'x')
    $drift = Get-SettingsDrift 'app' $expectedAutomation $differing @('PollSchedule')
    Assert-Equal @() @($drift.Failures) 'differing values do not fail'
    Assert-Equal @("app: 'AzureWebJobs.RetrySweepHandler.Disabled' differs from the configuration", "app: retired setting 'PollSchedule' is still present; the next 'settings' run deletes it") @($drift.Warnings) 'a differing gated value warns (the Insights connection is compared by name only)'
    $missingGated = Get-SettingsDrift 'app' $expectedAutomation @($currentAutomation | Where-Object name -ne 'AzureWebJobs.QueueCaptureCustomer.Disabled') @()
    Assert-True ((@($missingGated.Failures) -join ' ') -match 'app: missing AzureWebJobs.QueueCaptureCustomer.Disabled') 'a missing gated setting fails'
    Assert-Equal @('AzureWebJobs.QueueCaptureCustomer.Disabled') @(Get-MissingGatedSettings $gatedSix @($currentAutomation | Where-Object name -ne 'AzureWebJobs.QueueCaptureCustomer.Disabled')) 'the missing gated settings are named'
    Assert-Equal @('PollSchedule') @(Get-RetiredSettingsToDelete @('PollSchedule', 'Gone') $differing) 'only retired settings present on the app are deleted'

    # ---------------------------------------------------------------------------------------------
    Write-Host '== Key Vault references and verify targets'
    $reference = { param($Setting, $Status, $Details) [pscustomobject]@{ name = "site/$Setting"; properties = [pscustomobject]@{ status = $Status; details = $Details } } }
    Assert-Equal @() @(Get-ReferenceProblems 'app' @('ServiceBus', 'EIMDB') @((& $reference 'ServiceBus' 'Resolved'))) 'a resolved reference is no problem'
    Assert-Equal @("app: the Key Vault reference in 'ServiceBus' is 'AccessToKeyVaultDenied': denied") @(Get-ReferenceProblems 'app' @('ServiceBus') @((& $reference 'ServiceBus' 'AccessToKeyVaultDenied' 'denied'))) 'an unresolved reference is named with its status'
    Assert-Equal @("app: the Key Vault reference in 'Simulation__ServiceBusConnectionString' is not reported") @(Get-ReferenceProblems 'app' @('Simulation__ServiceBusConnectionString') @()) 'an unreported reference is a problem'
    Assert-Equal @() @(Get-ReferenceProblems 'app' @('EIMDB') @()) 'settings without a reference are not checked'
    Assert-Equal 'Resolved (kv-eim-wtc-test)' (Format-ReferenceStatus ([pscustomobject]@{ properties = [pscustomobject]@{ status = 'Resolved'; vaultName = 'kv-eim-wtc-test' } })) 'verify shows the vault a reference resolves against'
    Assert-Equal 'Resolved' (Format-ReferenceStatus ([pscustomobject]@{ properties = [pscustomobject]@{ status = 'Resolved'; vaultName = '' } })) 'a reference without a vault name shows its status only'
    Assert-Equal @('azf-eim-gateway-wtc-bat', 'wtc-dashboard-api-dev', 'wtc-dashboard-frontend-dev', 'azf-eim-automation-wtc-bat') @((Get-VerifyTargets $configs.bat $batNames $true).Name) 'verify for bat: the gateway, the dashboard API and SPA, and its Automation app'
    Assert-Equal @('azf-eim-automation-wtc-test') @((Get-VerifyTargets $configs.test $testNames $false).Name) 'verify for test: its Automation app only'
    Assert-Equal @($false, $false, $false, $true) @((Get-VerifyTargets $configs.bat $batNames $true).Gated) 'only the Automation app reports gated triggers'

    # ---------------------------------------------------------------------------------------------
    Write-Host '== script text'
    $deployText = Get-Content $deployScriptPath -Raw
    $mainBlock = $deployText.Substring($deployText.IndexOf("if (`$MyInvocation.InvocationName -ne '.')"))
    Assert-True ($mainBlock.IndexOf('Get-OfflinePreflightProblems $config') -gt 0 -and $mainBlock.IndexOf('Get-OfflinePreflightProblems $config') -lt $mainBlock.IndexOf('Assert-AzureCli $config')) 'the offline preflight runs before the first az call'
    Assert-True ($mainBlock.IndexOf('Get-UsesProblems $config') -lt $mainBlock.IndexOf('Assert-AzureCli $config')) 'the uses check runs before the first az call'
    foreach ($file in $deployScriptPath, $packageScriptPath) {
        $text = Get-Content $file -Raw
        foreach ($variable in ('EIM_DEPLOY_' + 'EIMDB'), ('EIM_DEPLOY_' + 'SERVICEBUS')) {
            Assert-True ($text -notmatch $variable) "$(Split-Path $file -Leaf) reads no $variable"
        }
    }
    Assert-True ($deployText -notmatch "resource delete|'resource',\s*'delete'") 'the script deletes no Azure resource'
    Assert-True ($deployText -notmatch "delete-policy") 'the script removes no Key Vault access policy'
    # Every 'delete' passed to az follows 'appsettings' or 'firewall-rule'.
    $deployTokens = $null
    [System.Management.Automation.Language.Parser]::ParseFile($deployScriptPath, [ref]$deployTokens, [ref]$null) | Out-Null
    $significant = @($deployTokens | Where-Object { $_.Kind -notin 'Comma', 'NewLine', 'Comment', 'LineContinuation' })
    $deleteCalls = for ($i = 1; $i -lt $significant.Count; $i++) {
        if (($significant[$i].Text -replace "^'|'$", '') -eq 'delete') { $significant[$i - 1].Text -replace "^'|'$", '' }
    }
    Assert-Equal @('appsettings', 'firewall-rule', 'firewall-rule') @($deleteCalls | Sort-Object) 'the only az delete calls are app settings and firewall rules'
    # Every Verb-Noun command the script calls is defined in it or available in PowerShell.
    $defined = @($deployAst.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true) | ForEach-Object Name)
    $unresolved = @($deployAst.FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] }, $true) | ForEach-Object { $_.GetCommandName() } |
            Where-Object { $_ -match '^[A-Za-z]+-[A-Za-z]+$' } | Sort-Object -Unique | Where-Object { $defined -notcontains $_ -and -not (Get-Command $_ -ErrorAction SilentlyContinue) })
    Assert-Equal @() $unresolved 'every Verb-Noun command in Deploy-Wilson.ps1 is defined or resolvable'

    # ---------------------------------------------------------------------------------------------
    Write-Host '== components with stubbed Azure calls'
    $savedBuildId = $env:BUILD_BUILDID
    try {
        $env:BUILD_BUILDID = '4711'
        $azCalls = [System.Collections.Generic.List[string]]::new()
        & {
            function Invoke-AzProcess([object[]]$Arguments) { $azCalls.Add(($Arguments -join ' ')); [pscustomobject]@{ ExitCode = 0; StdOut = ''; StdErr = '' } }
            Invoke-MyIpComponent $configs.bat $batNames -Remove 6>$null
        }
        Assert-Equal @('sql server firewall-rule delete -g d365fsc-integration-nonprod -s azql-wtc-sandbox -n eim-deploy-4711 -o none') @($azCalls) 'myipremove deletes the pipeline rule on the bat SQL server'
    }
    finally { $env:BUILD_BUILDID = $savedBuildId }

    $previewLines = & { $Preview = $true; Invoke-SqlUsersComponent $configs.bat $batNames $true } 6>&1 | ForEach-Object { "$_" }
    Assert-Equal @(
        '  would ensure database user [azf-eim-gateway-wtc-bat] in eimdb on azql-wtc-sandbox: db_datareader, db_datawriter, EXECUTE'
        '  would ensure database user [wtc-dashboard-api-dev] in eimdb on azql-wtc-sandbox: db_datareader, db_datawriter, EXECUTE'
        '  would ensure database user [azf-eim-automation-wtc-bat] in eimdb on azql-wtc-sandbox: db_datareader, db_datawriter, EXECUTE'
    ) @($previewLines) 'sqlusers -WhatIf lists the users and roles without connecting'
    $secretLines = & { $Preview = $true; Sync-ServiceBusSecret $batNames } 6>&1 | ForEach-Object { "$_" }
    Assert-True ((@($secretLines) -join ' ') -match "would write Key Vault secret 'eim-servicebus' in 'kv-d365fsc-wtc-nonprod' from asb-eim-wtc-nonprod") 'the Service Bus secret preview makes no az call'

    # infra's policy step: set-policy sends the union with the identity's existing secret permissions, or is skipped.
    $fakePrincipal = '00000000-0000-0000-0000-00000000000b'
    foreach ($case in @{ Existing = @('list', 'set'); Expected = @("functionapp identity assign -g d365fsc-integration-nonprod -n azf-eim-automation-wtc-bat -o none", "keyvault set-policy -g d365fsc-integration-nonprod -n kv-d365fsc-wtc-nonprod --object-id $fakePrincipal --secret-permissions list set get -o none"); Name = 'adds get to the existing secret permissions' },
        @{ Existing = @('get', 'list'); Expected = @('functionapp identity assign -g d365fsc-integration-nonprod -n azf-eim-automation-wtc-bat -o none'); Name = 'is skipped when get is already granted' }) {
        $azCalls = [System.Collections.Generic.List[string]]::new()
        & {
            $Preview = $false
            function Get-ExistingResources($Config, $Names, [bool]$IsOwner, [string[]]$OwnedKeys) { @{ Queues = @{}; Managed = @{} } }
            function Get-InfraPlan { Get-IdentitySteps $batNames 'azf-eim-automation-wtc-bat' 'functionapp' }
            function Get-AppPrincipalId { $fakePrincipal }
            function Get-VaultSecretPermissions { $case.Existing }
            function Invoke-Az { $azCalls.Add(($args -join ' ')) }
            Invoke-InfraComponent $configs.bat $batNames $true 6>$null
        }
        Assert-Equal $case.Expected @($azCalls) "the Key Vault policy step $($case.Name)"
    }

    # A configuration with "uses" that owns its vault writes the secret there and references it.
    $azCalls = [System.Collections.Generic.List[string]]::new()
    $written = @{}
    & {
        $Preview = $false
        function Invoke-Az { $azCalls.Add(($args -join ' ')); if ($args -contains 'keys') { 'fake-namespace-connection' } }
        function Invoke-AzProcess([object[]]$Arguments) { $azCalls.Add(($Arguments -join ' ')); [pscustomobject]@{ ExitCode = 1; StdOut = ''; StdErr = 'SecretNotFound' } }
        function Test-Az { throw 'the owned vault path must not test the bat secret' }
        function Test-WebApp { $true }
        function Get-AppSettingList { @() }
        function Get-InsightsConnection { 'InstrumentationKey=00000000-0000-0000-0000-000000000000' }
        function Update-AppSettings([string]$ResourceGroup, [string]$App, $Settings) { $written[$App] = $Settings }
        Invoke-SettingsComponent $ownVaultConfig $ownVaultNames $allConfigs $false 'bat' @('keyVault') 6>$null
    }
    Assert-True (@($azCalls | Where-Object { $_ -match '^keyvault secret (show|set) --vault-name kv-eim-wtc-test ' }).Count -eq 2) "settings reads and writes eim-servicebus in the owned vault ($($azCalls -join '; '))"
    Assert-True (@($azCalls | Where-Object { $_ -match 'servicebus namespace authorization-rule keys list -g d365fsc-integration-nonprod --namespace-name asb-eim-wtc-nonprod' }).Count -eq 1) 'the secret comes from the bat namespace'
    Assert-Equal @() @($azCalls | Where-Object { $_ -match 'kv-d365fsc-wtc-nonprod' }) 'settings with an owned vault does not touch the bat vault'
    Assert-Equal $ownVaultReference $written['azf-eim-automation-wtc-test'].ServiceBus 'settings writes ServiceBus as a reference to the owned vault'
    Assert-Equal @('azf-eim-automation-wtc-test') @($written.Keys) 'settings for test writes no setting of the bat apps'
    # The deployer cannot write the owned vault: settings fails before any app setting is written.
    $written = @{}
    Assert-Throws {
        & {
            $Preview = $false
            function Invoke-Az { if ($args -contains 'keys') { 'fake-namespace-connection' } }
            function Invoke-AzProcess([object[]]$Arguments) { [pscustomobject]@{ ExitCode = 1; StdOut = ''; StdErr = 'Forbidden' } }
            function Set-VaultSecret { throw 'Forbidden' }
            function Test-WebApp { $true }
            function Get-AppSettingList { @() }
            function Get-InsightsConnection { 'InstrumentationKey=00000000-0000-0000-0000-000000000000' }
            function Update-AppSettings([string]$ResourceGroup, [string]$App, $Settings) { $written[$App] = $Settings }
            Invoke-SettingsComponent $ownVaultConfig $ownVaultNames $allConfigs $false 'bat' @('keyVault') 6>$null
        }
    } "Could not write Key Vault secret 'eim-servicebus' in 'kv-eim-wtc-test'; the deployer needs secret get and set on the vault" 'settings names the owned vault the deployer cannot write'
    Assert-Equal @() @($written.Keys) 'no app setting is written when the owned vault secret cannot be written'
    Assert-Throws {
        & {
            $Preview = $false
            function Test-Az { $false }
            Invoke-SettingsComponent $configs.test $testNames $allConfigs $false 'bat' 6>$null
        }
    } "eim-servicebus' does not exist in 'kv-d365fsc-wtc-nonprod'.*run 'settings' for 'bat' first" 'settings for test without its own vault still needs the bat secret'

    # Preflight with an owned vault does not require the secret; without one it still does.
    $secretChecks = [System.Collections.Generic.List[string]]::new()
    $preflightStubs = {
        param($Names, [string[]]$Owned)
        & {
            function Get-PackageManifest { $null }
            function Get-FoundationState { @{ ResourceGroup = $true; ServiceBusNamespace = $true; KeyVault = $true; KeyVaultRbac = $false; Plan = $true; LogAnalytics = $true; SqlServer = $true; SqlDatabase = $true } }
            function Test-VaultSecret([string]$Vault) { $secretChecks.Add($Vault); $false }
            Invoke-PreflightComponent $configs.test $Names @('preflight', 'infra', 'settings') $false 'bat' $Owned 6>$null
        }
    }
    & $preflightStubs $ownVaultNames @('keyVault')
    Assert-Equal @() @($secretChecks) 'preflight with an owned vault checks no secret, so not the one in kv-d365fsc-wtc-nonprod'
    Assert-Throws { & $preflightStubs $testNames @() } "eim-servicebus' does not exist in 'kv-d365fsc-wtc-nonprod'.*run 'settings' for 'bat' first" 'preflight for the shipped test still needs the bat secret'

    # infra probes the owned vault or workspace, never the plan.
    foreach ($case in @{ Owned = @('keyVault'); Names = $ownVaultNames; Probe = '^keyvault show -g d365fsc-integration-nonprod -n kv-eim-wtc-test$'; Absent = 'Microsoft\.OperationalInsights/workspaces' },
        @{ Owned = @('logAnalytics'); Names = $ownWorkspaceNames; Probe = '^resource show -g d365fsc-integration-nonprod -n log-eim-wtc-test --resource-type Microsoft\.OperationalInsights/workspaces$'; Absent = '^keyvault show' }) {
        $azCalls = [System.Collections.Generic.List[string]]::new()
        $probed = & {
            function Test-Az { $azCalls.Add(($args -join ' ')); $true }
            function Get-AzShow { $azCalls.Add(($args -join ' ')); [pscustomobject]@{ tags = $null } }
            Get-ExistingResources $configs.test $case.Names $false $case.Owned
        }
        Assert-True (@($azCalls | Where-Object { $_ -match $case.Probe }).Count -eq 1) "infra for test owning $($case.Owned) probes it ($($azCalls -join '; '))"
        Assert-Equal @() @($azCalls | Where-Object { $_ -match '^appservice plan show' -or $_ -match $case.Absent }) "infra for test owning $($case.Owned) probes no plan and nothing it does not own"
        Assert-True (-not $probed.ContainsKey('Plan')) "infra for test owning $($case.Owned) reports no plan"
    }

    # An owned workspace and an existing Automation Insights on the bat workspace: a warning, no failure.
    $warnings = [System.Collections.Generic.List[string]]::new()
    & {
        $Preview = $false
        function Get-ExistingResources { @{ ResourceGroup = $true; ServiceBusNamespace = $true; LogAnalytics = $true; AutomationInsights = $true; Queues = @{}; Managed = @{} } }
        function Get-InfraPlan { }
        function Invoke-Az { if ($args -contains 'properties.WorkspaceResourceId') { '/subscriptions/x/resourceGroups/d365fsc-integration-nonprod/providers/Microsoft.OperationalInsights/workspaces/log-eim-wtc-bat' } }
        function Write-DeployWarning([string]$Message) { $warnings.Add($Message) }
        Invoke-InfraComponent $ownWorkspaceConfig $ownWorkspaceNames $false @('logAnalytics') 6>$null
    }
    Assert-Equal 1 $warnings.Count 'infra warns once about the Insights placement'
    Assert-True ($warnings[0] -match 'appins-eim-automation-wtc-test' -and $warnings[0] -match "'log-eim-wtc-bat'" -and $warnings[0] -match "'log-eim-wtc-test'") "the placement warning names the Insights resource and both workspaces ($($warnings[0]))"

    # sqlusers with stubbed identities and database calls; no connection is opened.
    $sqlRow = {
        param($Columns)
        $table = [System.Data.DataTable]::new()
        foreach ($key in $Columns.Keys) { [void]$table.Columns.Add($key) }
        $row = $table.NewRow()
        foreach ($key in $Columns.Keys) { $row[$key] = $Columns[$key] }
        $table.Rows.Add($row)
        @($table.Rows)
    }
    $queries = [System.Collections.Generic.List[string]]::new()
    Assert-Throws {
        & {
            $Preview = $false
            function Get-AppPrincipalId([string]$ResourceGroup, [string]$App) { $fakePrincipal }
            function Get-AppIdentityClientId { '00000000-0000-0000-0000-00000000000c' }
            function Invoke-DeployerSql($Names, [string]$Query, [hashtable]$Parameters) {
                $queries.Add($Query)
                if ($Query -eq (Get-DatabaseUserReadSql)) { return & $sqlRow ([ordered]@{ name = $Parameters['@name']; type_desc = 'EXTERNAL_USER'; roles = 'db_datareader, db_datawriter'; can_execute = 1 }) }
                $outcome = if ($Parameters['@name'] -eq 'azf-eim-automation-wtc-bat') { 'mismatch' } else { 'exists' }
                & $sqlRow ([ordered]@{ outcome = $outcome })
            }
            Invoke-SqlUsersComponent $configs.bat $batNames $true 6>$null
        }
    } "Database user \[azf-eim-automation-wtc-bat\] in 'eimdb' belongs to another identity.*drop it by hand" 'Recreated app: a SID mismatch fails naming the user and the manual drop'
    Assert-Equal @() @($queries | Where-Object { $_ -match '(?i)\bDROP\b' }) 'Recreated app: no user is dropped'

    $sqlCalls = [System.Collections.Generic.List[string]]::new()
    Assert-Throws {
        & {
            $Preview = $false
            function Invoke-Az { '' }
            function Invoke-DeployerSql { $sqlCalls.Add('called') }
            Invoke-SqlUsersComponent $configs.test $testNames $false 6>$null
        }
    } "'azf-eim-automation-wtc-test' has no system-assigned identity; run 'infra' first" 'App without an identity: sqlusers fails naming the app and infra'
    Assert-Equal 0 $sqlCalls.Count 'App without an identity: no database call is made'

    # Sign-in failures. The driver wraps a SqlException (here a stand-in carrying Number) in a MethodInvocationException.
    $fakeToken = 'eyFAKE.selftest.token'
    $sqlFailure = {
        param([int]$Number, [string]$Message)
        $inner = [System.Exception]::new($Message)
        $inner | Add-Member -NotePropertyName Number -NotePropertyValue $Number
        [System.Management.Automation.MethodInvocationException]::new("Exception calling `"Open`" with `"0`" argument(s): `"$Message`"", $inner)
    }
    $loginFailed = & $sqlFailure 18456 "Login failed for user '<token-identified principal>'. token $fakeToken"
    $notAdmin = Get-SqlSignInFailureMessage $batNames 'deployer@example.com' $loginFailed $fakeToken
    Assert-Equal "'deployer@example.com' cannot sign in to SQL server 'azql-wtc-sandbox' (database 'eimdb'): add it to the server's Entra admin group." $notAdmin 'Deployer not an administrator: the message names the server, the database and the identity, and the admin group'
    Assert-Throws {
        & {
            $Preview = $false
            function Get-AppPrincipalId { $fakePrincipal }
            function Get-AppIdentityClientId { '00000000-0000-0000-0000-00000000000c' }
            function Invoke-DeployerSql { throw $notAdmin }
            Invoke-SqlUsersComponent $configs.bat $batNames $true 6>$null
        }
    } "'deployer@example\.com' cannot sign in to SQL server 'azql-wtc-sandbox'.*Entra admin group" 'Deployer not an administrator: sqlusers fails with the admin-group message'
    $blocked = Get-SqlSignInFailureMessage $batNames 'deployer@example.com' (& $sqlFailure 40615 "Cannot open server 'azql-wtc-sandbox' requested by the login. Client with IP address '20.1.2.3' is not allowed to access the server.") $fakeToken
    Assert-True ($blocked -match "blocked by the firewall; run '-Component myip' first" -and $blocked -notmatch 'admin group') 'a firewall block points at myip, not at the admin group'
    $timeout = Get-SqlSignInFailureMessage $batNames 'deployer@example.com' (& $sqlFailure 258 "Connection Timeout Expired. token=$fakeToken") $fakeToken
    Assert-Equal "'deployer@example.com' cannot open a connection to SQL server 'azql-wtc-sandbox' (database 'eimdb') (SQL error 258): Connection Timeout Expired. token=<access token>" $timeout 'any other sign-in failure gives its SQL error number and message, not the admin-group advice'
    foreach ($message in $notAdmin, $blocked, $timeout) {
        Assert-True (-not $message.Contains($fakeToken)) "Database sign-in failure hides the token: $message"
    }
    Assert-True ((Get-SqlSignInFailureMessage $batNames 'deployer@example.com' ([System.InvalidOperationException]::new('The connection was closed.')) $fakeToken) -match '\(error\): The connection was closed\.$') 'a failure without a SQL error number still gives its message'

    # ---------------------------------------------------------------------------------------------
    Write-Host '== service bus queues'
    # A configuration with its serviceBusQueues replaced (lists replace, never merge); $Raw: the list as JSON text.
    $sbqShipped = @(
        [ordered]@{ name = 'd365fsc-dataevent'; setting = 'D365FscDataEventQueue' }
        [ordered]@{ name = 'd365fsc-businessevents'; setting = 'D365BusinessEventQueue' }
        [ordered]@{ name = 'd365fsc-to-capture'; setting = 'D365ToCaptureQueue' }
        [ordered]@{ name = 'capture-to-d365fsc'; setting = 'CaptureToD365Queue' }
        [ordered]@{ name = 'capture-to-d365fsc-response'; setting = 'CaptureToD365QueueResponse' }
    )
    $sbqWithRaw = { param([string]$Name, [string]$Raw, [string]$Directory = $ConfigDirectory) Get-DeployConfig $Name $Directory $null "{ `"serviceBusQueues`": $Raw }" 6>$null }
    $sbqWith = { param([string]$Name, [object[]]$Entries, [string]$Directory = $ConfigDirectory) & $sbqWithRaw $Name (ConvertTo-Json -InputObject @($Entries) -Depth 5 -Compress) $Directory }
    $sbqShippedNames = @('d365fsc-dataevent', 'd365fsc-businessevents', 'd365fsc-to-capture', 'capture-to-d365fsc', 'capture-to-d365fsc-response')
    $sbqShippedSettings = @('D365FscDataEventQueue', 'D365BusinessEventQueue', 'D365ToCaptureQueue', 'CaptureToD365Queue', 'CaptureToD365QueueResponse')

    # 2.1 The shipped list resolves to today's queues, with sessions off and duplicate detection on.
    foreach ($case in @{ Config = $configs.bat; Prefix = 'bat' }, @{ Config = $configs.test; Prefix = 'test' }, @{ Config = $configs.prd; Prefix = 'prd' }) {
        $queues = @(Get-ServiceBusQueues $case.Config $case.Prefix)
        Assert-Equal @($sbqShippedNames | ForEach-Object { "$($case.Prefix)-$_" }) $queues.Name "$($case.Prefix) resolves today's five queue names, in order"
        Assert-Equal @() @($queues | Where-Object { $_.RequiresSession -or -not $_.DuplicateDetection } | ForEach-Object Name) "$($case.Prefix) queues have no sessions and keep duplicate detection"
        Assert-Equal $sbqShippedSettings @((Get-QueueSettings $queues).Keys) "$($case.Prefix) queue settings, in order"
        Assert-Equal @() @(Get-QueueConfigProblems $case.Config $case.Prefix) "the committed $($case.Prefix) serviceBusQueues has no problem"
    }
    $sbqDefaults = @(Get-ServiceBusQueues (& $sbqWith 'bat' (@($sbqShipped) + [ordered]@{ name = 'no-setting' })) 'bat')[-1]
    Assert-True ($null -eq $sbqDefaults.Setting -and $sbqDefaults.RequiresSession -eq $false -and $sbqDefaults.DuplicateDetection -eq $true) 'an entry with only a name: no setting, no sessions, duplicate detection on'

    # 2.2 Queue creation: today's arguments plus duplicate detection; sessions and duplicate detection per entry.
    $sbqPlan = @(Get-InfraPlan $mck $mckNames $nothing $true | Where-Object Label -like 'create queue *')
    Assert-Equal @($sbqShippedNames | ForEach-Object { "create queue prdmck-$_" }) @($sbqPlan.Label) 'prdmck creates its five queues with plain labels'
    foreach ($step in $sbqPlan) {
        $queueName = $step.Label -replace '^create queue ', ''
        $expectedArgs = "servicebus queue create -g $($mckNames.ResourceGroup) --namespace-name $($mckNames.ServiceBusNamespace) -n $queueName " +
        '--max-size 1024 --default-message-time-to-live P14D --lock-duration PT60S --max-delivery-count 10 ' +
        '--enable-dead-lettering-on-message-expiration true --enable-partitioning false ' +
        '--duplicate-detection true --duplicate-detection-history-time-window PT5M -o none'
        Assert-Equal $expectedArgs ($step.Args -join ' ') "$queueName is created with today's arguments plus five minutes of duplicate detection"
    }
    $sbqFlags = & $sbqWith 'prdmck' (@($sbqShipped) + [ordered]@{ name = 'sessions'; requiresSession = $true } + [ordered]@{ name = 'no-dedup'; duplicateDetection = $false } + [ordered]@{ name = 'both'; requiresSession = $true; duplicateDetection = $false }) $fixtureDir
    $sbqFlagPlan = @(Get-InfraPlan $sbqFlags $mckNames $nothing $true | Where-Object Label -like 'create queue *')
    $sessionStep = @($sbqFlagPlan | Where-Object Label -eq 'create queue prdmck-sessions (sessions)')
    Assert-Equal 1 $sessionStep.Count 'a session queue is labelled (sessions)'
    Assert-True (($sessionStep[0].Args -join ' ') -match ' --enable-session true --duplicate-detection true --duplicate-detection-history-time-window PT5M -o none$') 'a session queue adds --enable-session true and keeps duplicate detection'
    $noDedupStep = @($sbqFlagPlan | Where-Object Label -eq 'create queue prdmck-no-dedup (no duplicate detection)')
    Assert-Equal 1 $noDedupStep.Count 'a queue without duplicate detection is labelled (no duplicate detection)'
    Assert-True (($noDedupStep[0].Args -join ' ') -match ' --enable-partitioning false -o none$' -and $noDedupStep[0].Args -notcontains '--duplicate-detection' -and $noDedupStep[0].Args -notcontains '--enable-session') 'duplicateDetection false sends neither duplicate detection argument'
    Assert-Equal 1 @($sbqFlagPlan | Where-Object Label -eq 'create queue prdmck-both (sessions) (no duplicate detection)').Count 'both flags show in the label'
    Assert-Equal @() @($sbqPlan | Where-Object { $_.Args -contains '--enable-session' } | ForEach-Object Label) 'no shipped queue enables sessions'

    $sbqBatExisting = @{ ResourceGroup = $true; ServiceBusNamespace = $true; Queues = @{}; Managed = @{} }
    foreach ($queue in (Get-ServiceBusQueues $configs.bat 'bat').Name) { $sbqBatExisting.Queues[$queue] = $true }
    Assert-Equal @() @(Get-InfraPlan $configs.bat $batNames $sbqBatExisting $true | Where-Object { $_.Label -match 'queue bat-' -or ($_.Args -contains 'queue') } | ForEach-Object Label) 'existing bat queues get no step at all (left as they are)'
    $sbqTestPartial = @(Get-InfraPlan $configs.test $testNames @{ ResourceGroup = $true; ServiceBusNamespace = $true; Queues = @{ 'test-d365fsc-dataevent' = $true }; Managed = @{} } $false | Where-Object Label -like 'create queue *' | ForEach-Object Label)
    Assert-Equal @($sbqShippedNames | Select-Object -Skip 1 | ForEach-Object { "create queue test-$_" }) $sbqTestPartial 'with only test-d365fsc-dataevent existing, test creates the other four'
    $sbqExtraMck = & $sbqWith 'prdmck' (@($sbqShipped) + [ordered]@{ name = 'extra-queue'; setting = 'ExtraQueue' }) $fixtureDir
    Assert-Equal 'create queue prdmck-extra-queue' @(Get-InfraPlan $sbqExtraMck $mckNames $nothing $true | Where-Object Label -like 'create queue *' | ForEach-Object Label)[-1] 'an added entry plans its queue'
    $sbqExtraMckSettings = Get-AutomationSettings (& $sbqWith 'prdmck' (@($sbqShipped) + [ordered]@{ name = 'extra-queue'; setting = 'ExtraQueue' }) $fixtureDir 6>$null) $mckNames 'x'
    Assert-Equal 'prdmck-extra-queue' $sbqExtraMckSettings.ExtraQueue 'an added entry writes ExtraQueue = prdmck-extra-queue'
    Assert-Equal @($sbqShippedNames | ForEach-Object { "prdmck-$_" }) @($sbqShippedSettings | ForEach-Object { $sbqExtraMckSettings[$_] }) 'prdmck keeps its five queue settings beside the added one'
    $sbqNoSettingPlan = @(Get-InfraPlan (& $sbqWith 'prdmck' (@($sbqShipped) + [ordered]@{ name = 'no-setting' }) $fixtureDir) $mckNames $nothing $true | ForEach-Object Label)
    Assert-True ($sbqNoSettingPlan -contains 'create queue prdmck-no-setting') 'a queue without a setting is still created when missing'
    $sbqPrdPlan = @(Get-InfraPlan $configs.prd $prdNames $nothing $true | Where-Object Label -like 'create queue *')
    Assert-Equal @($sbqShippedNames | ForEach-Object { "create queue prd-$_" }) @($sbqPrdPlan.Label) 'prd creates its five queues with plain labels'
    Assert-Equal @() @($sbqPrdPlan | Where-Object { ($_.Args -join ' ') -notmatch ' --duplicate-detection true --duplicate-detection-history-time-window PT5M -o none$' -or $_.Args -contains '--enable-session' } | ForEach-Object Label) 'prd queues get duplicate detection (PT5M) and no sessions, as non-production'

    # infra probes exactly the configured queues, an added one included.
    $sbqExtraTest = & $sbqWith 'test' (@($sbqShipped) + [ordered]@{ name = 'extra-queue'; setting = 'ExtraQueue' })
    $sbqCalls = [System.Collections.Generic.List[string]]::new()
    $sbqProbed = & {
        function Test-Az { $sbqCalls.Add(($args -join ' ')); $true }
        function Get-AzShow { [pscustomobject]@{ tags = $null } }
        Get-ExistingResources $sbqExtraTest $testNames $false @()
    }
    $probedQueues = @($sbqCalls | Where-Object { $_ -like 'servicebus queue show *' } | ForEach-Object { $_ -replace '^.* -n (\S+)$', '$1' })
    Assert-Equal @(@($sbqShippedNames) + 'extra-queue' | ForEach-Object { "test-$_" }) $probedQueues 'infra probes exactly the configured test queues'
    Assert-Equal @() @($sbqCalls | Where-Object { $_ -like 'servicebus queue show *' -and $_ -notlike "* -g $($testNames.ResourceGroup) --namespace-name $($testNames.ServiceBusNamespace) -n *" }) 'queues are probed in the configuration''s namespace'
    Assert-True ($sbqProbed.Queues['test-extra-queue'] -eq $true) 'a probed queue is reported as existing'

    # 2.3 Validation: every problem names the configuration and the entry.
    $sbqProblems = { param($Config, [string]$Prefix = 'bat') @(Get-QueueConfigProblems $Config $Prefix) -join ' ' }
    $sbqMissing = Get-DeployConfig 'bat' $ConfigDirectory
    $sbqMissing.PSObject.Properties.Remove('serviceBusQueues')
    Assert-True ((& $sbqProblems $sbqMissing) -match "serviceBusQueues must list the Service Bus queues of 'bat'.*it is missing") 'a missing list is a problem'
    Assert-True ((& $sbqProblems (& $sbqWithRaw 'bat' '"bat-queues"')) -match 'it is not a list') 'a list that is not a list is a problem'
    Assert-True ((& $sbqProblems (& $sbqWithRaw 'bat' '[]')) -match 'it is empty') 'an empty list is a problem'
    $sbqOneConfig = & $sbqWithRaw 'bat' '[{ "name": "d365fsc-dataevent", "setting": "D365FscDataEventQueue" }]'
    Assert-Equal 'd365fsc-dataevent' @($sbqOneConfig.serviceBusQueues | ForEach-Object name) 'an environment list replaces the whole base list'
    $sbqOne = & $sbqProblems $sbqOneConfig
    Assert-True ($sbqOne -notmatch 'not a list' -and $sbqOne -match "no entry with the setting 'D365BusinessEventQueue'") "a one-entry list is still a list, and fails for the settings it lacks ($sbqOne)"
    Assert-True ((& $sbqProblems (& $sbqWithRaw 'bat' '["d365fsc-dataevent"]')) -match "serviceBusQueues\[0\] of 'bat' is not an object with a name") 'an entry that is not an object is a problem'
    Assert-True ((& $sbqProblems (& $sbqWith 'bat' (@($sbqShipped) + [ordered]@{ name = 'typo'; duplicateDetecton = $false }))) -match "serviceBusQueues\[5\] of 'bat' has the unknown key 'duplicateDetecton'") 'a misspelt key is a problem, not a silent default'
    Assert-True ((& $sbqProblems (& $sbqWith 'bat' (@($sbqShipped) + [ordered]@{ name = ' '; setting = 'Blank' }))) -match "serviceBusQueues\[5\] of 'bat' has no name") 'a blank name is a problem'
    Assert-True ((& $sbqProblems (& $sbqWith 'bat' (@($sbqShipped) + [ordered]@{ name = 'Capture To D365' }))) -match "has the name 'Capture To D365'; a queue name is lowercase letters and digits") 'an invalid name is a problem'
    Assert-True ((& $sbqProblems (& $sbqWith 'bat' (@($sbqShipped) + [ordered]@{ name = ('a' * 258) }))) -match "resolves to the queue name 'bat-a{258}', which exceeds the Service Bus limit of 260 characters") 'a resolved name over 260 characters is a problem'
    Assert-True ((& $sbqProblems (& $sbqWith 'bat' (@($sbqShipped) + [ordered]@{ name = ('a' * 256) }))) -eq '') 'a resolved name of exactly 260 characters is fine'
    Assert-True ((& $sbqProblems (& $sbqWith 'bat' (@($sbqShipped) + [ordered]@{ name = 'D365FSC-DataEvent' }))) -match "serviceBusQueues\[5\] of 'bat' resolves to the queue name 'bat-D365FSC-DataEvent', which serviceBusQueues\[0\] resolves to too") 'queue names that differ only in case are duplicates'
    Assert-True ((& $sbqProblems (& $sbqWith 'bat' (@($sbqShipped) + [ordered]@{ name = 'bad-setting'; setting = '9Queue' }))) -match "has the setting '9Queue'; a setting name is letters, digits and underscores") 'an invalid setting name is a problem'
    Assert-True ((& $sbqProblems (& $sbqWith 'bat' (@($sbqShipped) + [ordered]@{ name = 'twice'; setting = 'captureToD365Queue' }))) -match "serviceBusQueues\[5\] of 'bat' has the setting 'captureToD365Queue', which serviceBusQueues\[3\] has too") 'settings that differ only in case are duplicates'
    Assert-True ((& $sbqProblems (& $sbqWith 'bat' (@($sbqShipped) + [ordered]@{ name = 'string-flag'; requiresSession = 'true' }))) -match "has requiresSession 'true'; it must be the JSON value true or false") 'a string flag is a problem'
    Assert-True ((& $sbqProblems (& $sbqWith 'bat' (@($sbqShipped) + [ordered]@{ name = 'number-flag'; duplicateDetection = 0 }))) -match "has duplicateDetection '0'; it must be the JSON value true or false") 'a number flag is a problem'
    foreach ($required in $sbqShippedSettings) {
        $without = & $sbqWith 'bat' @($sbqShipped | Where-Object { $_.setting -ne $required })
        Assert-Equal "serviceBusQueues of 'bat' has no entry with the setting '$required', which the Automation app requires (RequiredSettings.Queues)" (& $sbqProblems $without) "a list without $required fails"
    }
    Assert-Throws { Get-ServiceBusQueues (& $sbqWithRaw 'bat' '[]') 'bat' } "(?s)Configuration 'bat' has an invalid serviceBusQueues:.*it is empty" 'Get-ServiceBusQueues throws on an invalid list'
    Assert-Throws { Get-AutomationSettings (& $sbqWithRaw 'test' '[]') $testNames '<insights>' } "Configuration 'test' has an invalid serviceBusQueues" 'settings are not written from an invalid list'
    $sbqTypo = & $sbqWith 'test' (@($sbqShipped) + [ordered]@{ name = 'typo'; requiresSesion = $true })
    Assert-True ((@(Get-OfflinePreflightProblems $sbqTypo @('preflight', 'infra') (Get-AllDeployConfigs $sbqTypo $ConfigDirectory) $false) -join ' ') -match "serviceBusQueues\[5\] of 'test' has the unknown key 'requiresSesion'") 'the offline preflight reports queue problems before any az call'
    Assert-True ((@(Get-OfflinePreflightProblems (& $sbqWith 'test' (@($sbqShipped) + [ordered]@{ name = ('a' * 256) })) @('preflight', 'infra') $allConfigs $false) -join ' ') -match "'test-a{256}', which exceeds") 'the offline preflight resolves the name with the configuration''s own prefix'
    Assert-Equal @() @(Get-OfflinePreflightProblems $configs.test @('preflight', 'infra') $allConfigs $false | Where-Object { $_ -match 'serviceBusQueues' }) 'the committed test list passes the offline preflight'
    foreach ($case in @{ Selected = @('infra'); IsOwner = $true }, @{ Selected = @('infra'); IsOwner = $false }, @{ Selected = @('settings'); IsOwner = $true }, @{ Selected = @('settings'); IsOwner = $false }) {
        Assert-True (@(Get-RequiredConfigPaths $case.Selected $case.IsOwner) -contains 'serviceBusQueues') "$($case.Selected) needs serviceBusQueues (owner: $($case.IsOwner))"
    }
    Assert-True (@(Get-RequiredConfigPaths @('frontend') $true) -notcontains 'serviceBusQueues') 'frontend does not need serviceBusQueues'
    $sbqPlaceholder = & $sbqWith 'test' (@($sbqShipped) + [ordered]@{ name = '<queue name>'; setting = 'ExtraQueue' })
    Assert-True ((@(Get-PlaceholderProblems $sbqPlaceholder @('preflight', 'infra') $false) -join ' ') -match "'serviceBusQueues\[5\]\.name' is still a placeholder") 'a placeholder in serviceBusQueues fails infra'

    # 2.4 Settings: the Automation and simulation queue settings come from the list.
    foreach ($case in @{ Settings = (Get-AutomationSettings $configs.bat $batNames '<insights>'); Prefix = 'bat' }, @{ Settings = (Get-AutomationSettings $configs.test $testNames '<insights>'); Prefix = 'test' }) {
        Assert-Equal @($sbqShippedNames | ForEach-Object { "$($case.Prefix)-$_" }) @($sbqShippedSettings | ForEach-Object { $case.Settings[$_] }) "$($case.Prefix) Automation queue settings are today's values"
    }
    $sbqExtraSettings = Get-AutomationSettings (& $sbqWith 'bat' (@($sbqShipped) + [ordered]@{ name = 'extra-queue'; setting = 'ExtraQueue' } + [ordered]@{ name = 'no-setting' })) $batNames '<insights>'
    Assert-Equal 'bat-extra-queue' $sbqExtraSettings.ExtraQueue 'an added entry writes its setting'
    Assert-Equal @($sbqShippedNames | ForEach-Object { "bat-$_" }) @($sbqShippedSettings | ForEach-Object { $sbqExtraSettings[$_] }) 'an added entry leaves the five settings unchanged'
    Assert-Equal @() @($sbqExtraSettings.Keys | Where-Object { "$($sbqExtraSettings[$_])" -eq 'bat-no-setting' }) 'an entry without a setting writes no setting'
    Assert-Equal @($sbqShippedSettings + 'ExtraQueue') @($sbqExtraSettings.Keys | Where-Object { "$($sbqExtraSettings[$_])" -like 'bat-*' }) 'the queue settings are exactly the entries with a setting'

    $sbqTargets = Get-SimulationTargets $configs.bat $allConfigs
    Assert-Equal @('bat', 'test') @($sbqTargets.Keys) 'bat simulates on bat and test'
    Assert-True ([object]::ReferenceEquals($sbqTargets['test'], $allConfigs['test'])) 'the test target is the test configuration, not bat'
    $sbqDashboard = Get-DashboardApiSettings $configs.bat $batNames $sbqTargets
    Assert-Equal @('bat-d365fsc-dataevent', 'bat-capture-to-d365fsc', 'bat-capture-to-d365fsc-response', 'bat-d365fsc-to-capture') @(
        $sbqDashboard.Simulation__D365FscDataEventQueue, $sbqDashboard.Simulation__CaptureToD365Queue, $sbqDashboard.Simulation__CaptureToD365ResponseQueue, $sbqDashboard.Simulation__D365ToCaptureQueue) 'the four simulation queue settings are today''s bat values'
    Assert-Equal @('Simulation__Enabled', 'Simulation__ServiceBusConnectionString', 'Simulation__D365FscDataEventQueue', 'Simulation__CaptureToD365Queue', 'Simulation__CaptureToD365ResponseQueue', 'Simulation__D365ToCaptureQueue') @($sbqDashboard.Keys | Where-Object { $_ -like 'Simulation__*' }) 'the simulation settings are unchanged'
    $sbqRenamedTest = & $sbqWith 'test' @($sbqShipped | ForEach-Object { if ($_.name -eq 'capture-to-d365fsc') { [ordered]@{ name = 'capture-to-d365fsc-v2'; setting = 'CaptureToD365Queue' } } else { $_ } })
    $sbqSimulateTest = Get-DeployConfig 'bat' $ConfigDirectory $null '{ "dashboard": { "simulationQueuePrefix": "test" } }'
    $sbqRenamedAll = Get-AllDeployConfigs $sbqRenamedTest $ConfigDirectory
    Assert-Equal 'test-capture-to-d365fsc-v2' (Get-DashboardApiSettings $sbqSimulateTest $batNames (Get-SimulationTargets $sbqSimulateTest $sbqRenamedAll)).Simulation__CaptureToD365Queue 'simulating on test reads the test list, not bat''s'
    Assert-Equal 'bat-capture-to-d365fsc' (Get-DashboardApiSettings $configs.bat $batNames (Get-SimulationTargets $configs.bat $sbqRenamedAll)).Simulation__CaptureToD365Queue 'simulating on bat still reads bat''s list'
    Assert-Throws { Get-DashboardApiSettings $simulatePrd $batNames (Get-SimulationTargets $simulatePrd $sbqRenamedAll) } "simulationQueuePrefix is 'prd'.*bat, test only" 'simulating on prd still fails'

    # ---------------------------------------------------------------------------------------------
    Write-Host '== packaging helpers'
    $metadataJson = Get-Content $metadataFixture -Raw
    Assert-Equal $gatedSix (Get-QueueAndTimerFunctions $metadataJson) 'the fixture yields exactly the six queue and timer functions'
    Assert-Equal @() @(Compare-GatedFunctions (Get-QueueAndTimerFunctions $metadataJson) @($configs.bat.automation.gatedFunctions)) 'the fixture matches automation.gatedFunctions'
    Assert-True ((@(Compare-GatedFunctions (@($gatedSix) + 'NewQueueHandler') $gatedSix) -join ' ') -match "'NewQueueHandler' is not in automation.gatedFunctions") 'a new queue function not in the config is named'
    Assert-True ((@(Compare-GatedFunctions @($gatedSix | Select-Object -Skip 1) $gatedSix) -join ' ') -match "'AwaitingD365SweepHandler', which the package does not contain") 'a configured function missing from the package is named'
    Assert-True ((Get-FunctionNames $metadataJson) -contains 'HealthProbe') 'Get-FunctionNames lists HTTP functions too'
    $readme = Get-PackageReadme 'v1' 'abc1234' @('bat', 'test')
    Assert-True ($readme -match 'Deploy-Wilson\.ps1 -Environment bat -Component all, myip, myipremove -WhatIf') 'README.txt carries the operator steps for the owner'
    Assert-True ($readme -match 'Deploy-Wilson\.ps1 -Environment test -Component all, myip, myipremove') 'README.txt carries the steps for the configuration that uses it'
    Assert-True ($readme -match '(?m)^  bat  owner \(non-production, resource group d365fsc-integration-nonprod\)' -and $readme -match '(?m)^  test  uses bat:') 'README.txt lists bat as the owner and test as using it'
    Assert-True ($readme -cnotmatch '\bdev\b') 'README.txt names no dev environment'
    Assert-True ($readme -notmatch 'd365fsc-integration-prod') 'a non-production package README names no production resource group'
    $fullReadme = Get-PackageReadme 'v1' 'abc1234' @('bat', 'test', 'prd')
    Assert-True ($fullReadme -match '(?m)^  prd  owner \(production, resource group d365fsc-integration-prod\)' -and $fullReadme -match 'Deploy-Wilson\.ps1 -Environment prd -Component all, myip, myipremove') 'a full package lists prd and its steps'
    Assert-True ($fullReadme -match 'automation\.triggersEnabled') 'the README says the triggers follow automation.triggersEnabled'
    $testOnly = Get-PackageReadme 'v1' 'abc1234' @('test')
    Assert-True ($testOnly -match 'Not deployed by this package' -and $testOnly -notmatch '-Environment bat -Component') 'a package of test alone lists bat for the names but deploys only test'
    foreach ($text in $readme, $fullReadme, $testOnly) {
        foreach ($term in 'stamp', ('cut' + 'over'), ('leg' + 'acy'), ('roll' + 'back'), '-Component remove', ('EIM_DEPLOY_' + 'EIMDB'), ('EIM_DEPLOY_' + 'SERVICEBUS'), 'scope') {
            Assert-True ($text -notmatch [regex]::Escape($term)) "the package README has no '$term' text"
        }
    }
    $packageAst = [System.Management.Automation.Language.Parser]::ParseFile($packageScriptPath, [ref]$null, [ref]$null)
    $environmentParam = $packageAst.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'Environment' }
    $defaultEnvironments = @($environmentParam.DefaultValue.FindAll({ param($node) $node -is [System.Management.Automation.Language.StringConstantExpressionAst] }, $true) | ForEach-Object { $_.Value })
    Assert-Equal @('bat', 'test', 'prd') $defaultEnvironments 'New-DeploymentPackage.ps1 packages bat, test and prd by default'

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
        foreach ($required in 'Deploy-Wilson.ps1', 'README.txt', 'config/deploy.base.json', 'config/deploy.bat.json', 'artifacts/manifest.json', 'artifacts/gateway.zip', 'artifacts/dashboard-api.zip', 'artifacts/wilson-automation.zip', 'artifacts/dbmigrator/ARM.Integration.DbMigrator.exe') {
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
