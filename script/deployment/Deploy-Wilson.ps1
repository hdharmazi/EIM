<#
.SYNOPSIS
    Deploys the Wilson EIM apps (Automation, D365 F&SC gateway, dashboard) to Azure.

.DESCRIPTION
    One script for every non-production configuration. -Environment names a configuration file
    script/deployment/config/deploy.<name>.json (today nonprod, dev, bat and test); any file with a matching
    name is accepted, and nothing here lists the environments or reads the ENVIRONMENT values in eimdb
    (DEV, DEV2, BAT, TEST are database rows, not deployment configurations).

    Configuration is merged in this order: deploy.base.json, deploy.<name>.json, -ConfigOverridesFile, then the
    JSON in EIM_DEPLOY_CONFIG_OVERRIDES. Objects merge key by key; lists and plain values are replaced.

    Names follow {type}-{workload}-{component}-{client}-{suffix}: the suffix is the configuration name for
    per-environment resources and sharedSuffix (nonprod) for shared ones. Storage uses st{client}{workload}
    {shortcomponent}{suffix}. Any derived name can be overridden under "names"; the kept resources (resource
    group, Service Bus namespace, Key Vault, SQL server, database, dashboard API and static web app) are.

    Each configuration has a scope:
      shared       (nonprod)       plan, Log Analytics, gateway, dashboard API and SPA, schema migrations
      environment  (dev, bat, ...) the five queues and the Automation storage, Insights and Function App
    A component outside the scope is rejected, naming the scope that owns it; 'all' expands to the scope's own.

    Secrets come only from EIM_DEPLOY_EIMDB and EIM_DEPLOY_SERVICEBUS, or a hidden prompt when run
    interactively outside a pipeline. They are written to Azure through temporary files deleted afterwards,
    and never printed or placed on a command line.

    Application code is taken from a package built by New-DeploymentPackage.ps1 (artifacts/manifest.json
    next to this script, or the folder given by -PackagePath). This script never runs dotnet publish.

    Components, always run in this order:
      preflight   fails, changing nothing, on unfilled <...> values the requested components need, missing
                  secrets, a derived name equal to a legacy name, missing package artifacts, and a package
                  whose queue/timer functions differ from automation.gatedFunctions. When code is deployed
                  without 'settings', also fails on settings the config defines that an app lacks, warns on
                  differing values and on retired settings still present. Names only, never values. Added
                  automatically before any component that changes something, except 'myipremove' on its own,
                  so the firewall rule is removed even when preflight would fail.
      infra       shared: plan, Log Analytics, gateway storage/Insights/app; the dashboard API and static web
                  app only when absent. environment: queues, Automation storage/Insights/app. New Function Apps
                  get Always On, HTTPS only, TLS 1.2, FTPS disabled and DOTNET-ISOLATED|10.0 on every run.
      settings    shared: gateway and dashboard API. environment: Automation, with the gated triggers
                  AzureWebJobs.<fn>.Disabled=true written only when absent; retired settings deleted.
      myip        allow this machine's (or agent's) public IP on the SQL server
      schema      database migrations with the packaged ARM.Integration.DbMigrator.exe (--whatif under -WhatIf)
      myipremove  delete the rule 'myip' added
      gateway     zip-deploy gateway.zip to the gateway Function App
      backend     zip-deploy dashboard-api.zip to the dashboard API (in place)
      frontend    deploy the packaged SPA to the static web app (in place) and link the API as its backend
      automation  zip-deploy wilson-automation.zip; refused while any gated setting is missing
      verify      report only: host, state, Always On, plan, outbound IPs, gated trigger states, SWA backend
      health      fails unless each Function App host reports Running within 5 minutes, the dashboard API
                  answers below HTTP 500 and the static web app serves its root

    On demand only, never part of 'all', and each runs on its own:
      cutover     environment: move the gated triggers from the legacy Automation app to the new one.
                  shared: copy the named gateway host and D365Data/D365Service function keys.
      rollback    environment: the reverse of cutover; refused once the legacy app is gone
      remove      delete the resources listed under "legacy" in the configuration. Operator machine only:
                  needs a 'remove -WhatIf' run first the same day and the configuration name typed to confirm.

.EXAMPLE
    ./script/deployment/Deploy-Wilson.ps1 -Environment dev -Component all -WhatIf -PackagePath artifacts/package/wtc-eim-deploy-local

.EXAMPLE
    From an extracted package:
    ./Deploy-Wilson.ps1 -Environment nonprod -Component gateway, backend, frontend

.EXAMPLE
    ./Deploy-Wilson.ps1 -Environment dev -Component cutover
#>
# After the help block: a #Requires line above it stops Get-Help from finding the help.
#Requires -Version 7.2
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    # The name of a configuration file script/deployment/config/deploy.<name>.json.
    [ValidatePattern('^[a-z0-9]+$')]
    [string]$Environment,

    [ValidateSet('preflight', 'infra', 'settings', 'myip', 'schema', 'myipremove', 'gateway', 'backend', 'frontend', 'automation', 'verify', 'health', 'all', 'cutover', 'rollback', 'remove')]
    [string[]]$Component,

    # JSON merged over the configuration, before EIM_DEPLOY_CONFIG_OVERRIDES.
    [string]$ConfigOverridesFile,

    # The package folder (the one holding artifacts/manifest.json). Defaults to this script's folder.
    [string]$PackagePath,

    # cutover for the shared configuration only: also copy the auto-generated 'default' keys.
    [switch]$IncludeDefaultKeys
)

$ErrorActionPreference = 'Stop'
# Preview is handled through $Preview only; left on, -WhatIf makes cmdlets such as ForEach-Object print their
# input objects, which here can be app settings including secret values.
$Preview = [bool]$WhatIfPreference
$WhatIfPreference = $false
$ConfirmDisabled = $PSBoundParameters.ContainsKey('Confirm') -and -not [bool]$PSBoundParameters['Confirm']
$PackageRoot = if ($PackagePath) { $PackagePath } else { $PSScriptRoot }
$PackageArtifacts = Join-Path $PackageRoot 'artifacts'
$InPackage = Test-Path (Join-Path $PackageArtifacts 'manifest.json')
$ConfigDirectory = Join-Path $PSScriptRoot 'config'
$ArmApiVersion = '2023-12-01'

$ComponentOrder = @('preflight', 'infra', 'settings', 'myip', 'schema', 'myipremove', 'gateway', 'backend', 'frontend', 'automation', 'verify', 'health')
$ReadOnlyComponents = @('preflight', 'verify', 'health')
# Cleanup that must run even when preflight would fail; the pipeline calls it in a finally.
$CleanupComponents = @('myipremove')
$OnDemandComponents = @('cutover', 'rollback', 'remove')
$CodeComponents = @('gateway', 'backend', 'frontend', 'automation')
$ScopeComponents = @{
    shared      = @('preflight', 'infra', 'settings', 'myip', 'schema', 'myipremove', 'gateway', 'backend', 'frontend', 'verify', 'health')
    environment = @('preflight', 'infra', 'settings', 'automation', 'verify', 'health')
}

# Settings that must be identical on the legacy and new Automation app: same database, namespace and queues.
$QueueSettingNames = @('D365FscDataEventQueue', 'D365BusinessEventQueue', 'D365ToCaptureQueue', 'CaptureToD365Queue', 'CaptureToD365QueueResponse')
$ParityCriticalSettings = @('EIMDB', 'ServiceBus') + $QueueSettingNames
# Owned by each app itself (runtime, storage, telemetry, trigger state), so never compared at cutover.
$ParityIgnoredSettings = @(
    'FUNCTIONS_EXTENSION_VERSION'
    'FUNCTIONS_WORKER_RUNTIME'
    'APPINSIGHTS_INSTRUMENTATIONKEY'
    'APPLICATIONINSIGHTS_CONNECTION_STRING'
    'AzureWebJobsStorage'
    'AzureWebJobsDashboard'
    'AzureWebJobsSecretStorageType'
    'AzureWebJobs.*.Disabled'
    'WEBSITE_*'
    'SCM_*'
    'DOCKER_*'
    'ENABLE_ORYX_BUILD'
)
# Compared by name only in the drift check: their values are per app or not known without the secrets.
$DriftNameOnlySettings = @('APPLICATIONINSIGHTS_CONNECTION_STRING', 'AzureWebJobs.*.Disabled')
# The only resource types 'remove' ever deletes.
$RemovableTypes = @('Microsoft.Web/sites', 'Microsoft.Web/serverfarms', 'Microsoft.Storage/storageAccounts', 'Microsoft.Insights/components')
$FunctionRuntime = 'DOTNET-ISOLATED|10.0'
$GatewayKeyFunctions = @('D365Data', 'D365Service')

# --------------------------------------------------------------------------------------------
# Process and Azure CLI helpers
# --------------------------------------------------------------------------------------------

# Started through System.Diagnostics.Process rather than '&': after dotnet has run, PowerShell's own launcher can fail with
# "StandardOutputEncoding is only supported when standard output is redirected".
function Invoke-Native([string]$FilePath, [string[]]$ArgumentList, [switch]$Capture, [hashtable]$Environment = @{}) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($FilePath)
    foreach ($argument in $ArgumentList) { $startInfo.ArgumentList.Add($argument) }
    $startInfo.UseShellExecute = $false
    $startInfo.WorkingDirectory = (Get-Location).ProviderPath
    foreach ($key in $Environment.Keys) { $startInfo.Environment[$key] = "$($Environment[$key])" }
    if ($Capture) {
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $startInfo.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
        $startInfo.StandardErrorEncoding = [System.Text.UTF8Encoding]::new($false)
    }

    $process = [System.Diagnostics.Process]::Start($startInfo)
    try {
        if (-not $Capture) {
            $process.WaitForExit()
            return [pscustomobject]@{ ExitCode = $process.ExitCode; StdOut = ''; StdErr = '' }
        }
        $stderr = $process.StandardError.ReadToEndAsync()
        $stdout = $process.StandardOutput.ReadToEnd()
        $process.WaitForExit()
        [pscustomobject]@{ ExitCode = $process.ExitCode; StdOut = $stdout; StdErr = $stderr.GetAwaiter().GetResult() }
    }
    finally { $process.Dispose() }
}

function Resolve-Tool([string]$Name) {
    $command = Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $command -and $IsWindows) {
        # Tools installed after this terminal opened are only on the PATH stored in the registry.
        $env:Path = @([Environment]::GetEnvironmentVariable('Path', 'Machine'), [Environment]::GetEnvironmentVariable('Path', 'User')) -join ';'
        $command = Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    }
    if (-not $command) { throw "'$Name' not found on PATH. Install it, then open a new terminal." }
    $command.Source
}

# Streams to the console; returns the exit code.
function Invoke-Tool([string]$Name, [string[]]$ArgumentList, [hashtable]$Environment = @{}) {
    (Invoke-Native (Resolve-Tool $Name) $ArgumentList -Environment $Environment).ExitCode
}

$script:AzLauncher = $null
function Get-AzLauncher {
    if ($script:AzLauncher) { return $script:AzLauncher }
    $azPath = Resolve-Tool 'az'
    # az.cmd only runs '<CLI>\python.exe -IBm azure.cli'; calling python directly avoids the batch-file hop.
    $python = Join-Path (Split-Path (Split-Path $azPath)) 'python.exe'
    $script:AzLauncher = if ($IsWindows -and (Test-Path $python)) {
        @{ File = $python; Prefix = @('-IBm', 'azure.cli'); Environment = @{ AZ_INSTALLER = 'MSI'; PYTHONIOENCODING = 'utf-8' } }
    } else {
        @{ File = $azPath; Prefix = @(); Environment = @{ PYTHONIOENCODING = 'utf-8' } }
    }
    $script:AzLauncher
}

function Invoke-AzProcess([object[]]$Arguments) {
    $az = Get-AzLauncher
    Invoke-Native $az.File (@($az.Prefix) + @($Arguments | ForEach-Object { "$_" })) -Capture -Environment $az.Environment
}

# Azure's gateway sometimes times out or throttles a call. Throttling (429) means Azure didn't run it, so every call is
# retried on it; other temporary errors only for reads, as a write that timed out may have applied.
$ThrottledAzError = 'TooManyRequests|Too Many Requests'
$TransientAzError = 'GatewayTimeout|Gateway Timeout|ServiceUnavailable|Service Unavailable|Bad Gateway'
$AzAttempts = 4

function Test-AzRead([object[]]$Arguments) {
    $words = @($Arguments | ForEach-Object { "$_" })
    if ($words[0] -eq 'rest') {
        $method = [array]::IndexOf($words, '--method')
        return $method -ge 0 -and $words[$method + 1] -ieq 'get'
    }
    @($words | Where-Object { $_ -in 'show', 'list' }).Count -gt 0
}

function Invoke-Az {
    $read = Test-AzRead $args
    for ($attempt = 1; ; $attempt++) {
        $result = Invoke-AzProcess $args
        if ($result.ExitCode -eq 0) { break }

        $stderr = @($result.StdErr -split "`r?`n" | Where-Object { $_ -and $_ -notmatch 'UserWarning|warnings\.warn|cryptography' }) -join ' '
        $retryable = $stderr -match $ThrottledAzError -or ($read -and $stderr -match $TransientAzError)
        if (-not $retryable -or $attempt -ge $AzAttempts) {
            throw "az $($args[0]) $($args[1]) $($args[2]) failed: $stderr"
        }

        $delay = 15 * $attempt
        Write-Warning "az $($args[0]) $($args[1]) $($args[2]): Azure answered a temporary error; retrying in $delay s (attempt $($attempt + 1) of $AzAttempts)"
        Start-Sleep -Seconds $delay
    }
    @($result.StdOut -split "`r?`n" | Where-Object { $_ -ne '' })
}

function Invoke-AzJson {
    $text = (Invoke-Az @args -o json) -join "`n"
    if (-not [string]::IsNullOrWhiteSpace($text)) { $text | ConvertFrom-Json }
}

function Test-Az {
    (Invoke-AzProcess (@($args) + @('-o', 'none'))).ExitCode -eq 0
}

# Written to the user's temp folder and deleted after use, so setting values never reach a command line.
function Write-TempJson($Object) {
    $path = Join-Path ([IO.Path]::GetTempPath()) "wtc-deploy-$([guid]::NewGuid()).json"
    ConvertTo-Json -InputObject $Object -Depth 20 | Set-Content -Path $path -Encoding utf8NoBOM -WhatIf:$false
    $path
}

function Invoke-AzRest([string]$Method, [string]$Path, $Body) {
    $url = "https://management.azure.com$Path" + '?api-version=' + $ArmApiVersion
    if ($null -eq $Body) { return Invoke-AzJson rest --method $Method --url $url }

    $file = Write-TempJson $Body
    try { Invoke-AzJson rest --method $Method --url $url --body "@$file" --headers 'Content-Type=application/json' }
    finally { Remove-Item $file -ErrorAction SilentlyContinue -WhatIf:$false }
}

function Get-SiteId([string]$ResourceGroup, [string]$Name) {
    "$(Invoke-Az webapp show -g $ResourceGroup -n $Name --query id -o tsv)"
}

function Get-AppSettingList([string]$ResourceGroup, [string]$Name) {
    @(Invoke-AzJson webapp config appsettings list -g $ResourceGroup -n $Name)
}

function Set-AppSettingList([string]$ResourceGroup, [string]$Name, $Settings) {
    $list = @($Settings.GetEnumerator() | ForEach-Object { @{ name = $_.Key; value = "$($_.Value)"; slotSetting = $false } })
    if ($list.Count -eq 0) { return }

    $file = Write-TempJson $list
    try { Invoke-Az webapp config appsettings set -g $ResourceGroup -n $Name --settings "@$file" -o none | Out-Null }
    finally { Remove-Item $file -ErrorAction SilentlyContinue -WhatIf:$false }
}

function Write-DeployWarning([string]$Message) {
    if ($env:TF_BUILD) { Write-Host "##vso[task.logissue type=warning]$Message" } else { Write-Warning $Message }
}

# --------------------------------------------------------------------------------------------
# Configuration and naming (pure: no Azure calls)
# --------------------------------------------------------------------------------------------

# Objects merge key by key; anything else (values, lists) replaces the earlier value.
function Merge-ConfigObject($Target, $Patch, [string]$Path, [switch]$Quiet) {
    foreach ($property in $Patch.PSObject.Properties) {
        $current = $Target.PSObject.Properties[$property.Name]
        if ($current -and $current.Value -is [System.Management.Automation.PSCustomObject] -and $property.Value -is [System.Management.Automation.PSCustomObject]) {
            Merge-ConfigObject $current.Value $property.Value "$Path$($property.Name)." -Quiet:$Quiet
            continue
        }
        $Target | Add-Member -NotePropertyName $property.Name -NotePropertyValue $property.Value -Force
        if (-not $Quiet) { Write-Host "  config override: $Path$($property.Name)" }
    }
}

function Merge-ConfigJson($Config, [string]$Json, [string]$Source) {
    $patch = try { $Json | ConvertFrom-Json } catch { throw "$Source is not valid JSON: $($_.Exception.Message)" }
    if ($patch -isnot [System.Management.Automation.PSCustomObject]) { throw "$Source must be a JSON object." }
    if ($patch.PSObject.Properties.Name -contains 'environment') { throw "$Source cannot change 'environment'." }
    Merge-ConfigObject $Config $patch ''
}

function Get-ConfigurationNames([string]$Directory) {
    @(Get-ChildItem -Path $Directory -Filter 'deploy.*.json' -File -ErrorAction SilentlyContinue |
            ForEach-Object { $_.Name -replace '^deploy\.(.+)\.json$', '$1' } |
            Where-Object { $_ -ne 'base' } | Sort-Object)
}

# Any deploy.<name>.json is a configuration; there is no list of environments in this script.
function Get-DeployConfig([string]$Name, [string]$Directory = $ConfigDirectory, [string]$OverridesFile, [string]$OverridesJson) {
    if ([string]::IsNullOrWhiteSpace($Name)) { throw "-Environment is required: the <name> of a deploy.<name>.json file in '$Directory'." }
    $available = Get-ConfigurationNames $Directory
    if ($Name -eq 'base' -or $Name -notmatch '^[a-z0-9]+$' -or $available -notcontains $Name) {
        throw "No deployment configuration named '$Name': there is no deploy.$Name.json in '$Directory'. Configured: $($available -join ', ')."
    }

    $path = Join-Path $Directory "deploy.$Name.json"
    $own = Get-Content $path -Raw | ConvertFrom-Json
    if ($own.environment -ne $Name) {
        throw "Configuration '$path' declares environment '$($own.environment)'; it must equal the file name '$Name'."
    }

    $basePath = Join-Path $Directory 'deploy.base.json'
    if (-not (Test-Path $basePath)) { throw "'$basePath' is missing." }
    $config = Get-Content $basePath -Raw | ConvertFrom-Json
    if ($config.PSObject.Properties.Name -contains 'environment') { throw "'$basePath' cannot set 'environment'." }
    Merge-ConfigObject $config $own '' -Quiet

    if ($OverridesFile) {
        if (-not (Test-Path $OverridesFile)) { throw "Overrides file '$OverridesFile' not found." }
        Merge-ConfigJson $config (Get-Content $OverridesFile -Raw) "Overrides file '$OverridesFile'"
    }
    # Set by the pipeline from a DevOps variable; an undefined variable arrives as its literal '$(name)'.
    if (-not [string]::IsNullOrWhiteSpace($OverridesJson) -and $OverridesJson -notmatch '^\$\(') {
        Merge-ConfigJson $config $OverridesJson 'EIM_DEPLOY_CONFIG_OVERRIDES'
    }

    if ($config.scope -notin 'shared', 'environment') { throw "Configuration '$Name' has scope '$($config.scope)'; expected 'shared' or 'environment'." }
    foreach ($key in 'client', 'workload', 'sharedSuffix', 'location') {
        if ([string]::IsNullOrWhiteSpace($config.$key)) { throw "Configuration '$Name' is missing required key '$key'." }
    }
    if (@($config.automation.gatedFunctions | Where-Object { $_ }).Count -eq 0) { throw "Configuration '$Name' has no automation.gatedFunctions." }
    $config
}

function Test-Placeholder($Value) { "$Value" -match '^<.*>$' }

# Dotted paths (e.g. entra.tenantId) of every value still holding a <...> placeholder.
function Get-PlaceholderPaths($Value, [string]$Path = '') {
    if ($null -eq $Value) { return }
    if ($Value -is [System.Management.Automation.PSCustomObject]) {
        foreach ($property in $Value.PSObject.Properties) {
            Get-PlaceholderPaths $property.Value $(if ($Path) { "$Path.$($property.Name)" } else { $property.Name })
        }
        return
    }
    if ($Value -is [System.Collections.IList]) {
        for ($i = 0; $i -lt $Value.Count; $i++) { Get-PlaceholderPaths $Value[$i] "$Path[$i]" }
        return
    }
    if (Test-Placeholder $Value) { $Path }
}

# The configuration paths each requested component reads.
function Get-RequiredConfigPaths([string[]]$Selected, [string]$Scope) {
    $paths = [System.Collections.Generic.List[string]]::new()
    $paths.AddRange([string[]]@('subscriptionId', 'location', 'names'))
    if ($Selected -contains 'infra') { $paths.Add('appServicePlan') }
    if ($Selected -contains 'settings') {
        if ($Scope -eq 'environment') { $paths.Add('automation') }
        else { $paths.AddRange([string[]]@('entra.tenantId', 'entra.clientId', 'entra.audience', 'gateway', 'dashboard')) }
    }
    if ($Selected -contains 'frontend') { $paths.AddRange([string[]]@('entra.tenantId', 'entra.clientId', 'entra.scope')) }
    if ($Selected | Where-Object { $_ -in 'cutover', 'rollback', 'remove' }) { $paths.Add('legacy') }
    @($paths | Select-Object -Unique)
}

function Get-PlaceholderProblems($Config, [string[]]$Selected) {
    $required = Get-RequiredConfigPaths $Selected $Config.scope
    foreach ($path in @(Get-PlaceholderPaths $Config)) {
        $needed = $required | Where-Object { $path -eq $_ -or $path.StartsWith("$_.") -or $path.StartsWith("$_[") }
        if ($needed) { "'$path' is still a placeholder; fill it in the configuration or EIM_DEPLOY_CONFIG_OVERRIDES" }
    }
}

function Get-ResourceNames($Config, [switch]$NoOverrides) {
    $c = $Config.client
    $w = $Config.workload
    $e = $Config.environment
    $s = $Config.sharedSuffix
    $overrides = if ($NoOverrides) { $null } else { $Config.names }
    $pick = {
        param($key, $default)
        if ($overrides -and -not [string]::IsNullOrWhiteSpace($overrides.$key)) { $overrides.$key } else { $default }
    }

    $names = [pscustomobject]@{
        ResourceGroup       = & $pick 'resourceGroup'       "rg-$w-$c-$s"
        ServiceBusNamespace = & $pick 'serviceBusNamespace' "sbns-$w-$c-$s"
        KeyVault            = & $pick 'keyVault'            "kv-$w-$c-$s"
        SqlServer           = & $pick 'sqlServer'           "sql-$w-$c-$s"
        SqlDatabase         = & $pick 'sqlDatabase'         "sqldb-$w-$c-$s"
        AppServicePlan      = & $pick 'appServicePlan'      "asp-$w-$c-$s"
        LogAnalytics        = & $pick 'logAnalytics'        "log-$w-$c-$s"
        GatewayApp          = & $pick 'gatewayApp'          "azf-$w-gateway-$c-$s"
        GatewayStorage      = & $pick 'gatewayStorage'      "st$c${w}gw$s"
        GatewayInsights     = & $pick 'gatewayInsights'     "appins-$w-gateway-$c-$s"
        DashboardApi        = & $pick 'dashboardApi'        "app-$w-dashboard-api-$c-$s"
        StaticWebApp        = & $pick 'staticWebApp'        "swa-$w-dashboard-$c-$s"
        AutomationApp       = & $pick 'automationApp'       "azf-$w-automation-$c-$e"
        AutomationStorage   = & $pick 'automationStorage'   "st$c${w}auto$e"
        AutomationInsights  = & $pick 'automationInsights'  "appins-$w-automation-$c-$e"
        QueuePrefix         = & $pick 'queuePrefix'         $e
    }

    foreach ($storage in $names.GatewayStorage, $names.AutomationStorage) {
        if ($storage -notmatch '^[a-z0-9]{3,24}$') { throw "Storage account name '$storage' must be 3-24 lowercase letters or digits; override it under 'names'." }
    }
    # Longer names are truncated into the Functions host ID, which then collides between apps.
    foreach ($app in $names.GatewayApp, $names.AutomationApp) {
        if ($app.Length -gt 32) { throw "Function App name '$app' exceeds 32 characters; override it under 'names'." }
    }
    $names
}

# Every resource name the deployment uses, derived and overridden, for the legacy-name guards.
function Get-NamesInUse($Config) {
    $all = foreach ($names in (Get-ResourceNames $Config), (Get-ResourceNames $Config -NoOverrides)) {
        $names.PSObject.Properties | Where-Object Name -ne 'QueuePrefix' | ForEach-Object { "$($_.Value)" }
    }
    @($all | Where-Object { $_ } | Sort-Object -Unique)
}

function Get-LegacyNames($Config) {
    $legacy = $Config.legacy
    if (-not $legacy) { return @() }
    @(@($legacy.automationApp, $legacy.gatewayApp) + @($legacy.resources | ForEach-Object { $_.name }) | Where-Object { $_ } | Sort-Object -Unique)
}

function Get-LegacyNameConflicts([string[]]$LegacyNames, [string[]]$NamesInUse) {
    foreach ($name in $LegacyNames) {
        if ($NamesInUse -contains $name) { "legacy name '$name' is also a name in use by the deployment" }
    }
}

function Get-QueueNames([string]$Prefix) {
    [ordered]@{
        D365FscDataEventQueue      = "$Prefix-d365fsc-dataevent"
        D365BusinessEventQueue     = "$Prefix-d365fsc-businessevents"
        D365ToCaptureQueue         = "$Prefix-d365fsc-to-capture"
        CaptureToD365Queue         = "$Prefix-capture-to-d365fsc"
        CaptureToD365QueueResponse = "$Prefix-capture-to-d365fsc-response"
    }
}

# --------------------------------------------------------------------------------------------
# Components and scope (pure)
# --------------------------------------------------------------------------------------------

function Resolve-Components([string[]]$Requested, [string]$Scope, [string]$SharedName) {
    $requested = @($Requested | Where-Object { $_ } | ForEach-Object { "$_".ToLowerInvariant() } | Select-Object -Unique)
    if ($requested.Count -eq 0) { throw "-Component is required, e.g. -Component all." }

    $onDemand = @($requested | Where-Object { $OnDemandComponents -contains $_ })
    if ($onDemand.Count -gt 0) {
        if ($requested.Count -gt 1) { throw "'cutover', 'rollback' and 'remove' run on their own; requested: $($requested -join ', ')." }
        return $onDemand
    }

    $owned = $ScopeComponents[$Scope]
    if (-not $owned) { throw "Unknown scope '$Scope'." }
    foreach ($name in @($requested | Where-Object { $_ -ne 'all' })) {
        if ($owned -contains $name) { continue }
        if ($ScopeComponents['shared'] -contains $name) {
            throw "'$name' is a shared component, owned by the shared configuration '$SharedName'; run it with -Environment $SharedName."
        }
        if ($ComponentOrder -notcontains $name) { throw "Unknown component '$name'." }
        throw "'$name' is a per-environment component, owned by the environment configurations (e.g. dev, bat, test); it is not deployed from the shared configuration '$SharedName'."
    }

    $wanted = if ($requested -contains 'all') { $owned } else { $requested }
    $selected = @($ComponentOrder | Where-Object { $wanted -contains $_ })
    if ($selected | Where-Object { $ReadOnlyComponents -notcontains $_ -and $CleanupComponents -notcontains $_ }) {
        $selected = @($ComponentOrder | Where-Object { $_ -eq 'preflight' -or $selected -contains $_ })
    }
    $selected
}

# The secret variables the selected components need.
function Get-RequiredSecrets([string[]]$Selected, [string]$Scope) {
    $needed = @()
    if ($Selected -contains 'settings') { $needed += 'EIM_DEPLOY_EIMDB', 'EIM_DEPLOY_SERVICEBUS' }
    if ($Selected | Where-Object { $_ -in 'myip', 'schema', 'myipremove' }) { $needed += 'EIM_DEPLOY_EIMDB' }
    @($needed | Select-Object -Unique)
}

# Package files each code component deploys.
function Get-RequiredArtifacts([string[]]$Selected, $Config) {
    $map = [ordered]@{
        schema     = 'dbmigrator/ARM.Integration.DbMigrator.exe'
        gateway    = 'gateway.zip'
        backend    = 'dashboard-api.zip'
        frontend   = "dashboard-frontend.$($Config.environment).zip"
        automation = 'wilson-automation.zip'
    }
    @($map.Keys | Where-Object { $Selected -contains $_ } | ForEach-Object { $map[$_] })
}

function Compare-GatedFunctions([string[]]$PackageFunctions, [string[]]$GatedFunctions) {
    foreach ($function in $PackageFunctions) {
        if ($GatedFunctions -notcontains $function) { "the package's queue/timer function '$function' is not in automation.gatedFunctions" }
    }
    foreach ($function in $GatedFunctions) {
        if ($PackageFunctions -notcontains $function) { "automation.gatedFunctions lists '$function', which the package does not contain" }
    }
}

# --------------------------------------------------------------------------------------------
# Settings (pure)
# --------------------------------------------------------------------------------------------

function Assert-SettingValues($Settings, [string]$App) {
    foreach ($key in @($Settings.Keys)) {
        if ([string]::IsNullOrWhiteSpace("$($Settings[$key])")) { throw "App setting '$key' for '$App' resolved to empty; check the configuration." }
    }
}

function Remove-RetiredSettings($Settings, [string[]]$RetiredSettings) {
    foreach ($key in @($Settings.Keys)) {
        if ($RetiredSettings -contains $key) { $Settings.Remove($key) }
    }
}

function Get-AutomationSettings($Config, $Names, $Secrets, [string]$InsightsConnection) {
    $a = $Config.automation
    $settings = [ordered]@{
        FUNCTIONS_WORKER_RUNTIME              = 'dotnet-isolated'
        APPLICATIONINSIGHTS_CONNECTION_STRING = $InsightsConnection
        ServiceBus                            = $Secrets.ServiceBus
        EIMDB                                 = $Secrets.EIMDB
    }
    $queues = Get-QueueNames $Names.QueuePrefix
    foreach ($key in $queues.Keys) { $settings[$key] = $queues[$key] }
    $settings['AwaitingD365SweepSchedule'] = $a.awaitingD365SweepSchedule
    $settings['AwaitingD365StuckTimeoutHours'] = $a.awaitingD365StuckTimeoutHours
    $settings['ReleaseSweepSchedule'] = $a.releaseSweepSchedule
    $settings['ReleaseSweepBatchSize'] = $a.releaseSweepBatchSize
    $settings['RetrySweepSchedule'] = $a.retrySweepSchedule
    $settings['RetrySweepBatchSize'] = $a.retrySweepBatchSize
    $settings['RetryMaxAttempts'] = $a.retryMaxAttempts
    $settings['RetryBaseDelaySeconds'] = $a.retryBaseDelaySeconds
    $settings['RetryMaxDelaySeconds'] = $a.retryMaxDelaySeconds
    $settings['DefaultEnvironment'] = "$($Config.environment)".ToLowerInvariant()
    $settings['ExcludedUsers'] = $a.excludedUsers

    Remove-RetiredSettings $settings @($a.retiredSettings)
    Assert-SettingValues $settings $Names.AutomationApp
    $settings
}

function Get-GatewayBaseUrl($Config, $Names) {
    if (-not [string]::IsNullOrWhiteSpace($Config.gateway.baseUrl)) { return "$($Config.gateway.baseUrl)".TrimEnd('/') }
    "https://$($Names.GatewayApp).azurewebsites.net"
}

function Get-GatewaySettings($Config, $Names, $Secrets, [string]$InsightsConnection) {
    $g = $Config.gateway
    $settings = [ordered]@{
        FUNCTIONS_WORKER_RUNTIME              = 'dotnet-isolated'
        APPLICATIONINSIGHTS_CONNECTION_STRING = $InsightsConnection
        EIMDB                                 = $Secrets.EIMDB
        Gateway__BaseUrl                      = Get-GatewayBaseUrl $Config $Names
        Gateway__MaxRequestBytes              = $g.maxRequestBytes
        Gateway__IdempotencyRetention         = $g.idempotencyRetention
        Gateway__IdempotencyPurgeSchedule     = $g.idempotencyPurgeSchedule
    }
    Assert-SettingValues $settings $Names.GatewayApp
    $settings
}

function Get-DashboardApiSettings($Config, $Names, $Secrets) {
    $entra = $Config.entra
    $queues = Get-QueueNames $Config.dashboard.simulationQueuePrefix
    $settings = [ordered]@{
        ASPNETCORE_ENVIRONMENT                 = 'Production'
        ConnectionStrings__EIMDB               = $Secrets.EIMDB
        AzureAd__TenantId                      = $entra.tenantId
        AzureAd__ClientId                      = $entra.clientId
        AzureAd__Audience                      = $entra.audience
        Simulation__ServiceBusConnectionString = $Secrets.ServiceBus
        Simulation__D365FscDataEventQueue      = $queues.D365FscDataEventQueue
        Simulation__CaptureToD365Queue         = $queues.CaptureToD365Queue
        Simulation__CaptureToD365ResponseQueue = $queues.CaptureToD365QueueResponse
        Simulation__D365ToCaptureQueue         = $queues.D365ToCaptureQueue
        # Same value the gateway is deployed with, so the address the dashboard verifies keys against
        # cannot drift from the one the gateway actually answers on.
        Gateway__BaseUrl                       = Get-GatewayBaseUrl $Config $Names
        Gateway__AppName                       = $Names.GatewayApp
    }
    # Extra gateways the dashboard can verify keys against: the legacy gateway during the overlap.
    $i = 0
    foreach ($target in @($Config.gateway.legacyVerificationTargets | Where-Object { $_ })) {
        $settings["Gateway__Applications__${i}__Name"] = $target.name
        $settings["Gateway__Applications__${i}__BaseUrl"] = $target.baseUrl
        $i++
    }
    Assert-SettingValues $settings $Names.DashboardApi
    $settings
}

# Gated triggers start disabled; an existing value (e.g. set by cutover) is never overwritten.
function Get-GatedSettingsToWrite([string[]]$GatedFunctions, $Current) {
    $existing = @($Current | ForEach-Object { "$($_.name)" })
    $settings = [ordered]@{}
    foreach ($function in $GatedFunctions) {
        $name = "AzureWebJobs.$function.Disabled"
        if ($existing -notcontains $name) { $settings[$name] = 'true' }
    }
    $settings
}

function Get-MissingGatedSettings([string[]]$GatedFunctions, $Current) {
    $existing = @($Current | ForEach-Object { "$($_.name)" })
    @($GatedFunctions | ForEach-Object { "AzureWebJobs.$_.Disabled" } | Where-Object { $existing -notcontains $_ })
}

# Only the retired names actually present on the app; nothing else is ever deleted from an Automation app.
function Get-RetiredSettingsToDelete([string[]]$RetiredSettings, $Current) {
    @($Current | ForEach-Object { "$($_.name)" } | Where-Object { $RetiredSettings -contains $_ })
}

# Gateway__Applications__<n>__* entries on the dashboard API that the configuration no longer lists.
function Get-StaleVerificationTargetSettings($Settings, $Current) {
    @($Current | ForEach-Object { "$($_.name)" } | Where-Object { $_ -like 'Gateway__Applications__*' -and -not $Settings.Contains($_) })
}

function ConvertTo-SettingMap($List) {
    $map = [System.Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in @($List)) { if ($item -and $item.name) { $map["$($item.name)"] = "$($item.value)" } }
    , $map
}

# Code-only runs: an app must already have every setting the config defines. Names only in the messages.
function Get-SettingsDrift([string]$App, $Expected, $Current, [string[]]$RetiredSettings) {
    $actual = ConvertTo-SettingMap $Current
    $failures = [System.Collections.Generic.List[string]]::new()
    $warnings = [System.Collections.Generic.List[string]]::new()

    $missing = @()
    foreach ($key in @($Expected.Keys)) {
        if (-not $actual.ContainsKey($key)) { $missing += $key; continue }
        $expectedValue = "$($Expected[$key])"
        if (($DriftNameOnlySettings | Where-Object { $key -like $_ }) -or (Test-Placeholder $expectedValue)) { continue }
        if ($actual[$key] -cne $expectedValue) { $warnings.Add("${App}: '$key' differs from the configuration") }
    }
    if ($missing) { $failures.Add("${App}: missing $($missing -join ', '); a manual run with applySettings (or the 'settings' component) is needed") }
    foreach ($name in $actual.Keys) {
        if ($RetiredSettings -contains $name) { $warnings.Add("${App}: retired setting '$name' is still present; the next 'settings' run deletes it") }
    }
    [pscustomobject]@{ Failures = @($failures); Warnings = @($warnings) }
}

# --------------------------------------------------------------------------------------------
# Infrastructure plan (pure: decisions only, from the existing-resource state)
# --------------------------------------------------------------------------------------------

function Get-ResourceTags($Config) {
    [ordered]@{ 'eim-workload' = "$($Config.workload)"; 'eim-client' = "$($Config.client)"; 'eim-scope' = "$($Config.scope)"; 'eim-environment' = "$($Config.environment)" }
}

function New-InfraStep([string]$Kind, [string]$Label, [object[]]$Arguments, $Body) {
    [pscustomobject]@{ Kind = $Kind; Label = $Label; Args = @($Arguments | ForEach-Object { "$_" }); Body = $Body }
}

function Get-StorageSteps($Config, $Names, [string]$Name, [bool]$Exists, [string[]]$TagArgs) {
    if ($Exists) { return }
    New-InfraStep az "create storage $Name" (@('storage', 'account', 'create', '-g', $Names.ResourceGroup, '-n', $Name, '-l', $Config.location,
            '--sku', 'Standard_LRS', '--kind', 'StorageV2', '--min-tls-version', 'TLS1_2', '--allow-blob-public-access', 'false', '--https-only', 'true',
            '--tags') + $TagArgs + @('-o', 'none'))
}

function Get-InsightsSteps($Config, $Names, [string]$Name, [bool]$Exists) {
    if ($Exists) { return }
    # Created through the generic resource API: 'az monitor app-insights' is an optional extension that can prompt to install.
    # WorkspaceResourceId is filled in when the step runs, from the Log Analytics workspace.
    New-InfraStep insights "create Application Insights $Name (workspace-based, $($Names.LogAnalytics))" @('resource', 'create', '-g', $Names.ResourceGroup, '-n', $Name,
        '--resource-type', 'Microsoft.Insights/components', '--is-full-object') @{
        location   = $Config.location
        kind       = 'web'
        tags       = Get-ResourceTags $Config
        properties = @{ Application_Type = 'web'; WorkspaceResourceId = $null }
    }
}

function Get-FunctionAppSteps($Config, $Names, [string]$App, [string]$Storage, [bool]$Exists, [string[]]$TagArgs) {
    $rg = $Names.ResourceGroup
    if (-not $Exists) {
        New-InfraStep az "create Function App $App on $($Names.AppServicePlan)" (@('functionapp', 'create', '-g', $rg, '-n', $App, '--plan', $Names.AppServicePlan,
                '--storage-account', $Storage, '--runtime', 'dotnet-isolated', '--runtime-version', '10', '--functions-version', '4', '--os-type', 'Linux',
                '--disable-app-insights', 'true', '--tags') + $TagArgs + @('-o', 'none'))
    }
    # Converged on every run, so a property changed by hand is put back.
    New-InfraStep az "$App`: Always On, FTPS disabled, TLS 1.2" @('functionapp', 'config', 'set', '-g', $rg, '-n', $App,
        '--always-on', 'true', '--ftps-state', 'Disabled', '--min-tls-version', '1.2', '--http20-enabled', 'false', '-o', 'none')
    New-InfraStep az "$App`: HTTPS only" @('functionapp', 'update', '-g', $rg, '-n', $App, '--set', 'httpsOnly=true', '-o', 'none')
    New-InfraStep runtime "$App`: runtime $FunctionRuntime" @($App)
}

# $Existing: Plan, LogAnalytics, ServiceBusNamespace, GatewayStorage, GatewayInsights, GatewayApp, DashboardApi,
# StaticWebApp, AutomationStorage, AutomationInsights, AutomationApp (booleans) and Queues (queue name -> boolean).
function Get-InfraPlan($Config, $Names, $Existing) {
    $rg = $Names.ResourceGroup
    $location = $Config.location
    $tags = Get-ResourceTags $Config
    $tagArgs = @($tags.Keys | ForEach-Object { "$_=$($tags[$_])" })

    if ($Config.scope -eq 'shared') {
        if (-not $Existing.Plan) {
            $sku = "$($Config.appServicePlan.sku)".ToUpperInvariant()
            New-InfraStep az "create App Service plan $($Names.AppServicePlan) ($sku, Linux)" (@('appservice', 'plan', 'create', '-g', $rg, '-n', $Names.AppServicePlan,
                    '-l', $location, '--is-linux', '--sku', $sku, '--tags') + $tagArgs + @('-o', 'none'))
        }
        if (-not $Existing.LogAnalytics) {
            # Generic resource API rather than 'az monitor log-analytics', for the same reason as Insights.
            New-InfraStep json "create Log Analytics workspace $($Names.LogAnalytics)" @('resource', 'create', '-g', $rg, '-n', $Names.LogAnalytics,
                '--resource-type', 'Microsoft.OperationalInsights/workspaces', '--is-full-object') @{
                location   = $location
                tags       = $tags
                properties = @{ sku = @{ name = 'PerGB2018' }; retentionInDays = 30 }
            }
        }
        Get-StorageSteps $Config $Names $Names.GatewayStorage ([bool]$Existing.GatewayStorage) $tagArgs
        Get-InsightsSteps $Config $Names $Names.GatewayInsights ([bool]$Existing.GatewayInsights)
        Get-FunctionAppSteps $Config $Names $Names.GatewayApp $Names.GatewayStorage ([bool]$Existing.GatewayApp) $tagArgs

        # The dashboard is deployed in place: created only when missing, never recreated, re-planned or reconfigured.
        if (-not $Existing.DashboardApi) {
            New-InfraStep az "create dashboard API $($Names.DashboardApi) on $($Names.AppServicePlan)" (@('webapp', 'create', '-g', $rg, '-n', $Names.DashboardApi,
                    '--plan', $Names.AppServicePlan, '--runtime', 'DOTNETCORE:10.0', '--tags') + $tagArgs + @('-o', 'none'))
            New-InfraStep az "$($Names.DashboardApi): Always On, FTPS disabled, TLS 1.2" @('webapp', 'config', 'set', '-g', $rg, '-n', $Names.DashboardApi,
                '--always-on', 'true', '--ftps-state', 'Disabled', '--min-tls-version', '1.2', '-o', 'none')
            New-InfraStep az "$($Names.DashboardApi): HTTPS only" @('webapp', 'update', '-g', $rg, '-n', $Names.DashboardApi, '--https-only', 'true', '-o', 'none')
        }
        if (-not $Existing.StaticWebApp) {
            New-InfraStep az "create static web app $($Names.StaticWebApp) (Standard)" (@('staticwebapp', 'create', '-g', $rg, '-n', $Names.StaticWebApp,
                    '-l', $location, '--sku', 'Standard', '--tags') + $tagArgs + @('-o', 'none'))
        }
        return
    }

    # Environment scope: the shared resources must exist already (infra for the shared configuration).
    foreach ($shared in @(
            @{ Exists = $Existing.Plan; Label = "App Service plan $($Names.AppServicePlan)" }
            @{ Exists = $Existing.LogAnalytics; Label = "Log Analytics workspace $($Names.LogAnalytics)" }
            @{ Exists = $Existing.ServiceBusNamespace; Label = "Service Bus namespace $($Names.ServiceBusNamespace)" })) {
        if (-not $shared.Exists) { New-InfraStep require "$($shared.Label) does not exist; run 'infra' for the shared configuration '$($Config.sharedSuffix)' first" @() }
    }

    $queues = Get-QueueNames $Names.QueuePrefix
    foreach ($queue in $queues.Values) {
        if ($Existing.Queues -and $Existing.Queues[$queue]) { continue }
        New-InfraStep az "create queue $queue" @('servicebus', 'queue', 'create', '-g', $rg, '--namespace-name', $Names.ServiceBusNamespace, '-n', $queue,
            '--max-size', '1024', '--default-message-time-to-live', 'P14D', '--lock-duration', 'PT60S', '--max-delivery-count', '10',
            '--enable-dead-lettering-on-message-expiration', 'true', '--enable-partitioning', 'false', '-o', 'none')
    }
    Get-StorageSteps $Config $Names $Names.AutomationStorage ([bool]$Existing.AutomationStorage) $tagArgs
    Get-InsightsSteps $Config $Names $Names.AutomationInsights ([bool]$Existing.AutomationInsights)
    Get-FunctionAppSteps $Config $Names $Names.AutomationApp $Names.AutomationStorage ([bool]$Existing.AutomationApp) $tagArgs
}

# --------------------------------------------------------------------------------------------
# Cutover and remove decisions (pure)
# --------------------------------------------------------------------------------------------

# $LegacyStates: function -> $true when enabled, $false when disabled ($null for the whole map when there is no
# legacy app). Mirrors the legacy app; without one, enables everything except keepDisabled.
function Get-CutoverPlan($LegacyStates, [string[]]$GatedFunctions, [string[]]$KeepDisabled) {
    foreach ($function in $GatedFunctions) {
        $enabled = if ($null -ne $LegacyStates -and $LegacyStates.Contains($function) -and $null -ne $LegacyStates[$function]) {
            [bool]$LegacyStates[$function]
        } else { $KeepDisabled -notcontains $function }
        [pscustomobject]@{ Function = $function; Enabled = $enabled }
    }
}

function Test-ParityIgnored([string]$Name, [string[]]$RetiredSettings) {
    ($RetiredSettings -contains $Name) -or [bool]($ParityIgnoredSettings | Where-Object { $Name -like $_ })
}

# Names only in the messages, never values.
function Test-CutoverParity($Legacy, $New, [string[]]$RetiredSettings) {
    $legacyMap = ConvertTo-SettingMap $Legacy
    $newMap = ConvertTo-SettingMap $New
    $failures = [System.Collections.Generic.List[string]]::new()
    $warnings = [System.Collections.Generic.List[string]]::new()

    foreach ($name in @($legacyMap.Keys | Sort-Object)) {
        if (Test-ParityIgnored $name $RetiredSettings) { continue }
        if (-not $newMap.ContainsKey($name)) { $failures.Add("'$name' is on the legacy app but missing on the new app"); continue }
        if ($legacyMap[$name] -ceq $newMap[$name]) { continue }
        if ($ParityCriticalSettings -contains $name) { $failures.Add("'$name' differs between the legacy and the new app; it must match (same database, namespace and queues)") }
        else { $warnings.Add("'$name' differs between the legacy and the new app") }
    }
    [pscustomobject]@{ Failures = @($failures); Warnings = @($warnings) }
}

# Keys a consumer may hold: every named key except the master key and, unless asked for, the generated 'default'.
function Select-CopyableKeys($Keys, [switch]$IncludeDefault) {
    $selected = [ordered]@{}
    if ($null -eq $Keys) { return $selected }
    $pairs = if ($Keys -is [System.Collections.IDictionary]) { $Keys.GetEnumerator() | ForEach-Object { @{ Name = $_.Key; Value = $_.Value } } }
    else { $Keys.PSObject.Properties | ForEach-Object { @{ Name = $_.Name; Value = $_.Value } } }
    foreach ($pair in $pairs) {
        if ($pair.Name -eq '_master') { continue }
        if ($pair.Name -eq 'default' -and -not $IncludeDefault) { continue }
        $selected[$pair.Name] = "$($pair.Value)"
    }
    $selected
}

# $Entries: Name, Type, Exists, HostedApps (plans only). Returns Refusals (abort the run), Proceed (delete, in
# order: sites, storage and Insights, plans) and Kept (left in place, with the reason).
function Test-RemoveAllowed {
    param(
        [string]$Environment,
        [object[]]$Entries,
        [string[]]$NamesInUse,
        [string[]]$EnabledLegacyFunctions,
        [bool]$IsPipeline,
        [bool]$Preview,
        [bool]$MarkerExists,
        [bool]$ConfirmDisabled,
        [bool]$CheckConfirmation,
        [AllowNull()][AllowEmptyString()][string]$Confirmation
    )
    $refusals = [System.Collections.Generic.List[string]]::new()
    $kept = [System.Collections.Generic.List[object]]::new()
    $enabledLegacy = @($EnabledLegacyFunctions | Where-Object { $_ })

    if ($IsPipeline) { $refusals.Add("remove never runs in a pipeline (TF_BUILD is set); run it from an operator machine") }
    if ($ConfirmDisabled) { $refusals.Add("-Confirm:`$false is not accepted for remove") }
    if (-not $Preview -and -not $MarkerExists) { $refusals.Add("run 'remove -WhatIf' for '$Environment' first (today, on this machine)") }
    if (-not $Preview -and $CheckConfirmation -and $Confirmation -cne $Environment) { $refusals.Add("the confirmation did not match '$Environment'") }
    if ($enabledLegacy.Count -gt 0) {
        $refusals.Add("the legacy app still has enabled gated functions: $($enabledLegacy -join ', '); run 'cutover' first")
    }

    $candidates = [System.Collections.Generic.List[object]]::new()
    foreach ($entry in @($Entries | Where-Object { $_ })) {
        if ($RemovableTypes -notcontains $entry.Type) {
            $refusals.Add("'$($entry.Name)' has type '$($entry.Type)', which remove never deletes (allowed: $($RemovableTypes -join ', '))")
            continue
        }
        if ($NamesInUse -contains $entry.Name) {
            $refusals.Add("'$($entry.Name)' is a name in use by the current deployment; remove refuses it")
            continue
        }
        if (-not $entry.Exists) { $kept.Add([pscustomobject]@{ Name = $entry.Name; Type = $entry.Type; Reason = 'not found (already removed)' }); continue }
        $candidates.Add($entry)
    }

    $deletedSites = @($candidates | Where-Object Type -eq 'Microsoft.Web/sites' | ForEach-Object { $_.Name })
    $proceed = [System.Collections.Generic.List[object]]::new()
    foreach ($type in 'Microsoft.Web/sites', 'Microsoft.Storage/storageAccounts', 'Microsoft.Insights/components', 'Microsoft.Web/serverfarms') {
        foreach ($entry in @($candidates | Where-Object Type -eq $type)) {
            if ($type -eq 'Microsoft.Web/serverfarms') {
                $remaining = @($entry.HostedApps | Where-Object { $_ -and $deletedSites -notcontains $_ })
                if ($remaining.Count -gt 0) {
                    $kept.Add([pscustomobject]@{ Name = $entry.Name; Type = $type; Reason = "still hosts $($remaining -join ', ')" })
                    continue
                }
            }
            $proceed.Add($entry)
        }
    }
    [pscustomobject]@{ Refusals = @($refusals); Proceed = @($proceed); Kept = @($kept) }
}

function ConvertTo-LockSeconds([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return 60 }
    try {
        $span = if ($Value.Trim() -match '^P') { [System.Xml.XmlConvert]::ToTimeSpan($Value.Trim()) } else { [TimeSpan]::Parse($Value.Trim()) }
        [int][Math]::Ceiling($span.TotalSeconds)
    }
    catch { 60 }
}

function Get-FirewallRuleName([string]$BuildId, [string]$UserName) {
    if (-not [string]::IsNullOrWhiteSpace($BuildId)) { return "eim-deploy-$BuildId" }
    "AllowDev-$UserName"
}

# The server name only; the error never repeats the connection string.
function Get-SqlServerFromConnection([string]$ConnectionString) {
    $match = [regex]::Match("$ConnectionString", '(?:Data Source|Server)\s*=\s*(?:tcp:)?([^.,;]+)\.database\.windows\.net', 'IgnoreCase')
    if (-not $match.Success) { throw "Could not read the SQL server name from EIM_DEPLOY_EIMDB; expected 'Server=tcp:<server>.database.windows.net' or 'Data Source=tcp:<server>...'." }
    $match.Groups[1].Value
}

# --------------------------------------------------------------------------------------------
# Secrets and package
# --------------------------------------------------------------------------------------------

$script:Secrets = @{ EIMDB = $null; ServiceBus = $null }

function Test-Interactive {
    -not $env:TF_BUILD -and [Environment]::UserInteractive -and -not ([Environment]::GetCommandLineArgs() -match '^-NonI')
}

# Returns the names still missing after the environment and, when interactive, a hidden prompt.
function Resolve-DeploySecrets([string[]]$Required) {
    $map = @{ EIM_DEPLOY_EIMDB = 'EIMDB'; EIM_DEPLOY_SERVICEBUS = 'ServiceBus' }
    $missing = @()
    foreach ($variable in $Required) {
        $key = $map[$variable]
        if (-not [string]::IsNullOrWhiteSpace($script:Secrets[$key])) { continue }
        $value = [Environment]::GetEnvironmentVariable($variable)
        # A pipeline variable missing from the group arrives as its literal '$(name)'.
        if ("$value" -match '^\$\(') { $value = $null }
        if ([string]::IsNullOrWhiteSpace($value) -and (Test-Interactive)) {
            $secure = Read-Host "$variable (input hidden)" -AsSecureString
            $value = [System.Net.NetworkCredential]::new('', $secure).Password
        }
        if ([string]::IsNullOrWhiteSpace($value)) { $missing += $variable; continue }
        $script:Secrets[$key] = $value
    }
    $missing
}

function Get-PackageArtifact([string]$RelativePath) {
    $path = Join-Path $PackageArtifacts $RelativePath
    if (-not (Test-Path $path)) {
        throw "The package has no 'artifacts/$RelativePath' (looked in '$PackageArtifacts'). Build one with New-DeploymentPackage.ps1 and run from it, or pass -PackagePath; this script never runs dotnet publish."
    }
    $path
}

function Get-PackageManifest {
    $path = Join-Path $PackageArtifacts 'manifest.json'
    if (Test-Path $path) { Get-Content $path -Raw | ConvertFrom-Json }
}

# --------------------------------------------------------------------------------------------
# Shared building blocks (Azure)
# --------------------------------------------------------------------------------------------

function Assert-AzureCli($Config) {
    if (-not (Test-Az account show)) { throw "Not logged in to Azure. Run 'az login' first." }
    if (-not [string]::IsNullOrWhiteSpace($Config.subscriptionId) -and -not (Test-Placeholder $Config.subscriptionId)) {
        Invoke-Az account set --subscription $Config.subscriptionId | Out-Null
    }
    else { Write-DeployWarning "subscriptionId is not filled in; using the current Azure CLI subscription." }
    Write-Host "Subscription: $(Invoke-Az account show --query name -o tsv)"
}

function Test-WebApp([string]$ResourceGroup, [string]$Name) {
    -not [string]::IsNullOrWhiteSpace($Name) -and (Test-Az webapp show -g $ResourceGroup -n $Name)
}

function Assert-WebApp([string]$ResourceGroup, [string]$Name) {
    if (-not (Test-WebApp $ResourceGroup $Name)) { throw "'$Name' does not exist in '$ResourceGroup'; run 'infra' (a manual run with deployInfra) first." }
}

function Get-InsightsConnection([string]$ResourceGroup, [string]$Name) {
    $value = "$(Invoke-Az resource show -g $ResourceGroup -n $Name --resource-type Microsoft.Insights/components --query properties.ConnectionString -o tsv)"
    if ([string]::IsNullOrWhiteSpace($value)) { throw "Could not read the connection string of Application Insights '$Name'; run 'infra' first." }
    $value
}

# Effective state per gated function: the function's isDisabled, else the AzureWebJobs.<fn>.Disabled setting.
# Returns function -> $true (enabled) / $false (disabled) / $null (not on this app).
function Get-EffectiveFunctionStates([string]$ResourceGroup, [string]$App, [string[]]$GatedFunctions) {
    $settings = ConvertTo-SettingMap (Get-AppSettingList $ResourceGroup $App)
    $listed = Invoke-AzProcess @('functionapp', 'function', 'list', '-g', $ResourceGroup, '-n', $App, '--query', '[].{name:name,isDisabled:isDisabled}', '-o', 'json')
    $functions = @{}
    if ($listed.ExitCode -eq 0 -and $listed.StdOut.Trim()) {
        foreach ($item in @($listed.StdOut | ConvertFrom-Json)) { $functions[("$($item.name)" -split '/')[-1]] = $item.isDisabled }
    }

    $states = [ordered]@{}
    foreach ($function in $GatedFunctions) {
        $setting = "AzureWebJobs.$function.Disabled"
        $states[$function] = if ($functions.ContainsKey($function) -and $null -ne $functions[$function]) { -not [bool]$functions[$function] }
        elseif ($settings.ContainsKey($setting)) { $settings[$setting] -ne 'true' -and $settings[$setting] -ne '1' }
        elseif ($functions.Count -gt 0) { $null }
        else { $true }
    }
    $states
}

function Get-GatedTriggerState([string]$ResourceGroup, [string]$App, [string[]]$GatedFunctions) {
    $settings = ConvertTo-SettingMap (Get-AppSettingList $ResourceGroup $App)
    foreach ($function in $GatedFunctions) {
        $name = "AzureWebJobs.$function.Disabled"
        [pscustomobject]@{ Function = $function; Disabled = if ($settings.ContainsKey($name)) { $settings[$name] } else { '(not set)' } }
    }
}

# Writes only the values that change, so a rerun changes nothing.
function Set-GatedTriggers([string]$ResourceGroup, [string]$App, $Plan) {
    $current = ConvertTo-SettingMap (Get-AppSettingList $ResourceGroup $App)
    $changes = [ordered]@{}
    foreach ($entry in $Plan) {
        $name = "AzureWebJobs.$($entry.Function).Disabled"
        $value = if ($entry.Enabled) { 'false' } else { 'true' }
        if (-not $current.ContainsKey($name) -or $current[$name] -ne $value) { $changes[$name] = $value }
    }
    if ($changes.Count -eq 0) { Write-Host "  $App`: gated triggers already as planned; nothing changed"; return }
    Set-AppSettingList $ResourceGroup $App $changes
    Write-Host "  $App`: $($changes.Count) gated trigger setting(s) changed"
}

# The host answers ServiceUnavailable while it starts (or when startup fails), so poll until Running or timeout.
function Wait-FunctionHostRunning([string]$SiteId, [int]$TimeoutSeconds = 300) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ($true) {
        $result = Invoke-AzProcess @('rest', '--method', 'get', '--url', "https://management.azure.com$SiteId/hostruntime/admin/host/status?api-version=2022-03-01", '--query', 'state', '-o', 'tsv')
        $state = if ($result.ExitCode -eq 0 -and $result.StdOut.Trim()) { $result.StdOut.Trim() } else { 'not responding' }
        if ($state -eq 'Running' -or (Get-Date) -ge $deadline) { return $state }
        Start-Sleep -Seconds 15
    }
}

function Wait-HttpResponse([string]$Url, [int]$TimeoutSeconds = 300, [int]$Below = 500) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ($true) {
        $status = try { [int](Invoke-WebRequest -Uri $Url -Method Get -TimeoutSec 30 -SkipHttpErrorCheck -ErrorAction Stop).StatusCode } catch { 0 }
        if (($status -gt 0 -and $status -lt $Below) -or (Get-Date) -ge $deadline) { return $status }
        Start-Sleep -Seconds 15
    }
}

# --------------------------------------------------------------------------------------------
# Components
# --------------------------------------------------------------------------------------------

function Invoke-PreflightComponent($Config, $Names, [string[]]$Selected) {
    $problems = [System.Collections.Generic.List[string]]::new()
    $rg = $Names.ResourceGroup

    foreach ($problem in @(Get-PlaceholderProblems $Config $Selected)) { $problems.Add($problem) }

    foreach ($variable in @(Resolve-DeploySecrets (Get-RequiredSecrets $Selected $Config.scope))) {
        $problems.Add("secret variable $variable is not set")
    }

    # Every configuration's names count, so a per-environment 'legacy' entry cannot name a shared resource either.
    $inUse = @(foreach ($name in (Get-ConfigurationNames $ConfigDirectory)) { Get-NamesInUse (Get-DeployConfig $name $ConfigDirectory) }) + @(Get-NamesInUse $Config) | Sort-Object -Unique
    foreach ($conflict in @(Get-LegacyNameConflicts (Get-LegacyNames $Config) $inUse)) { $problems.Add($conflict) }

    $artifacts = @(Get-RequiredArtifacts $Selected $Config)
    if ($artifacts.Count -gt 0 -and -not $InPackage) {
        $problems.Add("no package at '$PackageRoot' (artifacts/manifest.json missing); build one with New-DeploymentPackage.ps1 and pass -PackagePath")
    }
    elseif ($artifacts.Count -gt 0) {
        foreach ($artifact in $artifacts) {
            if (-not (Test-Path (Join-Path $PackageArtifacts $artifact))) { $problems.Add("the package has no 'artifacts/$artifact'") }
        }
    }
    $manifest = Get-PackageManifest
    if ($manifest) {
        foreach ($problem in @(Compare-GatedFunctions @($manifest.automationQueueAndTimerFunctions) @($Config.automation.gatedFunctions))) { $problems.Add($problem) }
    }

    if (-not (Test-Az group show -n $rg)) { $problems.Add("resource group '$rg' was not found or you have no access to it") }

    $codeApps = @{ gateway = $Names.GatewayApp; backend = $Names.DashboardApi; automation = $Names.AutomationApp }
    $deployed = @($CodeComponents | Where-Object { $Selected -contains $_ -and $codeApps.ContainsKey($_) })
    if ($deployed -and $Selected -notcontains 'settings') {
        # Secrets are compared by name only, so a code-only run needs none.
        $placeholderSecrets = @{ EIMDB = '<EIM_DEPLOY_EIMDB>'; ServiceBus = '<EIM_DEPLOY_SERVICEBUS>' }
        foreach ($component in $deployed) {
            $app = $codeApps[$component]
            if (-not (Test-WebApp $rg $app)) { $problems.Add("${app}: does not exist; run a manual run with deployInfra and applySettings"); continue }
            $current = Get-AppSettingList $rg $app
            $expected = switch ($component) {
                'gateway' { Get-GatewaySettings $Config $Names $placeholderSecrets '<insights>' }
                'backend' { Get-DashboardApiSettings $Config $Names $placeholderSecrets }
                'automation' {
                    $s = Get-AutomationSettings $Config $Names $placeholderSecrets '<insights>'
                    foreach ($function in $Config.automation.gatedFunctions) { $s["AzureWebJobs.$function.Disabled"] = 'true' }
                    $s
                }
            }
            $drift = Get-SettingsDrift $app $expected $current @($Config.automation.retiredSettings)
            foreach ($warning in $drift.Warnings) { Write-DeployWarning $warning }
            foreach ($failure in $drift.Failures) { $problems.Add($failure) }
        }
    }
    elseif ($deployed) { Write-Host "  settings comparison skipped: 'settings' runs in this deployment" }

    if ($problems.Count -gt 0) { throw "Pre-flight check failed; nothing was changed:`n  - $($problems -join "`n  - ")" }
    Write-Host "  passed"
}

function Get-ExistingResources($Config, $Names) {
    $rg = $Names.ResourceGroup
    $existing = @{
        Plan                = Test-Az appservice plan show -g $rg -n $Names.AppServicePlan
        LogAnalytics        = Test-Az resource show -g $rg -n $Names.LogAnalytics --resource-type Microsoft.OperationalInsights/workspaces
        ServiceBusNamespace = Test-Az servicebus namespace show -g $rg -n $Names.ServiceBusNamespace
        Queues              = @{}
    }
    if ($Config.scope -eq 'shared') {
        $existing.GatewayStorage = Test-Az storage account show -g $rg -n $Names.GatewayStorage
        $existing.GatewayInsights = Test-Az resource show -g $rg -n $Names.GatewayInsights --resource-type Microsoft.Insights/components
        $existing.GatewayApp = Test-Az functionapp show -g $rg -n $Names.GatewayApp
        $existing.DashboardApi = Test-Az webapp show -g $rg -n $Names.DashboardApi
        $existing.StaticWebApp = Test-Az staticwebapp show -g $rg -n $Names.StaticWebApp
    }
    else {
        foreach ($queue in (Get-QueueNames $Names.QueuePrefix).Values) {
            $existing.Queues[$queue] = $existing.ServiceBusNamespace -and (Test-Az servicebus queue show -g $rg --namespace-name $Names.ServiceBusNamespace -n $queue)
        }
        $existing.AutomationStorage = Test-Az storage account show -g $rg -n $Names.AutomationStorage
        $existing.AutomationInsights = Test-Az resource show -g $rg -n $Names.AutomationInsights --resource-type Microsoft.Insights/components
        $existing.AutomationApp = Test-Az functionapp show -g $rg -n $Names.AutomationApp
    }
    $existing
}

function Invoke-InfraComponent($Config, $Names) {
    $rg = $Names.ResourceGroup
    $steps = @(Get-InfraPlan $Config $Names (Get-ExistingResources $Config $Names))

    $missing = @($steps | Where-Object Kind -eq 'require')
    if ($missing) {
        if ($Preview) { foreach ($step in $missing) { Write-DeployWarning $step.Label } }
        else { throw "Infrastructure prerequisites missing; nothing was changed:`n  - $(($missing.Label) -join "`n  - ")" }
    }
    if ($Preview) {
        foreach ($step in @($steps | Where-Object Kind -ne 'require')) { Write-Host "  would $($step.Label)" }
        if (-not ($steps | Where-Object { $_.Label -like 'create *' })) { Write-Host "  every resource exists; only the Function App properties would be converged" }
        return
    }

    foreach ($step in $steps) {
        switch ($step.Kind) {
            'az' { $stepArgs = $step.Args; Invoke-Az @stepArgs | Out-Null }
            'json' {
                $file = Write-TempJson $step.Body
                try { $stepArgs = $step.Args + @('--properties', "@$file", '-o', 'none'); Invoke-Az @stepArgs | Out-Null }
                finally { Remove-Item $file -ErrorAction SilentlyContinue -WhatIf:$false }
            }
            'insights' {
                $workspaceId = "$(Invoke-Az resource show -g $rg -n $Names.LogAnalytics --resource-type Microsoft.OperationalInsights/workspaces --query id -o tsv)"
                if ([string]::IsNullOrWhiteSpace($workspaceId)) { throw "Log Analytics workspace '$($Names.LogAnalytics)' not found." }
                $step.Body.properties.WorkspaceResourceId = $workspaceId
                $file = Write-TempJson $step.Body
                try { $stepArgs = $step.Args + @('--properties', "@$file", '-o', 'none'); Invoke-Az @stepArgs | Out-Null }
                finally { Remove-Item $file -ErrorAction SilentlyContinue -WhatIf:$false }
            }
            'runtime' {
                $app = $step.Args[0]
                $runtime = "$(Invoke-Az functionapp config show -g $rg -n $app --query linuxFxVersion -o tsv)"
                if ($runtime -ine $FunctionRuntime) {
                    Invoke-Az functionapp config set -g $rg -n $app --linux-fx-version $FunctionRuntime -o none | Out-Null
                    Write-Host "  $app runtime was '$runtime'; set to $FunctionRuntime"
                }
            }
        }
        Write-Host "  $($step.Label)"
    }
}

function Write-SettingsPlan([string]$App, $Settings, [string[]]$Delete) {
    Write-Host "  $App`: $(if ($Preview) { 'would set' } else { 'set' }) $($Settings.Count) setting(s): $(@($Settings.Keys) -join ', ')"
    foreach ($name in $Delete) { Write-Host "  $App`: $(if ($Preview) { 'would delete' } else { 'deleted' }) setting '$name'" }
}

function Update-AppSettings([string]$ResourceGroup, [string]$App, $Settings, [string[]]$Delete, [string]$Kind) {
    if ($Preview) { Write-SettingsPlan $App $Settings $Delete; return }
    Set-AppSettingList $ResourceGroup $App $Settings
    if ($Delete) { Invoke-Az $Kind config appsettings delete -g $ResourceGroup -n $App --setting-names @Delete -o none | Out-Null }
    Write-SettingsPlan $App $Settings $Delete
}

function Invoke-SettingsComponent($Config, $Names) {
    $rg = $Names.ResourceGroup
    # Under -WhatIf on a first run the apps and Insights may not exist yet (infra only previews them).
    $target = {
        param([string]$App)
        if (Test-WebApp $rg $App) { return $true }
        if ($Preview) { Write-Host "  $App`: does not exist yet; its settings would be written after 'infra' creates it"; return $false }
        throw "'$App' does not exist in '$rg'; run 'infra' (a manual run with deployInfra) first."
    }
    $insights = {
        param([string]$Name)
        if ($Preview -and -not (Test-Az resource show -g $rg -n $Name --resource-type Microsoft.Insights/components)) { return '<created by infra>' }
        Get-InsightsConnection $rg $Name
    }

    if ($Config.scope -eq 'shared') {
        if (& $target $Names.GatewayApp) {
            $gateway = Get-GatewaySettings $Config $Names $script:Secrets (& $insights $Names.GatewayInsights)
            Update-AppSettings $rg $Names.GatewayApp $gateway @() 'functionapp'
        }

        # In place: the kept dashboard API keeps every setting name; only the gateway it points at changes.
        if (& $target $Names.DashboardApi) {
            $dashboard = Get-DashboardApiSettings $Config $Names $script:Secrets
            $stale = Get-StaleVerificationTargetSettings $dashboard (Get-AppSettingList $rg $Names.DashboardApi)
            Update-AppSettings $rg $Names.DashboardApi $dashboard $stale 'webapp'
        }
        return
    }

    $app = $Names.AutomationApp
    if (-not (& $target $app)) { return }
    $current = Get-AppSettingList $rg $app
    $settings = Get-AutomationSettings $Config $Names $script:Secrets (& $insights $Names.AutomationInsights)
    $gated = Get-GatedSettingsToWrite @($Config.automation.gatedFunctions) $current
    foreach ($key in $gated.Keys) { $settings[$key] = $gated[$key] }
    if ($gated.Count -gt 0) { Write-Host "  $app`: $($gated.Count) gated trigger(s) start disabled until 'cutover'" }
    $retired = Get-RetiredSettingsToDelete @($Config.automation.retiredSettings) $current
    Update-AppSettings $rg $app $settings $retired 'functionapp'
}

function Invoke-MyIpComponent($Config, $Names, [switch]$Remove) {
    # Preflight resolves the secret, but 'myipremove' on its own runs without preflight.
    if (@(Resolve-DeploySecrets @('EIM_DEPLOY_EIMDB')).Count -gt 0) { throw "secret variable EIM_DEPLOY_EIMDB is not set." }
    $server = Get-SqlServerFromConnection $script:Secrets.EIMDB
    if ($server -ne $Names.SqlServer) { throw "EIM_DEPLOY_EIMDB points at SQL server '$server' but names.sqlServer is '$($Names.SqlServer)'." }
    $rule = Get-FirewallRuleName $env:BUILD_BUILDID $env:USERNAME
    $rg = $Names.ResourceGroup

    if ($Remove) {
        if ($Preview) { Write-Host "  would delete firewall rule '$rule' on $server"; return }
        $result = Invoke-AzProcess @('sql', 'server', 'firewall-rule', 'delete', '-g', $rg, '-s', $server, '-n', $rule, '-o', 'none')
        if ($result.ExitCode -ne 0) { Write-DeployWarning "Could not delete firewall rule '$rule' on $server; delete it by hand if it exists." }
        else { Write-Host "  deleted firewall rule '$rule' on $server" }
        return
    }

    $ip = "$(Invoke-RestMethod -Uri 'https://api.ipify.org' -TimeoutSec 20)".Trim()
    if ($ip -notmatch '^\d{1,3}(\.\d{1,3}){3}$') { throw "Could not determine this machine's public IP address; got '$ip'." }
    if ($Preview) { Write-Host "  would allow $ip on $server as firewall rule '$rule'"; return }
    Invoke-Az sql server firewall-rule create -g $rg -s $server -n $rule --start-ip-address $ip --end-ip-address $ip -o none | Out-Null
    Write-Host "  $server now allows $ip via '$rule'"
    Write-Host "  remove it when done: -Component myipremove"
}

function Invoke-SchemaComponent($Config, $Names) {
    $migrator = Get-PackageArtifact 'dbmigrator/ARM.Integration.DbMigrator.exe'
    $arguments = @('--timeout', '1800')
    if ($Preview) { $arguments += '--whatif' }
    # Only in the child process environment, so the connection string never reaches a command line.
    $exitCode = (Invoke-Native $migrator $arguments -Environment @{ EIMDB_MIGRATOR_CONNECTION = $script:Secrets.EIMDB }).ExitCode
    if ($exitCode -eq 0) { return }
    $message = "Migration $(if ($Preview) { 'preview ' })failed with exit code $exitCode. If it could not connect, run '-Component myip' first."
    if ($Preview) { Write-DeployWarning $message } else { throw $message }
}

function Invoke-FunctionDeploy($Config, $Names, [string]$App, [string]$Artifact) {
    $rg = $Names.ResourceGroup
    $zip = Get-PackageArtifact $Artifact
    if ($Preview) { Write-Host "  would stop $App, zip-deploy $Artifact, start it and sync its triggers"; return }
    Assert-WebApp $rg $App

    # Zip deploy under a running worker left half-replaced assemblies ("Bad IL format"); Service Bus holds messages meanwhile.
    Invoke-Az functionapp stop -g $rg -n $App -o none | Out-Null
    try {
        try { Invoke-Az functionapp deployment source config-zip -g $rg -n $App --src $zip --timeout 900 -o none | Out-Null }
        catch { throw "Zip deploy of $Artifact to '$App' failed; the app is being started again. $($_.Exception.Message)" }
    }
    finally {
        Invoke-Az functionapp start -g $rg -n $App -o none | Out-Null
    }
    # Otherwise the portal and 'verify' can keep listing the functions from before the deploy.
    # The host answers ServiceUnavailable while it is still starting, so give it a few tries.
    $siteId = Get-SiteId $rg $App
    for ($attempt = 1; ; $attempt++) {
        try {
            Invoke-AzRest post "$siteId/syncfunctiontriggers" | Out-Null
            break
        }
        catch {
            if ($attempt -ge 6) { throw "Trigger sync on '$App' failed after 6 attempts. $($_.Exception.Message)" }
            Write-Host "  host still starting; retrying trigger sync in 20s ($attempt/5)"
            Start-Sleep -Seconds 20
        }
    }
    Write-Host "  deployed $Artifact to https://$(Invoke-Az functionapp show -g $rg -n $App --query defaultHostName -o tsv)"
}

function Invoke-AutomationComponent($Config, $Names) {
    $rg = $Names.ResourceGroup
    $app = $Names.AutomationApp
    if (Test-WebApp $rg $app) {
        $missing = Get-MissingGatedSettings @($Config.automation.gatedFunctions) (Get-AppSettingList $rg $app)
        if ($missing.Count -gt 0) {
            $message = "Refusing to deploy to '$app': it lacks $($missing -join ', '). Without them the new app would start consuming the legacy app's queues; run 'settings' first."
            if (-not $Preview) { throw $message }
            Write-DeployWarning $message
        }
    }
    elseif (-not $Preview) { throw "'$app' does not exist; run 'infra' and 'settings' first." }
    Invoke-FunctionDeploy $Config $Names $app 'wilson-automation.zip'
}

function Invoke-GatewayComponent($Config, $Names) {
    Invoke-FunctionDeploy $Config $Names $Names.GatewayApp 'gateway.zip'
}

function Invoke-BackendComponent($Config, $Names) {
    $rg = $Names.ResourceGroup
    $zip = Get-PackageArtifact 'dashboard-api.zip'
    if ($Preview) { Write-Host "  would zip-deploy dashboard-api.zip to $($Names.DashboardApi) (in place)"; return }
    Assert-WebApp $rg $Names.DashboardApi
    Invoke-Az webapp deploy -g $rg -n $Names.DashboardApi --src-path $zip --type zip -o none | Out-Null
    Write-Host "  deployed dashboard API to https://$(Invoke-Az webapp show -g $rg -n $Names.DashboardApi --query defaultHostName -o tsv)"
}

# The Static Web Apps CLI needs Node.js; ARM zipdeploy is not an option (405 Method Not Allowed for these sites).
function Publish-StaticWebApp($Names, [string]$DistDir) {
    if (-not (Get-Command npx -ErrorAction SilentlyContinue)) { throw "The frontend step needs Node.js (npx) on this machine. Install Node.js LTS and open a new terminal." }
    $token = "$(Invoke-Az staticwebapp secrets list -g $Names.ResourceGroup -n $Names.StaticWebApp --query properties.apiKey -o tsv)"
    if ([string]::IsNullOrWhiteSpace($token)) { throw "Could not read the deployment token of '$($Names.StaticWebApp)'." }

    # Passed through the environment so the token never appears on a command line.
    $deployExit = Invoke-Tool 'npx' @('--yes', '@azure/static-web-apps-cli', 'deploy', $DistDir, '--env', 'production') @{ SWA_CLI_DEPLOYMENT_TOKEN = $token }
    if ($deployExit -ne 0) { throw "SPA deploy to '$($Names.StaticWebApp)' failed." }
}

function Invoke-FrontendComponent($Config, $Names) {
    $rg = $Names.ResourceGroup
    $artifact = "dashboard-frontend.$($Config.environment).zip"
    $zip = Get-PackageArtifact $artifact
    if ($Preview) { Write-Host "  would link $($Names.DashboardApi) as the /api backend and deploy $artifact to $($Names.StaticWebApp) (in place)"; return }
    if (-not (Test-Az staticwebapp show -g $rg -n $Names.StaticWebApp)) { throw "Static web app '$($Names.StaticWebApp)' does not exist; run 'infra' first." }

    # Re-checked every run; normally a no-op, as the existing app is already linked.
    $apiId = Get-SiteId $rg $Names.DashboardApi
    $linked = try { @(Invoke-AzJson staticwebapp backends show -g $rg -n $Names.StaticWebApp) | Where-Object { $_.backendResourceId -eq $apiId } } catch { $null }
    if (-not $linked) {
        Invoke-Az staticwebapp backends link -g $rg -n $Names.StaticWebApp --backend-resource-id $apiId --backend-region $Config.location -o none | Out-Null
        Write-Host "  linked $($Names.DashboardApi) as the /api backend"
    }

    $distDir = Join-Path ([IO.Path]::GetTempPath()) "wtc-deploy-spa-$([guid]::NewGuid())"
    Expand-Archive -Path $zip -DestinationPath $distDir -Force
    try { Publish-StaticWebApp $Names $distDir }
    finally { Remove-Item $distDir -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Host "  frontend: https://$(Invoke-Az staticwebapp show -g $rg -n $Names.StaticWebApp --query defaultHostname -o tsv) (hostname unchanged)"
}

function Write-AppReport([string]$ResourceGroup, [string]$App, [string]$Label) {
    if (-not (Test-WebApp $ResourceGroup $App)) { Write-Host "  $App ($Label): not found"; return }
    $site = Invoke-AzJson webapp show -g $ResourceGroup -n $App --query '{host:defaultHostName,state:state,plan:serverFarmId,outbound:possibleOutboundIpAddresses}'
    $alwaysOn = "$(Invoke-Az webapp config show -g $ResourceGroup -n $App --query alwaysOn -o tsv)"
    Write-Host "  $App ($Label)"
    Write-Host "    host       https://$($site.host)  state: $($site.state)  alwaysOn: $alwaysOn"
    Write-Host "    plan       $(("$($site.plan)" -split '/')[-1])"
    Write-Host "    outbound   $($site.outbound)"
}

function Write-GatedReport([string]$ResourceGroup, [string]$App, [string[]]$GatedFunctions) {
    if (-not (Test-WebApp $ResourceGroup $App)) { return }
    Write-Host "    gated triggers (AzureWebJobs.<fn>.Disabled)"
    foreach ($state in @(Get-GatedTriggerState $ResourceGroup $App $GatedFunctions)) { Write-Host ("      {0,-40} {1}" -f $state.Function, $state.Disabled) }
}

# Report only: never fails the run.
function Invoke-VerifyComponent($Config, $Names) {
    $rg = $Names.ResourceGroup
    if ($Preview) { Write-Host "  would report host, state, Always On, plan, outbound IPs and trigger states"; return }
    $gated = @($Config.automation.gatedFunctions)
    try {
        if ($Config.scope -eq 'shared') {
            Write-AppReport $rg $Names.GatewayApp 'gateway'
            if ($Config.legacy.gatewayApp) { Write-AppReport $rg $Config.legacy.gatewayApp 'legacy gateway' }
            Write-AppReport $rg $Names.DashboardApi 'dashboard API, shared by every environment (kept name)'
            if (Test-Az staticwebapp show -g $rg -n $Names.StaticWebApp) {
                $swaHost = "$(Invoke-Az staticwebapp show -g $rg -n $Names.StaticWebApp --query defaultHostname -o tsv)"
                $backend = try { @(Invoke-AzJson staticwebapp backends show -g $rg -n $Names.StaticWebApp) | ForEach-Object { ("$($_.backendResourceId)" -split '/')[-1] } } catch { '(none)' }
                Write-Host "  $($Names.StaticWebApp) (dashboard SPA, shared by every environment (kept name))"
                Write-Host "    host       https://$swaHost  linked backend: $($backend -join ', ')"
            }
            else { Write-Host "  $($Names.StaticWebApp): not found" }
        }
        else {
            Write-AppReport $rg $Names.AutomationApp 'Automation'
            Write-GatedReport $rg $Names.AutomationApp $gated
            if ($Config.legacy.automationApp) {
                Write-AppReport $rg $Config.legacy.automationApp 'legacy Automation'
                Write-GatedReport $rg $Config.legacy.automationApp $gated
            }
        }
    }
    catch { Write-DeployWarning "verify could not complete: $($_.Exception.Message)" }
}

function Invoke-HealthComponent($Config, $Names) {
    $rg = $Names.ResourceGroup
    if ($Preview) { Write-Host "  would wait for the Function App hosts to report Running and check the dashboard API and SPA"; return }

    $problems = [System.Collections.Generic.List[string]]::new()
    $functionApps = if ($Config.scope -eq 'shared') { @($Names.GatewayApp) } else { @($Names.AutomationApp) }
    foreach ($app in $functionApps) {
        if (-not (Test-WebApp $rg $app)) { $problems.Add("${app}: not found"); continue }
        $state = Wait-FunctionHostRunning (Get-SiteId $rg $app)
        if ($state -eq 'Running') { Write-Host "  $app`: host Running" }
        else { $problems.Add("${app}: host is '$state' after 5 minutes, not Running") }
    }

    if ($Config.scope -eq 'shared') {
        $api = $Names.DashboardApi
        if (-not (Test-WebApp $rg $api)) { $problems.Add("${api}: not found") }
        else {
            $status = Wait-HttpResponse "https://$(Invoke-Az webapp show -g $rg -n $api --query defaultHostName -o tsv)/"
            if ($status -gt 0 -and $status -lt 500) { Write-Host "  $api`: answered HTTP $status" }
            else { $problems.Add("${api}: no answer below HTTP 500 within 5 minutes (last: $(if ($status) { $status } else { 'no response' }))") }
        }

        $swa = $Names.StaticWebApp
        if (-not (Test-Az staticwebapp show -g $rg -n $swa)) { $problems.Add("${swa}: not found") }
        else {
            $status = Wait-HttpResponse "https://$(Invoke-Az staticwebapp show -g $rg -n $swa --query defaultHostname -o tsv)/" -Below 400
            if ($status -ge 200 -and $status -lt 400) { Write-Host "  $swa`: served / (HTTP $status)" }
            else { $problems.Add("${swa}: did not serve / within 5 minutes (last: $(if ($status) { $status } else { 'no response' }))") }
        }
    }

    if ($problems.Count -gt 0) { throw "Health check failed:`n  - $($problems -join "`n  - ")" }
    Write-Host "  all checks passed"
}

function Get-CutoverStatePath([string]$Environment) {
    Join-Path ([IO.Path]::GetTempPath()) "wtc-eim-cutover-$Environment.json"
}

function Get-QueueLockSeconds($Names) {
    $seconds = @(foreach ($queue in (Get-QueueNames $Names.QueuePrefix).Values) {
            $result = Invoke-AzProcess @('servicebus', 'queue', 'show', '-g', $Names.ResourceGroup, '--namespace-name', $Names.ServiceBusNamespace, '-n', $queue, '--query', 'lockDuration', '-o', 'tsv')
            if ($result.ExitCode -eq 0) { ConvertTo-LockSeconds $result.StdOut } else { 60 }
        })
    if ($seconds.Count -eq 0) { return 60 }
    [int]($seconds | Measure-Object -Maximum).Maximum
}

function Wait-GatedDisabled([string]$ResourceGroup, [string]$App, [string[]]$GatedFunctions, [int]$TimeoutSeconds = 300) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ($true) {
        $states = Get-EffectiveFunctionStates $ResourceGroup $App $GatedFunctions
        $enabled = @($states.Keys | Where-Object { $states[$_] -eq $true })
        if ($enabled.Count -eq 0) { return }
        if ((Get-Date) -ge $deadline) { throw "'$App' still reports enabled after $TimeoutSeconds s: $($enabled -join ', ')." }
        Start-Sleep -Seconds 15
    }
}

function Write-CutoverTable([string]$ResourceGroup, [string[]]$Apps, [string[]]$GatedFunctions) {
    $columns = @($Apps | ForEach-Object { , @(Get-GatedTriggerState $ResourceGroup $_ $GatedFunctions) })
    Write-Host ("    {0,-40} {1}" -f 'function (Disabled)', ($Apps -join ' / '))
    for ($i = 0; $i -lt $GatedFunctions.Count; $i++) {
        Write-Host ("    {0,-40} {1}" -f $GatedFunctions[$i], (@($columns | ForEach-Object { $_[$i].Disabled }) -join ' / '))
    }
}

function Invoke-CutoverComponent($Config, $Names, [bool]$Rollback) {
    if ($Config.scope -eq 'shared') {
        if ($Rollback) { throw "'rollback' applies to environment configurations; the legacy gateway keeps serving until 'remove', so consumers roll back by switching their base URL." }
        Invoke-GatewayKeyCopy $Config $Names
        return
    }

    $rg = $Names.ResourceGroup
    $gated = @($Config.automation.gatedFunctions)
    $newApp = $Names.AutomationApp
    $legacyApp = "$($Config.legacy.automationApp)"
    $rerun = "./Deploy-Wilson.ps1 -Environment $($Config.environment)"

    if (-not (Test-WebApp $rg $newApp)) { throw "'$newApp' does not exist; nothing was changed." }
    $missing = Get-MissingGatedSettings $gated (Get-AppSettingList $rg $newApp)
    if ($missing.Count -gt 0) { throw "'$newApp' lacks $($missing -join ', '); run 'settings' first. Nothing was changed." }
    $legacyExists = Test-WebApp $rg $legacyApp

    if ($Rollback -and -not $legacyExists) { throw "Rollback refused: the legacy app '$legacyApp' no longer exists. Nothing was changed." }
    if (-not $Rollback) {
        $state = Wait-FunctionHostRunning (Get-SiteId $rg $newApp)
        if ($state -ne 'Running') { throw "'$newApp' host is '$state', not Running; fix it before cutover. Nothing was changed." }
        if ($legacyExists) {
            $parity = Test-CutoverParity (Get-AppSettingList $rg $legacyApp) (Get-AppSettingList $rg $newApp) @($Config.automation.retiredSettings)
            foreach ($warning in $parity.Warnings) { Write-DeployWarning "${newApp}: $warning" }
            if ($parity.Failures.Count -gt 0) { throw "Cutover refused; nothing was changed:`n  - $($parity.Failures -join "`n  - ")" }
            Write-Host "  settings parity with $legacyApp`: passed"
        }
    }

    # No legacy app (a new environment): enable everything except keepDisabled.
    if (-not $Rollback -and -not $legacyExists) {
        $plan = @(Get-CutoverPlan $null $gated @($Config.automation.keepDisabled))
        Write-Host "  no legacy app; $(if ($Preview) { 'would enable' } else { 'enabling' }) on ${newApp}: $(@($plan | Where-Object Enabled | ForEach-Object Function) -join ', ')"
        if ($Preview -or -not $PSCmdlet.ShouldProcess($newApp, 'Enable the gated triggers')) { return }
        Set-GatedTriggers $rg $newApp $plan
        Write-CutoverTable $rg @($newApp) $gated
        return
    }

    $from = if ($Rollback) { $newApp } else { $legacyApp }
    $to = if ($Rollback) { $legacyApp } else { $newApp }
    $fromStates = Get-EffectiveFunctionStates $rg $from $gated
    $toStates = Get-EffectiveFunctionStates $rg $to $gated
    $fromEnabled = @($fromStates.Keys | Where-Object { $fromStates[$_] -eq $true })
    $toEnabled = @($toStates.Keys | Where-Object { $toStates[$_] -eq $true })
    $statePath = Get-CutoverStatePath $Config.environment

    if ($fromEnabled.Count -eq 0 -and $toEnabled.Count -gt 0) {
        Write-Host "  already done: $from has no enabled gated trigger and $to has $($toEnabled -join ', '); nothing changed"
        Write-CutoverTable $rg @($legacyApp, $newApp) $gated
        # A file left by a run killed after $to was set is stale now; see Move-GatedTriggers.
        if (-not $Preview -and (Test-Path $statePath)) {
            try { Remove-Item $statePath -Force -WhatIf:$false -ErrorAction Stop }
            catch { Write-DeployWarning "Could not delete '$statePath'; delete it by hand, or a later cutover or rollback with both apps disabled would re-enable the triggers it records." }
        }
        return
    }
    if ($fromEnabled.Count -eq 0 -and (Test-Path $statePath)) {
        # Both disabled after an interrupted run: use what was enabled before it.
        $recorded = @((Get-Content $statePath -Raw | ConvertFrom-Json).enabled)
        Write-Host "  both apps have every gated trigger disabled; using the states recorded before the interrupted run: $($recorded -join ', ')"
        $fromStates = [ordered]@{}
        foreach ($function in $gated) { $fromStates[$function] = $recorded -contains $function }
    }
    $plan = @(Get-CutoverPlan $fromStates $gated @($Config.automation.keepDisabled))
    $enable = @($plan | Where-Object Enabled | ForEach-Object Function)

    Write-Host "  $(if ($Preview) { 'would disable' } else { 'disabling' }) every gated trigger on $from, wait for the queue lock duration, then enable on ${to}: $(if ($enable) { $enable -join ', ' } else { '(none)' })"
    if ($Preview -or -not $PSCmdlet.ShouldProcess($to, "Move the gated triggers from $from")) { return }

    Move-GatedTriggers $Names $from $to $gated $plan $fromEnabled $statePath $Rollback $rerun
    Write-CutoverTable $rg @($legacyApp, $newApp) $gated
}

# Disables every gated trigger on $From, waits until they report disabled and for the queue lock duration, then
# applies $Plan on $To. $StatePath records what $From had enabled, so a rerun after an interruption (both apps
# disabled) can restore it. It is deleted once $To is set: a stale file would re-enable those triggers on a later
# rerun.
function Move-GatedTriggers($Names, [string]$From, [string]$To, [string[]]$GatedFunctions, $Plan, [string[]]$FromEnabled, [string]$StatePath, [bool]$Rollback, [string]$Rerun) {
    $rg = $Names.ResourceGroup
    if ($FromEnabled.Count -gt 0) { @{ enabled = $FromEnabled; from = $From } | ConvertTo-Json | Set-Content $StatePath -Encoding utf8NoBOM -WhatIf:$false }
    try {
        Set-GatedTriggers $rg $From @($GatedFunctions | ForEach-Object { [pscustomobject]@{ Function = $_; Enabled = $false } })
        Wait-GatedDisabled $rg $From $GatedFunctions
        $lock = Get-QueueLockSeconds $Names
        Write-Host "  $From reports every gated trigger disabled; waiting $lock s (queue lock duration)"
        Start-Sleep -Seconds $lock
        Set-GatedTriggers $rg $To $Plan
    }
    catch {
        throw ("$(if ($Rollback) { 'Rollback' } else { 'Cutover' }) failed: $($_.Exception.Message)`n" +
            "  No trigger was enabled on $To after the failure; both apps may now have the gated triggers disabled, and messages wait in the queues.`n" +
            "  Rerun:     $Rerun -Component $(if ($Rollback) { 'rollback' } else { 'cutover' })`n" +
            "  Roll back: $Rerun -Component $(if ($Rollback) { 'cutover' } else { 'rollback' })")
    }
    if (Test-Path $StatePath) {
        try { Remove-Item $StatePath -Force -WhatIf:$false -ErrorAction Stop }
        catch { Write-DeployWarning "Could not delete '$StatePath'; delete it by hand, or a later cutover or rollback with both apps disabled would re-enable the triggers it records." }
    }
}

# Copies every named host key and D365Data/D365Service function key, with the same value, so consumers only change the
# base URL. Values stay in memory and go to Azure through temp files; only key names are printed.
function Invoke-GatewayKeyCopy($Config, $Names) {
    $rg = $Names.ResourceGroup
    $legacy = "$($Config.legacy.gatewayApp)"
    $new = $Names.GatewayApp
    if (-not (Test-WebApp $rg $legacy)) { throw "The legacy gateway '$legacy' does not exist; there are no keys to copy." }
    Assert-WebApp $rg $new
    $legacyId = Get-SiteId $rg $legacy
    $newId = Get-SiteId $rg $new

    $sets = @(@{ Label = 'host'; List = 'host/default/listkeys'; Put = 'host/default/functionKeys'; Pick = { param($k) $k.functionKeys } })
    foreach ($function in $GatewayKeyFunctions) {
        $sets += @{ Label = $function; List = "functions/$function/listkeys"; Put = "functions/$function/keys"; Pick = { param($k) $k } }
    }

    foreach ($set in $sets) {
        $source = Select-CopyableKeys (& $set.Pick (Invoke-AzRest post "$legacyId/$($set.List)")) -IncludeDefault:$IncludeDefaultKeys
        $target = Select-CopyableKeys (& $set.Pick (Invoke-AzRest post "$newId/$($set.List)")) -IncludeDefault
        foreach ($name in @($source.Keys)) {
            if ($target.Contains($name) -and $target[$name] -ceq $source[$name]) { Write-Host "  $($set.Label) key '$name': already copied"; continue }
            if ($Preview) { Write-Host "  would copy $($set.Label) key '$name'"; continue }
            Invoke-AzRest put "$newId/$($set.Put)/$name" @{ properties = @{ name = $name; value = $source[$name] } } | Out-Null
            Write-Host "  copied $($set.Label) key '$name'"
        }
    }

    Write-Host "  next steps (see docs/deployment/resource-rename-cutover.md):"
    Write-Host "    - give consumers the new base URL $(Get-GatewayBaseUrl $Config $Names); their keys are unchanged"
    Write-Host "    - update GATEWAYCONSUMER.GATEWAYURL rows as each consumer confirms the switch"
    Write-Host "    - keep $legacy running until Insights shows no requests to it, then run 'remove'"
}

function Get-RemoveMarkerPath([string]$Environment) {
    Join-Path ([IO.Path]::GetTempPath()) "wtc-eim-remove-$Environment-$(Get-Date -Format yyyyMMdd).marker"
}

function Get-RemoveEntries($Config, $Names) {
    $rg = $Names.ResourceGroup
    $entries = [System.Collections.Generic.List[object]]::new()
    foreach ($resource in @($Config.legacy.resources | Where-Object { $_ })) {
        $entry = [pscustomobject]@{ Name = "$($resource.name)"; Type = "$($resource.type)"; Exists = $false; Id = $null; HostedApps = @() }
        $entries.Add($entry)
        if ($RemovableTypes -notcontains $entry.Type) { continue }
        $found = Invoke-AzProcess @('resource', 'show', '-g', $rg, '-n', $entry.Name, '--resource-type', $entry.Type, '-o', 'json')
        if ($found.ExitCode -ne 0) { continue }
        $item = $found.StdOut | ConvertFrom-Json
        $entry.Exists = $true
        $entry.Id = $item.id
        if ($entry.Type -eq 'Microsoft.Web/serverfarms') { $entry.HostedApps = @((Invoke-AzRest get "$($item.id)/sites").value | ForEach-Object { $_.name }) }
        # The legacy gateway's auto-created Flex plan, named only by the app's serverFarmId.
        if ($entry.Type -eq 'Microsoft.Web/sites' -and $resource.removePlan -eq $true -and $item.properties.serverFarmId) {
            $planId = "$($item.properties.serverFarmId)"
            $planName = ($planId -split '/')[-1]
            # Already listed by name (e.g. the old shared plan): keep the one entry.
            if (@($Config.legacy.resources | Where-Object { "$($_.name)" -eq $planName }).Count -gt 0) { continue }
            $entries.Add([pscustomobject]@{
                    Name = $planName; Type = 'Microsoft.Web/serverfarms'; Exists = $true; Id = $planId
                    HostedApps = @((Invoke-AzRest get "$planId/sites").value | ForEach-Object { $_.name })
                })
        }
    }
    @($entries)
}

function Invoke-RemoveComponent($Config, $Names) {
    $rg = $Names.ResourceGroup
    $environment = $Config.environment
    $marker = Get-RemoveMarkerPath $environment
    $common = @{
        Environment     = $environment
        IsPipeline      = [bool]$env:TF_BUILD
        Preview         = $Preview
        MarkerExists    = Test-Path $marker
        ConfirmDisabled = $ConfirmDisabled
    }
    # Fail fast on the guards that need no Azure call.
    $early = Test-RemoveAllowed @common -Entries @()
    if ($early.Refusals.Count -gt 0) { throw "remove refused; nothing was deleted:`n  - $($early.Refusals -join "`n  - ")" }

    $enabled = @()
    if ($Config.legacy.automationApp -and (Test-WebApp $rg $Config.legacy.automationApp)) {
        $states = Get-EffectiveFunctionStates $rg $Config.legacy.automationApp @($Config.automation.gatedFunctions)
        $enabled = @($states.Keys | Where-Object { $states[$_] -eq $true })
    }
    $inUse = @(foreach ($name in (Get-ConfigurationNames $ConfigDirectory)) { Get-NamesInUse (Get-DeployConfig $name $ConfigDirectory) }) + @(Get-NamesInUse $Config) | Sort-Object -Unique
    $entries = Get-RemoveEntries $Config $Names
    $decision = Test-RemoveAllowed @common -Entries $entries -NamesInUse $inUse -EnabledLegacyFunctions $enabled
    if ($decision.Refusals.Count -gt 0) { throw "remove refused; nothing was deleted:`n  - $($decision.Refusals -join "`n  - ")" }

    foreach ($entry in $decision.Proceed) { Write-Host ("  {0} {1,-36} {2}" -f $(if ($Preview) { 'would delete' } else { 'delete' }), $entry.Type, $entry.Name) }
    foreach ($entry in $decision.Kept) { Write-Host ("  keep         {0,-36} {1} ({2})" -f $entry.Type, $entry.Name, $entry.Reason) }
    if ($Preview) {
        Set-Content -Path $marker -Value (Get-Date -Format o) -WhatIf:$false
        Write-Host "  preview recorded; run 'remove' without -WhatIf today, on this machine, to delete"
        return
    }
    if ($decision.Proceed.Count -eq 0) { Write-Host "  nothing to delete"; return }

    $answer = Read-Host "Type the configuration name '$environment' to delete the $($decision.Proceed.Count) resource(s) listed above"
    $final = Test-RemoveAllowed @common -Entries $entries -NamesInUse $inUse -EnabledLegacyFunctions $enabled -CheckConfirmation $true -Confirmation "$answer".Trim()
    if ($final.Refusals.Count -gt 0) { throw "remove refused; nothing was deleted:`n  - $($final.Refusals -join "`n  - ")" }

    foreach ($entry in $final.Proceed) {
        Invoke-Az resource delete --ids $entry.Id -o none | Out-Null
        Write-Host "  deleted $($entry.Type) $($entry.Name)"
    }
    Remove-Item $marker -ErrorAction SilentlyContinue -WhatIf:$false
}

# --------------------------------------------------------------------------------------------
# Main (skipped when dot-sourced, e.g. by Test-DeployWilson.ps1)
# --------------------------------------------------------------------------------------------

if ($MyInvocation.InvocationName -ne '.') {
    if ($IncludeDefaultKeys -and $Component -notcontains 'cutover') { throw "-IncludeDefaultKeys only applies to 'cutover'." }
    $config = Get-DeployConfig $Environment $ConfigDirectory $ConfigOverridesFile $env:EIM_DEPLOY_CONFIG_OVERRIDES
    $names = Get-ResourceNames $config
    $selected = @(Resolve-Components $Component $config.scope $config.sharedSuffix)

    Write-Host "Configuration: $Environment (scope: $($config.scope))$(if ($InPackage) { "  package: $PackageRoot" })$(if ($Preview) { '  (preview - no changes)' })"
    Write-Host "Components: $($selected -join ', ')"
    $names.PSObject.Properties | ForEach-Object { Write-Host ("  {0,-20} {1}" -f $_.Name, $_.Value) }
    Assert-AzureCli $config

    foreach ($name in $selected) {
        Write-Host "== $name"
        switch ($name) {
            'preflight'  { Invoke-PreflightComponent $config $names $selected }
            'infra'      { Invoke-InfraComponent $config $names }
            'settings'   { Invoke-SettingsComponent $config $names }
            'myip'       { Invoke-MyIpComponent $config $names }
            'schema'     { Invoke-SchemaComponent $config $names }
            'myipremove' { Invoke-MyIpComponent $config $names -Remove }
            'gateway'    { Invoke-GatewayComponent $config $names }
            'backend'    { Invoke-BackendComponent $config $names }
            'frontend'   { Invoke-FrontendComponent $config $names }
            'automation' { Invoke-AutomationComponent $config $names }
            'verify'     { Invoke-VerifyComponent $config $names }
            'health'     { Invoke-HealthComponent $config $names }
            'cutover'    { Invoke-CutoverComponent $config $names $false }
            'rollback'   { Invoke-CutoverComponent $config $names $true }
            'remove'     { Invoke-RemoveComponent $config $names }
        }
    }
}
