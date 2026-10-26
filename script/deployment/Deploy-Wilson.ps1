<#
.SYNOPSIS
    Deploys the Wilson EIM apps (Automation, D365 F&SC gateway, dashboard) to Azure.

.DESCRIPTION
    One script for every deployment configuration. -Environment names a configuration file
    script/deployment/config/deploy.<name>.json (today bat and test for non-production, prd for production); any
    file with a matching name is accepted, and nothing here lists the environments or reads the ENVIRONMENT values
    in eimdb (DEV, DEV2, BAT, TEST, PRD are database rows, not deployment configurations). There is no dev
    configuration: development runs on a developer's machine.

    Configuration is merged in this order: deploy.base.json, deploy.<name>.json, -ConfigOverridesFile, then the
    JSON in EIM_DEPLOY_CONFIG_OVERRIDES. Objects merge key by key; lists and plain values are replaced.

    Owners and 'uses':
      owner   a configuration without "uses" (bat, prd). It owns its resource group, Service Bus namespace, Key
              Vault, SQL server and database, plan, Log Analytics, gateway, dashboard API and static web app, and
              its own queues and Automation app.
      uses    a configuration with "uses": "<owner>" (test uses bat). It borrows the owner's resources and owns only
              its queues and its Automation storage, Insights and Function App, on the owner's plan. Chains are not
              allowed, a production configuration never uses a non-production one, and it overrides no name it
              inherits.

    Names follow {type}-{workload}-{component}-{client}-{environment}, storage st{client}{workload}{short}{environment}.
    Any name can be overridden under "names" (a blank override falls back to the derived name); bat keeps the
    existing non-production resources that way. A configuration with only { "environment": "<name>" } derives a
    complete, isolated set of names.

    Missing resources are created and tagged eim-workload, eim-client and eim-environment; existing resources are
    used as found. Only resources with an eim-environment tag get the Function App baseline, the dashboard API site
    configuration and the SQL firewall rules. Every deployed app gets a system-assigned identity and secret 'get'
    on the Key Vault. The script deletes no Azure resource.

    There are no deployment secrets. The apps reach SQL with their managed identity; schema and sqlusers sign in
    with the deployer's Azure CLI identity, which must be in the SQL server's Entra admin group. The Service Bus
    connection is kept in the Key Vault secret 'eim-servicebus' and referenced from the app settings.

    Application code is taken from a package built by New-DeploymentPackage.ps1 (artifacts/manifest.json
    next to this script, or the folder given by -PackagePath). This script never runs dotnet publish.

    Components, always run in this order (a configuration with "uses" may run only those marked *):
      preflight * fails, changing nothing, on unfilled <...> values the requested components need, a wrong "uses",
                  a production name shared with a non-production configuration, a wrong automation.triggersEnabled
                  or keepDisabled, missing package artifacts, a package whose queue/timer functions differ from
                  automation.gatedFunctions, an owner resource or secret a "uses" configuration needs, a Key Vault
                  in RBAC mode, and (for schema or sqlusers) a SQL server without an Entra admin. When code is
                  deployed without 'settings', also fails on settings the config defines that an app lacks and
                  warns on differing values and on retired settings still present. Names only, never values. Added
                  automatically before any component that changes something, except 'myipremove' on its own, so
                  the firewall rule is removed even when preflight would fail.
      infra     * owner: resource group, Service Bus namespace, Key Vault, plan, Log Analytics, queues, gateway,
                  dashboard API, static web app and Automation, each created only when missing. uses: its queues
                  and Automation resources. Identities and Key Vault access policies for every deployed app.
      sql         owner: creates a missing SQL server (Entra-only, Entra admin from sql.entraAdmin) and database;
                  keeps the firewall rules on a server it manages; reports an existing server and warns on gaps.
      settings  * owner: the Key Vault secret eim-servicebus, the gateway, the dashboard API (Simulation__Enabled
                  from dashboard.simulationEnabled; the other Simulation__* settings only when it is true, and
                  deleted when it is false) and Automation. uses: Automation. The gated triggers
                  AzureWebJobs.<fn>.Disabled follow automation.triggersEnabled and keepDisabled on every run.
      myip      * allow this machine's (or agent's) public IP on the SQL server
      schema      database migrations with the packaged ARM.Integration.DbMigrator.exe (--whatif under -WhatIf)
      sqlusers  * a contained database user for each deployed app's identity, with db_datareader, db_datawriter
                  and EXECUTE; never drops a user
      myipremove* delete the rule 'myip' added
      gateway     zip-deploy gateway.zip to the gateway Function App
      backend     zip-deploy dashboard-api.zip to the dashboard API (in place)
      frontend    deploy the packaged SPA to the static web app (in place) and link the API as its backend
      automation* zip-deploy wilson-automation.zip; refused while any gated setting is missing
      verify    * report only: host, state, Always On, plan, outbound IPs, gated trigger states, Key Vault
                  references, SWA backend
      health    * fails unless each Function App host reports Running within 5 minutes, the dashboard API answers
                  below HTTP 500, the static web app serves its root and the Key Vault references resolve

    'all' runs every component except myip and myipremove.

.EXAMPLE
    ./script/deployment/Deploy-Wilson.ps1 -Environment bat -Component all -WhatIf -PackagePath artifacts/package/wtc-eim-deploy-local

.EXAMPLE
    From an extracted package:
    ./Deploy-Wilson.ps1 -Environment bat -Component gateway, backend, frontend, automation

.EXAMPLE
    ./Deploy-Wilson.ps1 -Environment test -Component myip, sqlusers, myipremove -WhatIf
#>
# After the help block: a #Requires line above it stops Get-Help from finding the help.
#Requires -Version 7.2
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    # The name of a configuration file script/deployment/config/deploy.<name>.json.
    [ValidatePattern('^[a-z0-9]+$')]
    [string]$Environment,

    [ValidateSet('preflight', 'infra', 'sql', 'settings', 'myip', 'schema', 'sqlusers', 'myipremove', 'gateway', 'backend', 'frontend', 'automation', 'verify', 'health', 'all')]
    [string[]]$Component,

    # JSON merged over the configuration, before EIM_DEPLOY_CONFIG_OVERRIDES.
    [string]$ConfigOverridesFile,

    # The package folder (the one holding artifacts/manifest.json). Defaults to this script's folder.
    [string]$PackagePath
)

$ErrorActionPreference = 'Stop'
# Preview is handled through $Preview only; left on, -WhatIf makes cmdlets such as ForEach-Object print their
# input objects, which here can be app settings.
$Preview = [bool]$WhatIfPreference
$WhatIfPreference = $false
$PackageRoot = if ($PackagePath) { $PackagePath } else { $PSScriptRoot }
$PackageArtifacts = Join-Path $PackageRoot 'artifacts'
$InPackage = Test-Path (Join-Path $PackageArtifacts 'manifest.json')
$ConfigDirectory = Join-Path $PSScriptRoot 'config'
$ArmApiVersion = '2023-12-01'

$ComponentOrder = @('preflight', 'infra', 'sql', 'settings', 'myip', 'schema', 'sqlusers', 'myipremove', 'gateway', 'backend', 'frontend', 'automation', 'verify', 'health')
# The components a configuration with "uses" may run; the rest belong to its owner.
$UsingComponents = @('preflight', 'infra', 'settings', 'myip', 'sqlusers', 'myipremove', 'automation', 'verify', 'health')
# Not part of 'all': they open and close the SQL firewall around schema and sqlusers.
$FirewallComponents = @('myip', 'myipremove')
$ReadOnlyComponents = @('preflight', 'verify', 'health')
# Cleanup that must run even when preflight would fail; the pipeline calls it in a finally.
$CleanupComponents = @('myipremove')
$CodeComponents = @('gateway', 'backend', 'frontend', 'automation')

# The names a configuration with "uses" takes from its owner and may not override. The App Service plan is locked
# too: an Automation app on another plan has other outbound IPs, which only the owner's 'sql' could allow.
$LockedNameKeys = @('resourceGroup', 'serviceBusNamespace', 'sqlServer', 'sqlDatabase', 'appServicePlan',
    'gatewayApp', 'gatewayStorage', 'gatewayInsights', 'dashboardApi', 'staticWebApp')
# The names a configuration with "uses" takes from its owner unless it sets its own; a different name is then its own
# resource (Get-OwnedNameKeys), used only by its Automation app.
$OverridableNameKeys = @('keyVault', 'logAnalytics')
# Owned name key -> the key in Get-ExistingResources and Get-FoundationState.
$OwnedResourceKeys = @{ keyVault = 'KeyVault'; logAnalytics = 'LogAnalytics' }
# The Key Vault secret holding the Service Bus connection string, referenced from the app settings.
$ServiceBusSecretName = 'eim-servicebus'
# Service Bus queues (serviceBusQueues): the duplicate detection window of a new queue, as in Wastequip; the keys
# an entry may have; and the settings the code requires (RequiredSettings.Queues), which every list must define.
$QueueDuplicateDetectionWindow = 'PT5M'
$QueueEntryKeys = @('name', 'setting', 'requiresSession', 'duplicateDetection')
$RequiredQueueSettings = @('D365FscDataEventQueue', 'D365BusinessEventQueue', 'D365ToCaptureQueue', 'CaptureToD365Queue', 'CaptureToD365QueueResponse')
# The dashboard API's simulation keys -> the queue setting each one takes its queue from. The API reads exactly these.
$SimulationQueueSettings = [ordered]@{
    Simulation__D365FscDataEventQueue      = 'D365FscDataEventQueue'
    Simulation__CaptureToD365Queue         = 'CaptureToD365Queue'
    Simulation__CaptureToD365ResponseQueue = 'CaptureToD365QueueResponse'
    Simulation__D365ToCaptureQueue         = 'D365ToCaptureQueue'
}
# The settings that hold a Key Vault reference; verify and health check that they resolve.
$KeyVaultReferenceSettings = @('ServiceBus', 'Simulation__ServiceBusConnectionString')
$SqlResource = 'https://database.windows.net/'
$IdentityApiVersion = '2023-01-31'
# sql.firewall: rules for the apps' outbound IPs, or the single "Allow Azure services" rule.
$SqlFirewallModes = @('appOutboundIps', 'azureServices')
# SQL Server error numbers: login failed, and a client IP the server firewall does not allow.
$SqlLoginFailedError = 18456
$SqlFirewallBlockedError = 40615

# Compared by name only in the drift check: the value is per app.
$DriftNameOnlySettings = @('APPLICATIONINSIGHTS_CONNECTION_STRING')
$FunctionRuntime = 'DOTNET-ISOLATED|10.0'

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

function Invoke-AzRest([string]$Method, [string]$Path, $Body, [string]$ApiVersion = $ArmApiVersion) {
    $url = "https://management.azure.com$Path" + '?api-version=' + $ApiVersion
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

    # Removed keys: a stale file or override must not bring the old configuration model back silently.
    foreach ($removed in 'scope', ('shared' + 'Suffix')) {
        if ($config.PSObject.Properties.Name -contains $removed) {
            throw "Configuration '$Name' sets '$removed', which is no longer supported (check '$path', '$basePath' and the overrides); use 'uses' to share another configuration's resources."
        }
    }
    foreach ($key in 'client', 'workload', 'location') {
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

# The configuration paths each requested component reads. A configuration with "uses" writes only Automation settings,
# and creates only the vault and workspace it owns ($OwnedKeys); a vault has no sizing key.
function Get-RequiredConfigPaths([string[]]$Selected, [bool]$IsOwner, [string[]]$OwnedKeys = @()) {
    $paths = [System.Collections.Generic.List[string]]::new()
    $paths.AddRange([string[]]@('subscriptionId', 'location', 'names'))
    if ($Selected -contains 'infra') {
        if ($IsOwner) { $paths.AddRange([string[]]@('appServicePlan', 'serviceBus', 'logAnalytics')) }
        elseif ($OwnedKeys -contains 'logAnalytics') { $paths.Add('logAnalytics') }
    }
    if ($Selected -contains 'settings') {
        $paths.Add('automation')
        if ($IsOwner) { $paths.AddRange([string[]]@('entra.tenantId', 'entra.clientId', 'entra.audience', 'gateway', 'dashboard')) }
    }
    if ($Selected -contains 'frontend') { $paths.AddRange([string[]]@('entra.tenantId', 'entra.clientId', 'entra.scope')) }
    # Every configuration creates its own queues and writes their settings.
    if ($Selected -contains 'infra' -or $Selected -contains 'settings') { $paths.Add('serviceBusQueues') }
    @($paths | Select-Object -Unique)
}

function Get-PlaceholderProblems($Config, [string[]]$Selected, [bool]$IsOwner = $true, [string[]]$OwnedKeys = @()) {
    $required = Get-RequiredConfigPaths $Selected $IsOwner $OwnedKeys
    foreach ($path in @(Get-PlaceholderPaths $Config)) {
        $needed = $required | Where-Object { $path -eq $_ -or $path.StartsWith("$_.") -or $path.StartsWith("$_[") }
        if ($needed) { "'$path' is still a placeholder; fill it in the configuration or EIM_DEPLOY_CONFIG_OVERRIDES" }
    }
}

# Every configuration in $Directory, merged with the base. $Config (with its overrides) replaces its own file's entry.
function Get-AllDeployConfigs($Config, [string]$Directory = $ConfigDirectory) {
    $all = [ordered]@{}
    foreach ($name in (Get-ConfigurationNames $Directory)) {
        $all[$name] = if ($Config -and $name -eq $Config.environment) { $Config } else { Get-DeployConfig $name $Directory }
    }
    if ($Config -and -not $all.Contains("$($Config.environment)")) { $all["$($Config.environment)"] = $Config }
    $all
}

# Why $Config's "uses" is not allowed; nothing when it has none or it is fine. $AllConfigs: name -> configuration. Pure.
function Get-UsesProblems($Config, $AllConfigs) {
    $name = "$($Config.environment)"
    $target = "$($Config.uses)"
    if ([string]::IsNullOrWhiteSpace($target)) { return }
    if ($target -eq $name) { return "'$name' uses itself; chains are not allowed" }
    if (-not $AllConfigs.Contains($target)) { return "'$name' uses '$target', an unknown target: there is no deploy.$target.json" }
    $owner = $AllConfigs[$target]
    if (-not [string]::IsNullOrWhiteSpace("$($owner.uses)")) {
        "'$name' uses '$target', which itself uses '$($owner.uses)'; chains are not allowed"
    }
    if ($Config.production -eq $true -and $owner.production -ne $true) {
        "production configuration '$name' uses the non-production configuration '$target'"
    }
    foreach ($key in $LockedNameKeys) {
        if ($Config.names -and -not [string]::IsNullOrWhiteSpace("$($Config.names.$key)")) {
            "'$name' sets names.$key, which it inherits from '$target'; remove it (or set it in deploy.$target.json)"
        }
    }
}

# Production stays apart from non-production: no "uses" across, and no resolved name in common (case-insensitive;
# the queue prefix is not a resource). $AllConfigs: name -> configuration. Pure.
function Get-IsolationProblems($AllConfigs) {
    $resolved = [ordered]@{}
    foreach ($name in @($AllConfigs.Keys)) {
        $config = $AllConfigs[$name]
        $owner = if ($config.uses -and $AllConfigs.Contains("$($config.uses)")) { $AllConfigs["$($config.uses)"] } else { $null }
        $resolved[$name] = [pscustomobject]@{
            Config = $config
            Names  = @((Get-ResourceNames $config $owner -SkipValidation).PSObject.Properties | Where-Object Name -ne 'QueuePrefix' | ForEach-Object { "$($_.Value)" } | Where-Object { $_ })
        }
    }
    $production = @($resolved.Keys | Where-Object { $resolved[$_].Config.production -eq $true })
    $nonProduction = @($resolved.Keys | Where-Object { $resolved[$_].Config.production -ne $true })
    foreach ($name in $production) {
        $config = $resolved[$name].Config
        if ($config.uses -and $nonProduction -contains "$($config.uses)") {
            "production configuration '$name' uses the non-production configuration '$($config.uses)'"
        }
        foreach ($other in $nonProduction) {
            foreach ($shared in @($resolved[$name].Names | Where-Object { $value = $_; @($resolved[$other].Names | Where-Object { $_ -ieq $value }).Count -gt 0 } | Sort-Object -Unique)) {
                "production configuration '$name' and non-production configuration '$other' both use the name '$shared'"
            }
        }
    }
}

# Why a resolved name cannot be used by Azure; the message names the resource and the name.
function Get-NameProblems($Names) {
    foreach ($storage in $Names.GatewayStorage, $Names.AutomationStorage) {
        if ("$storage" -notmatch '^[a-z0-9]{3,24}$') { "storage account name '$storage' must be 3-24 lowercase letters or digits; override it under 'names'" }
    }
    # Longer names are truncated into the Functions host ID, which then collides between apps.
    foreach ($app in $Names.GatewayApp, $Names.AutomationApp) {
        if ("$app".Length -gt 32) { "Function App name '$app' exceeds 32 characters; override it under 'names'" }
    }
    if ("$($Names.KeyVault)" -notmatch '^[a-zA-Z][a-zA-Z0-9-]{1,22}[a-zA-Z0-9]$') {
        "Key Vault name '$($Names.KeyVault)' must be 3-24 letters, digits or hyphens, start with a letter and end with a letter or digit; override it under 'names'"
    }
    if ("$($Names.SqlServer)" -cnotmatch '^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$') {
        "SQL server name '$($Names.SqlServer)' must be 3-63 lowercase letters, digits or hyphens, not starting or ending with a hyphen; override it under 'names'"
    }
}

# Every name from the configuration's own environment; a non-blank names.<key> wins. With $Owner (the configuration
# named by "uses"), the locked keys ($LockedNameKeys) are always the owner's resolved names, and an overridable key
# ($OverridableNameKeys) is the owner's unless names.<key> is set. A blank override inherits; it does not derive.
function Get-ResourceNames($Config, $Owner, [switch]$SkipValidation) {
    $c = $Config.client
    $w = $Config.workload
    $e = $Config.environment
    $overrides = $Config.names
    $pick = {
        param($key, $default)
        if ($overrides -and -not [string]::IsNullOrWhiteSpace("$($overrides.$key)")) { "$($overrides.$key)" } else { $default }
    }

    $names = [pscustomobject]@{
        ResourceGroup       = & $pick 'resourceGroup'       "rg-$w-$c-$e"
        ServiceBusNamespace = & $pick 'serviceBusNamespace' "sbns-$w-$c-$e"
        KeyVault            = & $pick 'keyVault'            "kv-$w-$c-$e"
        SqlServer           = & $pick 'sqlServer'           "sql-$w-$c-$e"
        SqlDatabase         = & $pick 'sqlDatabase'         "sqldb-$w-$c-$e"
        AppServicePlan      = & $pick 'appServicePlan'      "asp-$w-$c-$e"
        LogAnalytics        = & $pick 'logAnalytics'        "log-$w-$c-$e"
        GatewayApp          = & $pick 'gatewayApp'          "azf-$w-gateway-$c-$e"
        GatewayStorage      = & $pick 'gatewayStorage'      "st$c${w}gw$e"
        GatewayInsights     = & $pick 'gatewayInsights'     "appins-$w-gateway-$c-$e"
        DashboardApi        = & $pick 'dashboardApi'        "app-$w-dashboard-api-$c-$e"
        StaticWebApp        = & $pick 'staticWebApp'        "swa-$w-dashboard-$c-$e"
        AutomationApp       = & $pick 'automationApp'       "azf-$w-automation-$c-$e"
        AutomationStorage   = & $pick 'automationStorage'   "st$c${w}auto$e"
        AutomationInsights  = & $pick 'automationInsights'  "appins-$w-automation-$c-$e"
        QueuePrefix         = & $pick 'queuePrefix'         $e
    }

    if ($Owner) {
        $ownerNames = Get-ResourceNames $Owner -SkipValidation
        $property = { param([string]$Key) $Key.Substring(0, 1).ToUpperInvariant() + $Key.Substring(1) }
        foreach ($key in $LockedNameKeys) { $names.(& $property $key) = $ownerNames.(& $property $key) }
        # Blank inherits the owner's name (never the derived one); an override equal to it takes the owner's spelling.
        foreach ($key in $OverridableNameKeys) {
            $name = & $property $key
            $own = if ($overrides) { "$($overrides.$key)" } else { '' }
            if ([string]::IsNullOrWhiteSpace($own) -or $own -ieq $ownerNames.$name) { $names.$name = $ownerNames.$name }
        }
    }

    if (-not $SkipValidation) {
        $problems = @(Get-NameProblems $names)
        if ($problems.Count -gt 0) { throw "Configuration '$e' resolves to names Azure does not accept:`n  - $($problems -join "`n  - ")" }
    }
    $names
}

# The resolved names as printed at the start of a run; a name the configuration with "uses" owns is marked " (own)". Pure.
function Format-NameListing($Names, [string[]]$OwnedKeys = @()) {
    $ownedProperties = @($OwnedKeys | ForEach-Object { $OwnedResourceKeys[$_] })
    $Names.PSObject.Properties | ForEach-Object { "  {0,-20} {1}{2}" -f $_.Name, $_.Value, $(if ($ownedProperties -contains $_.Name) { ' (own)' }) }
}

# The overridable keys (keyVault, logAnalytics) a configuration with "uses" owns: names.<key> is set and differs,
# ignoring case, from the owner's resolved name. Nothing for an owner, which owns everything already. Pure.
function Get-OwnedNameKeys($Config, $Owner) {
    if (-not $Owner) { return @() }
    $ownerNames = Get-ResourceNames $Owner -SkipValidation
    foreach ($key in $OverridableNameKeys) {
        $own = if ($Config.names) { "$($Config.names.$key)" } else { '' }
        $name = $key.Substring(0, 1).ToUpperInvariant() + $key.Substring(1)
        if (-not [string]::IsNullOrWhiteSpace($own) -and $own -ine $ownerNames.$name) { $key }
    }
}

# Why the configuration's serviceBusQueues cannot be deployed; nothing when it is fine. $Prefix: its queue prefix,
# which every queue name is resolved with. Each message names the configuration and the entry. Pure.
function Get-QueueConfigProblems($Config, [string]$Prefix) {
    $environment = "$($Config.environment)"
    $property = $Config.PSObject.Properties['serviceBusQueues']
    # Assigned directly: an if-expression would unroll an empty or one-entry list.
    $list = $null
    if ($property) { $list = $property.Value }
    $state = if ($null -eq $list) { 'missing' } elseif ($list -isnot [System.Collections.IList]) { 'not a list' } elseif ($list.Count -eq 0) { 'empty' }
    if ($state) {
        return "serviceBusQueues must list the Service Bus queues of '$environment' (in deploy.base.json or deploy.$environment.json); it is $state"
    }

    $names = [ordered]@{}
    $settings = [ordered]@{}
    for ($i = 0; $i -lt $list.Count; $i++) {
        $entry = $list[$i]
        $at = "serviceBusQueues[$i] of '$environment'"
        if ($entry -isnot [System.Management.Automation.PSCustomObject]) { "$at is not an object with a name"; continue }
        foreach ($key in @($entry.PSObject.Properties.Name | Where-Object { $QueueEntryKeys -cnotcontains $_ })) {
            "$at has the unknown key '$key'; an entry has only $($QueueEntryKeys -join ', ')"
        }

        $name = $entry.name
        if ($null -eq $name -or [string]::IsNullOrWhiteSpace("$name")) { "$at has no name" }
        else {
            if ($name -isnot [string] -or $name -cnotmatch '^[a-z0-9]+(-[a-z0-9]+)*$') {
                "$at has the name '$name'; a queue name is lowercase letters and digits, separated by single hyphens"
            }
            $resolved = "$Prefix-$name"
            if ($resolved.Length -gt 260) { "$at resolves to the queue name '$resolved', which exceeds the Service Bus limit of 260 characters" }
            if ($names.Contains($resolved)) { "$at resolves to the queue name '$resolved', which $($names[$resolved]) resolves to too (queue names ignore case)" }
            else { $names[$resolved] = "serviceBusQueues[$i]" }
        }

        $setting = $entry.setting
        if ($null -ne $setting) {
            if ($setting -isnot [string] -or $setting -cnotmatch '^[A-Za-z_][A-Za-z0-9_]*$') {
                "$at has the setting '$setting'; a setting name is letters, digits and underscores, not starting with a digit"
            }
            elseif ($settings.Contains($setting)) { "$at has the setting '$setting', which $($settings[$setting]) has too (app settings ignore case)" }
            else { $settings[$setting] = "serviceBusQueues[$i]" }
        }

        foreach ($flag in 'requiresSession', 'duplicateDetection') {
            if ($entry.PSObject.Properties.Name -ccontains $flag -and $entry.$flag -isnot [bool]) {
                "$at has $flag '$($entry.$flag)'; it must be the JSON value true or false"
            }
        }
    }

    $defined = @($settings.Keys)
    foreach ($required in $RequiredQueueSettings) {
        if ($defined -cnotcontains $required) {
            "serviceBusQueues of '$environment' has no entry with the setting '$required', which the Automation app requires (RequiredSettings.Queues)"
        }
    }
}

# The configuration's queues, in configuration order: Name (<prefix>-<name>), Setting (or $null), RequiresSession
# (default false) and DuplicateDetection (default true). Throws on any Get-QueueConfigProblems problem. Pure.
function Get-ServiceBusQueues($Config, [string]$Prefix) {
    $problems = @(Get-QueueConfigProblems $Config $Prefix)
    if ($problems.Count -gt 0) { throw "Configuration '$($Config.environment)' has an invalid serviceBusQueues:`n  - $($problems -join "`n  - ")" }
    foreach ($entry in @($Config.serviceBusQueues)) {
        [pscustomobject]@{
            Name               = "$Prefix-$($entry.name)"
            Setting            = if ($null -ne $entry.setting) { "$($entry.setting)" } else { $null }
            RequiresSession    = $entry.requiresSession -eq $true
            DuplicateDetection = $entry.duplicateDetection -ne $false
        }
    }
}

# Setting -> queue name for the queues (Get-ServiceBusQueues) that have a setting, in their order. Pure.
function Get-QueueSettings($Queues) {
    $settings = [ordered]@{}
    foreach ($queue in @($Queues)) {
        if ($queue -and -not [string]::IsNullOrWhiteSpace($queue.Setting)) { $settings[$queue.Setting] = $queue.Name }
    }
    $settings
}

# --------------------------------------------------------------------------------------------
# Components (pure)
# --------------------------------------------------------------------------------------------

# $IsOwner: the configuration has no "uses"; $Owner: the configuration it uses, named when a component belongs to it.
function Resolve-Components([string[]]$Requested, [bool]$IsOwner, [string]$Owner) {
    $requested = @($Requested | Where-Object { $_ } | ForEach-Object { "$_".ToLowerInvariant() } | Select-Object -Unique)
    if ($requested.Count -eq 0) { throw "-Component is required, e.g. -Component all." }

    $allowed = if ($IsOwner) { $ComponentOrder } else { $UsingComponents }
    foreach ($name in @($requested | Where-Object { $_ -ne 'all' })) {
        if ($ComponentOrder -notcontains $name) { throw "Unknown component '$name'." }
        if ($allowed -notcontains $name) {
            throw "'$name' belongs to the owner configuration '$Owner', whose resources this configuration uses; run it with -Environment $Owner."
        }
    }

    $wanted = @($requested | Where-Object { $_ -ne 'all' })
    if ($requested -contains 'all') { $wanted += @($allowed | Where-Object { $FirewallComponents -notcontains $_ }) }
    $selected = @($ComponentOrder | Where-Object { $wanted -contains $_ })
    if ($selected | Where-Object { $ReadOnlyComponents -notcontains $_ -and $CleanupComponents -notcontains $_ }) {
        $selected = @($ComponentOrder | Where-Object { $_ -eq 'preflight' -or $selected -contains $_ })
    }
    $selected
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

# The connection string for the configuration's database. Not secret: it holds no password or key.
function Get-SqlConnectionString($Names, [string]$Authentication) {
    "Server=tcp:$($Names.SqlServer).database.windows.net,1433;Initial Catalog=$($Names.SqlDatabase);Authentication=$Authentication;Encrypt=True;TrustServerCertificate=False;Connect Timeout=30;"
}

# A versionless Key Vault reference, so the app picks up a rotated secret.
function Get-KeyVaultSecretReference($Names, [string]$Secret = $ServiceBusSecretName) {
    "@Microsoft.KeyVault(SecretUri=https://$($Names.KeyVault).vault.azure.net/secrets/$Secret/)"
}

function Get-AutomationSettings($Config, $Names, [string]$InsightsConnection) {
    $a = $Config.automation
    $settings = [ordered]@{
        FUNCTIONS_WORKER_RUNTIME              = 'dotnet-isolated'
        APPLICATIONINSIGHTS_CONNECTION_STRING = $InsightsConnection
        ServiceBus                            = Get-KeyVaultSecretReference $Names
        EIMDB                                 = Get-SqlConnectionString $Names 'Active Directory Managed Identity'
    }
    $queues = Get-QueueSettings (Get-ServiceBusQueues $Config $Names.QueuePrefix)
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

function Get-GatewaySettings($Config, $Names, [string]$InsightsConnection) {
    $g = $Config.gateway
    $settings = [ordered]@{
        FUNCTIONS_WORKER_RUNTIME              = 'dotnet-isolated'
        APPLICATIONINSIGHTS_CONNECTION_STRING = $InsightsConnection
        EIMDB                                 = Get-SqlConnectionString $Names 'Active Directory Managed Identity'
        Gateway__BaseUrl                      = Get-GatewayBaseUrl $Config $Names
        Gateway__MaxRequestBytes              = $g.maxRequestBytes
        Gateway__IdempotencyRetention         = $g.idempotencyRetention
        Gateway__IdempotencyPurgeSchedule     = $g.idempotencyPurgeSchedule
    }
    Assert-SettingValues $settings $Names.GatewayApp
    $settings
}

function Test-SimulationEnabled($Config) { $Config.dashboard.simulationEnabled -eq $true }

# Where the dashboard simulation may publish: queue prefix -> the merged configuration whose queues those are. The
# owner first, then each configuration that uses it. Pure.
function Get-SimulationTargets($Config, $AllConfigs) {
    $targets = [ordered]@{ "$((Get-ResourceNames $Config -SkipValidation).QueuePrefix)" = $Config }
    foreach ($name in @($AllConfigs.Keys)) {
        $other = $AllConfigs[$name]
        if ("$($other.uses)" -ne "$($Config.environment)") { continue }
        $prefix = "$((Get-ResourceNames $other $Config -SkipValidation).QueuePrefix)"
        if (-not $targets.Contains($prefix)) { $targets[$prefix] = $other }
    }
    $targets
}

# $SimulationTargets: Get-SimulationTargets; without it, only the configuration's own queue prefix and queues.
function Get-DashboardApiSettings($Config, $Names, $SimulationTargets) {
    $entra = $Config.entra
    $simulation = Test-SimulationEnabled $Config
    $settings = [ordered]@{
        ASPNETCORE_ENVIRONMENT   = 'Production'
        ConnectionStrings__EIMDB = Get-SqlConnectionString $Names 'Active Directory Managed Identity'
        AzureAd__TenantId        = $entra.tenantId
        AzureAd__ClientId        = $entra.clientId
        AzureAd__Audience        = $entra.audience
        # Always written: the API treats a missing value as off, so simulation is on only where this says true.
        Simulation__Enabled      = if ($simulation) { 'true' } else { 'false' }
    }
    if ($simulation) {
        $prefix = "$($Config.dashboard.simulationQueuePrefix)"
        if ([string]::IsNullOrWhiteSpace($prefix) -or (Test-Placeholder $prefix)) {
            throw "dashboard.simulationQueuePrefix must name the queue prefix the simulation publishes to while dashboard.simulationEnabled is true; it is '$prefix'."
        }
        $targets = $SimulationTargets
        if (-not $targets -or $targets.Count -eq 0) { $targets = [ordered]@{ "$($Names.QueuePrefix)" = $Config } }
        $allowed = @($targets.Keys)
        if ($allowed -notcontains $prefix) {
            throw "dashboard.simulationQueuePrefix is '$prefix', but '$($Config.environment)' and the configurations that use it publish to $($allowed -join ', ') only."
        }
        # The queues of the configuration that owns the prefix, so its own serviceBusQueues applies.
        $queues = Get-QueueSettings (Get-ServiceBusQueues $targets[$prefix] $prefix)
        $settings['Simulation__ServiceBusConnectionString'] = Get-KeyVaultSecretReference $Names
        foreach ($key in $SimulationQueueSettings.Keys) { $settings[$key] = $queues[$SimulationQueueSettings[$key]] }
    }
    # Same value the gateway is deployed with, so the address the dashboard verifies keys against
    # cannot drift from the one the gateway actually answers on.
    $settings['Gateway__BaseUrl'] = Get-GatewayBaseUrl $Config $Names
    $settings['Gateway__AppName'] = $Names.GatewayApp
    Assert-SettingValues $settings $Names.DashboardApi
    $settings
}

# Every gated trigger, written on every 'settings' run: enabled ('false') when automation.triggersEnabled is true,
# except the functions in keepDisabled; all disabled ('true') when it is false.
function Get-GatedSettings([string[]]$GatedFunctions, [bool]$TriggersEnabled, [string[]]$KeepDisabled) {
    $settings = [ordered]@{}
    foreach ($function in $GatedFunctions) {
        $disabled = -not $TriggersEnabled -or ($KeepDisabled -contains $function)
        $settings["AzureWebJobs.$function.Disabled"] = if ($disabled) { 'true' } else { 'false' }
    }
    $settings
}

# Why automation.triggersEnabled or automation.keepDisabled cannot be used. Pure.
function Get-TriggerConfigProblems($Config) {
    $automation = $Config.automation
    if (-not $automation -or $automation.PSObject.Properties.Name -notcontains 'triggersEnabled') {
        "automation.triggersEnabled is missing; set it to true or false in deploy.$($Config.environment).json"
    }
    elseif ($automation.triggersEnabled -isnot [bool]) {
        "automation.triggersEnabled is '$($automation.triggersEnabled)'; it must be the JSON value true or false"
    }
    foreach ($function in @($automation.keepDisabled | Where-Object { $_ })) {
        if (@($automation.gatedFunctions) -notcontains $function) { "automation.keepDisabled lists '$function', which is not in automation.gatedFunctions" }
    }
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

# Simulation__* entries on the dashboard API that the settings no longer write (all but Simulation__Enabled when
# simulation is off).
function Get-StaleSimulationSettings($Settings, $Current) {
    @($Current | ForEach-Object { "$($_.name)" } | Where-Object { $_ -like 'Simulation__*' -and -not $Settings.Contains($_) })
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
    [ordered]@{ 'eim-workload' = "$($Config.workload)"; 'eim-client' = "$($Config.client)"; 'eim-environment' = "$($Config.environment)" }
}

# A resource with an eim-environment tag was created by this script; only such resources get their configuration
# converged. Others are used as found.
function Test-ManagedResource($Tags) {
    if ($null -eq $Tags) { return $false }
    $names = if ($Tags -is [System.Collections.IDictionary]) { @($Tags.Keys) } else { @($Tags.PSObject.Properties.Name) }
    @($names | Where-Object { $_ -ieq 'eim-environment' }).Count -gt 0
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

# $Managed: the app carries an eim-environment tag (or is created now); an existing app without one is used as found.
function Get-FunctionAppSteps($Config, $Names, [string]$App, [string]$Storage, [bool]$Exists, [string[]]$TagArgs, [bool]$Managed = $true) {
    $rg = $Names.ResourceGroup
    if (-not $Exists) {
        New-InfraStep az "create Function App $App on $($Names.AppServicePlan)" (@('functionapp', 'create', '-g', $rg, '-n', $App, '--plan', $Names.AppServicePlan,
                '--storage-account', $Storage, '--runtime', 'dotnet-isolated', '--runtime-version', '10', '--functions-version', '4', '--os-type', 'Linux',
                '--disable-app-insights', 'true', '--tags') + $TagArgs + @('-o', 'none'))
    }
    if ($Exists -and -not $Managed) { return }
    # Converged on every run, so a property changed by hand is put back.
    New-InfraStep az "$App`: Always On, FTPS disabled, TLS 1.2" @('functionapp', 'config', 'set', '-g', $rg, '-n', $App,
        '--always-on', 'true', '--ftps-state', 'Disabled', '--min-tls-version', '1.2', '--http20-enabled', 'false', '-o', 'none')
    New-InfraStep az "$App`: HTTPS only" @('functionapp', 'update', '-g', $rg, '-n', $App, '--set', 'httpsOnly=true', '-o', 'none')
    New-InfraStep runtime "$App`: runtime $FunctionRuntime" @($App)
}

# The system-assigned identity and its secret 'get' on the Key Vault, for every app the configuration deploys. Both are
# idempotent and additive, so they run on kept apps and vaults too. {principalId} is filled in when the step runs, and
# the secret permissions become the union with the identity's existing ones (Get-SecretPermissionUnion).
function Get-IdentitySteps($Names, [string]$App, [string]$Type) {
    New-InfraStep az "$App`: system-assigned identity" @($Type, 'identity', 'assign', '-g', $Names.ResourceGroup, '-n', $App, '-o', 'none')
    New-InfraStep policy "$App`: secret 'get' on Key Vault $($Names.KeyVault)" @('keyvault', 'set-policy', '-g', $Names.ResourceGroup, '-n', $Names.KeyVault,
        '--object-id', '{principalId}', '--secret-permissions', 'get', '-o', 'none') @{ App = $App; Type = $Type }
}

# 'az keyvault set-policy' replaces an identity's secret permissions rather than adding to them. Returns the existing
# permissions plus 'get', or $null when 'get' (or 'all') is already granted and the policy is left as it is. Pure.
function Get-SecretPermissionUnion([string[]]$Existing) {
    $current = @($Existing | Where-Object { $_ } | ForEach-Object { "$_".ToLowerInvariant() } | Select-Object -Unique)
    if ($current -contains 'get' -or $current -contains 'all') { return $null }
    , @($current + 'get')
}

# A policy step's arguments with the principal id and the secret permissions filled in. Pure.
function Resolve-PolicyStepArgs([object[]]$StepArgs, [string]$PrincipalId, [string[]]$Permissions) {
    $resolved = [System.Collections.Generic.List[string]]::new()
    for ($i = 0; $i -lt $StepArgs.Count; $i++) {
        $arg = "$($StepArgs[$i])"
        if ($arg -eq '{principalId}') { $resolved.Add($PrincipalId); continue }
        $resolved.Add($arg)
        if ($arg -eq '--secret-permissions') {
            foreach ($permission in $Permissions) { $resolved.Add($permission) }
            $i++
        }
    }
    @($resolved)
}

# The create steps of the vault, plan and workspace named by $Keys (keyVault, appServicePlan, logAnalytics), always
# in that order, each only when it is missing. Owners pass all three; a configuration with "uses" the ones it owns.
function Get-FoundationResourceSteps($Config, $Names, $Existing, [string[]]$Keys, $TagArgs, $Tags) {
    $rg = $Names.ResourceGroup
    $location = $Config.location
    if ($Keys -contains 'keyVault' -and -not $Existing.KeyVault) {
        # Access policies, not RBAC: the service connection's Contributor role can set policies but not assign roles.
        New-InfraStep keyvault "create Key Vault $($Names.KeyVault) (access policies)" (@('keyvault', 'create', '-g', $rg, '-n', $Names.KeyVault, '-l', $location,
                '--enable-rbac-authorization', 'false', '--tags') + $TagArgs + @('-o', 'none'))
    }
    if ($Keys -contains 'appServicePlan' -and -not $Existing.Plan) {
        $sku = "$($Config.appServicePlan.sku)".ToUpperInvariant()
        New-InfraStep az "create App Service plan $($Names.AppServicePlan) ($sku, Linux)" (@('appservice', 'plan', 'create', '-g', $rg, '-n', $Names.AppServicePlan,
                '-l', $location, '--is-linux', '--sku', $sku, '--tags') + $TagArgs + @('-o', 'none'))
    }
    if ($Keys -contains 'logAnalytics' -and -not $Existing.LogAnalytics) {
        $retention = if ($Config.logAnalytics.retentionDays) { [int]$Config.logAnalytics.retentionDays } else { 30 }
        # Generic resource API rather than 'az monitor log-analytics', for the same reason as Insights.
        New-InfraStep json "create Log Analytics workspace $($Names.LogAnalytics) ($retention days)" @('resource', 'create', '-g', $rg, '-n', $Names.LogAnalytics,
            '--resource-type', 'Microsoft.OperationalInsights/workspaces', '--is-full-object') @{
            location   = $location
            tags       = $Tags
            properties = @{ sku = @{ name = 'PerGB2018' }; retentionInDays = $retention }
        }
    }
}

# $Existing (Get-ExistingResources): ResourceGroup, ServiceBusNamespace, KeyVault, Plan, LogAnalytics, GatewayStorage,
# GatewayInsights, GatewayApp, DashboardApi, StaticWebApp, AutomationStorage, AutomationInsights, AutomationApp
# (booleans), Queues (queue name -> boolean) and Managed (key -> $true when the resource has an eim-environment tag).
# $IsOwner: the configuration has no "uses"; otherwise only its queues and Automation resources are planned, plus
# the Key Vault and Log Analytics workspace it owns ($OwnedKeys, from Get-OwnedNameKeys), and preflight has already
# checked the owner's resources. The Automation app always goes on the owner's plan.
function Get-InfraPlan($Config, $Names, $Existing, [bool]$IsOwner, [string[]]$OwnedKeys = @()) {
    $rg = $Names.ResourceGroup
    $location = $Config.location
    $tags = Get-ResourceTags $Config
    $tagArgs = @($tags.Keys | ForEach-Object { "$_=$($tags[$_])" })
    $managed = { param([string]$Key) [bool]($Existing.Managed -and $Existing.Managed[$Key]) }

    if ($IsOwner) {
        if (-not $Existing.ResourceGroup) {
            New-InfraStep az "create resource group $rg" (@('group', 'create', '-n', $rg, '-l', $location, '--tags') + $tagArgs + @('-o', 'none'))
        }
        if (-not $Existing.ServiceBusNamespace) {
            $sku = if ($Config.serviceBus.sku) { "$($Config.serviceBus.sku)" } else { 'Standard' }
            New-InfraStep az "create Service Bus namespace $($Names.ServiceBusNamespace) ($sku)" (@('servicebus', 'namespace', 'create', '-g', $rg, '-n', $Names.ServiceBusNamespace,
                    '-l', $location, '--sku', $sku, '--min-tls', '1.2', '--tags') + $tagArgs + @('-o', 'none'))
        }
        Get-FoundationResourceSteps $Config $Names $Existing @('keyVault', 'appServicePlan', 'logAnalytics') $tagArgs $tags
    }
    else {
        # Only the vault and workspace it owns, never a plan; created in the owner's group with its own tags.
        Get-FoundationResourceSteps $Config $Names $Existing @($OwnedKeys | Where-Object { $OverridableNameKeys -contains $_ }) $tagArgs $tags
    }

    # serviceBusQueues: only missing queues are created; an existing queue is left exactly as it is, as in Wastequip.
    # Sessions and duplicate detection cannot change after creation, so the label shows a non-default choice.
    foreach ($queue in @(Get-ServiceBusQueues $Config $Names.QueuePrefix)) {
        if ($Existing.Queues -and $Existing.Queues[$queue.Name]) { continue }
        $queueArgs = @('servicebus', 'queue', 'create', '-g', $rg, '--namespace-name', $Names.ServiceBusNamespace, '-n', $queue.Name,
            '--max-size', '1024', '--default-message-time-to-live', 'P14D', '--lock-duration', 'PT60S', '--max-delivery-count', '10',
            '--enable-dead-lettering-on-message-expiration', 'true', '--enable-partitioning', 'false')
        $label = "create queue $($queue.Name)"
        if ($queue.RequiresSession) { $queueArgs += @('--enable-session', 'true'); $label += ' (sessions)' }
        if ($queue.DuplicateDetection) { $queueArgs += @('--duplicate-detection', 'true', '--duplicate-detection-history-time-window', $QueueDuplicateDetectionWindow) }
        else { $label += ' (no duplicate detection)' }
        New-InfraStep az $label ($queueArgs + @('-o', 'none'))
    }

    if ($IsOwner) {
        Get-StorageSteps $Config $Names $Names.GatewayStorage ([bool]$Existing.GatewayStorage) $tagArgs
        Get-InsightsSteps $Config $Names $Names.GatewayInsights ([bool]$Existing.GatewayInsights)
        Get-FunctionAppSteps $Config $Names $Names.GatewayApp $Names.GatewayStorage ([bool]$Existing.GatewayApp) $tagArgs (& $managed 'GatewayApp')
        Get-IdentitySteps $Names $Names.GatewayApp 'functionapp'

        $api = $Names.DashboardApi
        if (-not $Existing.DashboardApi) {
            New-InfraStep az "create dashboard API $api on $($Names.AppServicePlan)" (@('webapp', 'create', '-g', $rg, '-n', $api,
                    '--plan', $Names.AppServicePlan, '--runtime', 'DOTNETCORE:10.0', '--tags') + $tagArgs + @('-o', 'none'))
        }
        # Site configuration only on a dashboard API this script created; a kept one is deployed in place, as found.
        if (-not $Existing.DashboardApi -or (& $managed 'DashboardApi')) {
            New-InfraStep az "$api`: Always On, FTPS disabled, TLS 1.2" @('webapp', 'config', 'set', '-g', $rg, '-n', $api,
                '--always-on', 'true', '--ftps-state', 'Disabled', '--min-tls-version', '1.2', '-o', 'none')
            New-InfraStep az "$api`: HTTPS only" @('webapp', 'update', '-g', $rg, '-n', $api, '--https-only', 'true', '-o', 'none')
        }
        Get-IdentitySteps $Names $api 'webapp'

        if (-not $Existing.StaticWebApp) {
            New-InfraStep az "create static web app $($Names.StaticWebApp) (Standard)" (@('staticwebapp', 'create', '-g', $rg, '-n', $Names.StaticWebApp,
                    '-l', $location, '--sku', 'Standard', '--tags') + $tagArgs + @('-o', 'none'))
        }
    }

    Get-StorageSteps $Config $Names $Names.AutomationStorage ([bool]$Existing.AutomationStorage) $tagArgs
    Get-InsightsSteps $Config $Names $Names.AutomationInsights ([bool]$Existing.AutomationInsights)
    Get-FunctionAppSteps $Config $Names $Names.AutomationApp $Names.AutomationStorage ([bool]$Existing.AutomationApp) $tagArgs (& $managed 'AutomationApp')
    Get-IdentitySteps $Names $Names.AutomationApp 'functionapp'
}

# The owner resources a configuration needs before 'infra' (pure). $Foundation: ResourceGroup, ServiceBusNamespace,
# KeyVault, Plan, LogAnalytics, SqlServer, SqlDatabase (booleans) and KeyVaultRbac. Problems fail preflight; Notes
# are printed. An owner's missing resources are created by 'infra' and 'sql'. A configuration with "uses" cannot
# create them, except the Key Vault and Log Analytics workspace it owns ($OwnedKeys), which 'infra' creates; a
# missing plan is always a problem. The RBAC check covers the resolved vault, owned or not.
function Get-FoundationProblems($Names, $Foundation, [bool]$IsOwner, [string]$Owner, [string[]]$OwnedKeys = @()) {
    $problems = [System.Collections.Generic.List[string]]::new()
    $notes = [System.Collections.Generic.List[string]]::new()
    $owned = @($OwnedKeys | Where-Object { $OwnedResourceKeys.ContainsKey("$_") } | ForEach-Object { $OwnedResourceKeys["$_"] })
    $resources = [ordered]@{
        ResourceGroup       = "resource group '$($Names.ResourceGroup)'"
        ServiceBusNamespace = "Service Bus namespace '$($Names.ServiceBusNamespace)'"
        KeyVault            = "Key Vault '$($Names.KeyVault)'"
        Plan                = "App Service plan '$($Names.AppServicePlan)'"
        LogAnalytics        = "Log Analytics workspace '$($Names.LogAnalytics)'"
        SqlServer           = "SQL server '$($Names.SqlServer)'"
        SqlDatabase         = "database '$($Names.SqlDatabase)'"
    }
    foreach ($key in $resources.Keys) {
        if ($Foundation[$key]) { continue }
        if ($IsOwner) { $notes.Add("$($resources[$key]) does not exist; $(if ($key -like 'Sql*') { "'sql'" } else { "'infra'" }) will create it") }
        elseif ($owned -contains $key) { $notes.Add("$($resources[$key]) does not exist; 'infra' will create it") }
        else { $problems.Add("$($resources[$key]) does not exist; 'infra' and 'sql' must run for '$Owner' first") }
    }
    if ($Foundation.KeyVault -and $Foundation.KeyVaultRbac) {
        $problems.Add("Key Vault '$($Names.KeyVault)' uses RBAC authorisation; access-policy authorisation is required (the deployment grants the apps secret 'get' through access policies)")
    }
    [pscustomobject]@{ Problems = @($problems); Notes = @($notes) }
}

# 'infra' never relinks an existing Insights resource. One warning when the Automation Insights is linked to a
# workspace other than the resolved one ($InsightsWorkspaceId: its WorkspaceResourceId); nothing when it is blank. Pure.
function Get-PlacementWarnings($Names, [string]$InsightsWorkspaceId) {
    if ([string]::IsNullOrWhiteSpace($InsightsWorkspaceId)) { return }
    $current = ($InsightsWorkspaceId.Trim().TrimEnd('/') -split '/')[-1]
    if ($current -ine $Names.LogAnalytics) {
        "$($Names.AutomationInsights) is linked to workspace '$current', not '$($Names.LogAnalytics)'; relink it by hand or recreate it"
    }
}

# --------------------------------------------------------------------------------------------
# SQL plan and database users (pure)
# --------------------------------------------------------------------------------------------

# Why a missing SQL server cannot be created: it is Entra-only, so it needs its Entra admin. Pure.
function Get-SqlAdminProblems($Config, $Names) {
    $sql = $Config.sql
    $blank = { param($Value) [string]::IsNullOrWhiteSpace("$Value") -or (Test-Placeholder $Value) }
    if ((& $blank $sql.entraAdmin) -or (& $blank $sql.entraAdminObjectId)) {
        "SQL server '$($Names.SqlServer)' does not exist and is created Entra-only, so sql.entraAdmin and sql.entraAdminObjectId must both be filled in (the admin group's display name and object id); they are '$($sql.entraAdmin)' and '$($sql.entraAdminObjectId)'"
    }
}

# A misspelt sql.firewall would otherwise fall through to the app-outbound rules. Pure.
function Get-SqlFirewallProblems($Config) {
    $mode = "$($Config.sql.firewall)"
    if ($SqlFirewallModes -notcontains $mode) {
        "sql.firewall is '$mode'; it must be one of: $($SqlFirewallModes -join ', ')"
    }
}

function ConvertTo-IpNumber([string]$Address) {
    $bytes = [System.Net.IPAddress]::Parse($Address).GetAddressBytes()
    [uint32](([uint32]$bytes[0] -shl 24) -bor ([uint32]$bytes[1] -shl 16) -bor ([uint32]$bytes[2] -shl 8) -bor [uint32]$bytes[3])
}

# The outbound IPs no firewall rule lets in. AllowAllWindowsAzureIps (0.0.0.0) covers every Azure address.
function Get-UncoveredIps([string[]]$OutboundIps, $Rules) {
    if (@($Rules | Where-Object { $_.name -eq 'AllowAllWindowsAzureIps' -or ("$($_.startIpAddress)" -eq '0.0.0.0' -and "$($_.endIpAddress)" -eq '0.0.0.0') }).Count -gt 0) { return }
    foreach ($ip in $OutboundIps) {
        $value = ConvertTo-IpNumber $ip
        $covered = @($Rules | Where-Object {
                (ConvertTo-IpNumber "$($_.startIpAddress)") -le $value -and $value -le (ConvertTo-IpNumber "$($_.endIpAddress)")
            }).Count -gt 0
        if (-not $covered) { $ip }
    }
}

# $Existing (Get-SqlState): Server, Database, Managed (booleans), AdminLogin, AdminSid, AdOnly and Rules (the
# server's firewall rules: name, startIpAddress, endIpAddress). $OutboundIps: the possible outbound IPs of the
# configuration's apps. Returns Steps (New-InfraStep), Report and Warnings. A server this script did not create
# is only reported on.
function Get-SqlPlan($Config, $Names, $Existing, [string[]]$OutboundIps) {
    $rg = $Names.ResourceGroup
    $server = $Names.SqlServer
    $sql = $Config.sql
    $tags = Get-ResourceTags $Config
    $tagArgs = @($tags.Keys | ForEach-Object { "$_=$($tags[$_])" })
    $steps = [System.Collections.Generic.List[object]]::new()
    $report = [System.Collections.Generic.List[string]]::new()
    $warnings = [System.Collections.Generic.List[string]]::new()
    $ips = @($OutboundIps | Where-Object { $_ } | Sort-Object -Unique)
    $firewallProblems = @(Get-SqlFirewallProblems $Config)
    if ($firewallProblems.Count -gt 0) { throw $firewallProblems[0] }

    if (-not $Existing.Server) {
        $problems = @(Get-SqlAdminProblems $Config $Names)
        if ($problems.Count -gt 0) { throw $problems[0] }
        $type = if ($sql.entraAdminType) { "$($sql.entraAdminType)" } else { 'Group' }
        $steps.Add((New-InfraStep az "create SQL server $server (Entra-only, admin $($sql.entraAdmin))" (@('sql', 'server', 'create', '-g', $rg, '-n', $server,
                        '-l', $Config.location, '--enable-ad-only-auth', '--external-admin-principal-type', $type, '--external-admin-name', $sql.entraAdmin,
                        '--external-admin-sid', $sql.entraAdminObjectId, '--minimal-tls-version', '1.2', '--tags') + $tagArgs + @('-o', 'none'))))
    }
    if (-not $Existing.Server -or -not $Existing.Database) {
        $steps.Add((New-InfraStep az "create database $($Names.SqlDatabase) ($($sql.sku), $($sql.backupRedundancy) backups)" (@('sql', 'db', 'create', '-g', $rg, '-s', $server,
                        '-n', $Names.SqlDatabase, '--service-objective', $sql.sku, '--backup-storage-redundancy', $sql.backupRedundancy, '--tags') + $tagArgs + @('-o', 'none'))))
    }

    if (-not $Existing.Server -or $Existing.Managed) {
        $rules = @($Existing.Rules)
        if ("$($sql.firewall)" -eq 'azureServices') {
            if (-not ($rules | Where-Object name -eq 'AllowAllWindowsAzureIps')) {
                $steps.Add((New-InfraStep az "$server`: allow Azure services" @('sql', 'server', 'firewall-rule', 'create', '-g', $rg, '-s', $server,
                            '-n', 'AllowAllWindowsAzureIps', '--start-ip-address', '0.0.0.0', '--end-ip-address', '0.0.0.0', '-o', 'none')))
            }
            $report.Add("firewall: Allow Azure services")
        }
        elseif ($ips.Count -eq 0) {
            # Without the apps' IPs every app-outbound rule would look stale; change nothing.
            $warnings.Add("SQL server '$server': no app outbound IPs were found, so its app-outbound firewall rules are left as they are; run 'infra' first")
        }
        else {
            $expected = [ordered]@{}
            foreach ($ip in $ips) { $expected["app-outbound-$($ip -replace '\.', '-')"] = $ip }
            foreach ($name in $expected.Keys) {
                if ($rules | Where-Object name -eq $name) { continue }
                $steps.Add((New-InfraStep az "$server`: firewall rule $name" @('sql', 'server', 'firewall-rule', 'create', '-g', $rg, '-s', $server,
                            '-n', $name, '--start-ip-address', $expected[$name], '--end-ip-address', $expected[$name], '-o', 'none')))
            }
            foreach ($stale in @($rules | Where-Object { "$($_.name)" -like 'app-outbound-*' -and -not $expected.Contains("$($_.name)") })) {
                $steps.Add((New-InfraStep az "$server`: delete stale firewall rule $($stale.name)" @('sql', 'server', 'firewall-rule', 'delete', '-g', $rg, '-s', $server,
                            '-n', $stale.name, '-o', 'none')))
            }
            $report.Add("firewall: $($expected.Count) app outbound IPs allowed")
        }
        return [pscustomobject]@{ Steps = @($steps); Report = @($report); Warnings = @($warnings) }
    }

    # An existing server this script did not create: used as found, never changed.
    $admin = if ([string]::IsNullOrWhiteSpace("$($Existing.AdminLogin)")) { '(none)' } else { "$($Existing.AdminLogin) ($($Existing.AdminSid))" }
    $report.Add("SQL server $server is not managed by this script; reported only")
    $report.Add("Entra admin: $admin")
    $report.Add("Entra-only authentication: $(if ($Existing.AdOnly -eq $true) { 'on' } else { 'off' })")
    if ([string]::IsNullOrWhiteSpace("$($Existing.AdminLogin)")) {
        $warnings.Add("SQL server '$server' has no Entra admin; schema and sqlusers cannot sign in until one is set (az sql server ad-admin create)")
    }
    $configured = "$($sql.entraAdminObjectId)"
    if (-not [string]::IsNullOrWhiteSpace($configured) -and -not (Test-Placeholder $configured) -and $Existing.AdminSid -and $configured -ne "$($Existing.AdminSid)") {
        $warnings.Add("SQL server '$server' has the Entra admin object id '$($Existing.AdminSid)', but sql.entraAdminObjectId is '$configured'")
    }
    $uncovered = @(Get-UncoveredIps $ips @($Existing.Rules))
    if ($uncovered.Count -gt 0) {
        $warnings.Add("SQL server '$server' has no firewall rule for the app outbound IPs $($uncovered -join ', '); add rules by hand")
    }
    elseif ($ips.Count -gt 0) { $report.Add("firewall: all $($ips.Count) app outbound IPs are covered") }
    [pscustomobject]@{ Steps = @($steps); Report = @($report); Warnings = @($warnings) }
}

# The apps whose identities get a database user: an owner deploys the gateway, the dashboard API and its Automation
# app; a configuration with "uses" only its Automation app. Type is the az command group.
function Get-SqlUserApps($Names, [bool]$IsOwner) {
    if ($IsOwner) {
        [pscustomobject]@{ App = $Names.GatewayApp; Type = 'functionapp' }
        [pscustomobject]@{ App = $Names.DashboardApi; Type = 'webapp' }
    }
    [pscustomobject]@{ App = $Names.AutomationApp; Type = 'functionapp' }
}

$DatabaseRoles = @('db_datareader', 'db_datawriter')

# Creates the contained user @name for the Entra application @clientId (WITH SID ... TYPE = E: no Graph lookup, so
# the server needs no Directory Readers), then adds the roles and EXECUTE. Every name goes through QUOTENAME; the
# values are parameters. Returns 'created', 'exists' or 'mismatch' (a user with that name and another SID; never
# dropped).
function Get-DatabaseUserSql {
    @'
DECLARE @sid varbinary(16) = CONVERT(varbinary(16), CAST(@clientId AS uniqueidentifier));
DECLARE @existing varbinary(85) = (SELECT sid FROM sys.database_principals WHERE name = @name);
IF @existing IS NOT NULL AND @existing <> @sid BEGIN SELECT N'mismatch'; RETURN; END
IF @existing IS NULL
BEGIN
    DECLARE @create nvarchar(max) = N'CREATE USER ' + QUOTENAME(@name) + N' WITH SID = ' + CONVERT(nvarchar(64), @sid, 1) + N', TYPE = E';
    EXEC (@create);
END
DECLARE @user nvarchar(300) = QUOTENAME(@name);
DECLARE @grant nvarchar(max) = N'ALTER ROLE db_datareader ADD MEMBER ' + @user + N'; ALTER ROLE db_datawriter ADD MEMBER ' + @user + N'; GRANT EXECUTE TO ' + @user + N';';
EXEC (@grant);
SELECT CASE WHEN @existing IS NULL THEN N'created' ELSE N'exists' END;
'@
}

# The read-back that proves a user: its type, its roles and whether it holds EXECUTE on the database.
function Get-DatabaseUserReadSql {
    @'
SELECT p.name, p.type_desc,
    ISNULL((SELECT STRING_AGG(r.name, N',') WITHIN GROUP (ORDER BY r.name)
            FROM sys.database_role_members m JOIN sys.database_principals r ON r.principal_id = m.role_principal_id
            WHERE m.member_principal_id = p.principal_id), N'') AS roles,
    CASE WHEN EXISTS (SELECT 1 FROM sys.database_permissions d
            WHERE d.grantee_principal_id = p.principal_id AND d.class = 0 AND d.permission_name = N'EXECUTE' AND d.state IN ('G', 'W'))
        THEN 1 ELSE 0 END AS can_execute
FROM sys.database_principals p WHERE p.name = @name;
'@
}

# Why an app's Key Vault references do not resolve. $Settings: the app's current setting names; $References: the
# items of GET {siteId}/config/configreferences/appsettings. Only the settings in $KeyVaultReferenceSettings that
# the app has are checked. Pure.
function Get-ReferenceProblems([string]$App, [string[]]$Settings, $References) {
    foreach ($setting in $KeyVaultReferenceSettings) {
        if ($Settings -notcontains $setting) { continue }
        $reference = @($References | Where-Object { "$($_.name)".Split('/')[-1] -eq $setting }) | Select-Object -First 1
        $status = if ($reference) { "$($reference.properties.status)" } else { '' }
        if ($status -ne 'Resolved') {
            $detail = if ($reference -and $reference.properties.details) { ": $($reference.properties.details)" } else { '' }
            "$App`: the Key Vault reference in '$setting' is $(if ($status) { "'$status'" } else { 'not reported' })$detail"
        }
    }
}


function Get-FirewallRuleName([string]$BuildId, [string]$UserName) {
    if (-not [string]::IsNullOrWhiteSpace($BuildId)) { return "eim-deploy-$BuildId" }
    "AllowDev-$UserName"
}

# --------------------------------------------------------------------------------------------
# Package
# --------------------------------------------------------------------------------------------

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

function Get-GatedTriggerState([string]$ResourceGroup, [string]$App, [string[]]$GatedFunctions) {
    $settings = ConvertTo-SettingMap (Get-AppSettingList $ResourceGroup $App)
    foreach ($function in $GatedFunctions) {
        $name = "AzureWebJobs.$function.Disabled"
        [pscustomobject]@{ Function = $function; Disabled = if ($settings.ContainsKey($name)) { $settings[$name] } else { '(not set)' } }
    }
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

# The resource as JSON, or $null when it does not exist (or cannot be read).
function Get-AzShow {
    $result = Invoke-AzProcess (@($args) + @('-o', 'json'))
    if ($result.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($result.StdOut)) { return $null }
    $result.StdOut | ConvertFrom-Json
}

# The owner resources a configuration builds on (input to Get-FoundationProblems).
function Get-FoundationState($Names) {
    $rg = $Names.ResourceGroup
    $group = Test-Az group show -n $rg
    $vault = if ($group) { Get-AzShow keyvault show -g $rg -n $Names.KeyVault } else { $null }
    $server = $group -and (Test-Az sql server show -g $rg -n $Names.SqlServer)
    @{
        ResourceGroup       = $group
        ServiceBusNamespace = $group -and (Test-Az servicebus namespace show -g $rg -n $Names.ServiceBusNamespace)
        KeyVault            = $null -ne $vault
        KeyVaultRbac        = $vault -and $vault.properties.enableRbacAuthorization -eq $true
        Plan                = $group -and (Test-Az appservice plan show -g $rg -n $Names.AppServicePlan)
        LogAnalytics        = $group -and (Test-Az resource show -g $rg -n $Names.LogAnalytics --resource-type Microsoft.OperationalInsights/workspaces)
        SqlServer           = $server
        SqlDatabase         = $server -and (Test-Az sql db show -g $rg -s $Names.SqlServer -n $Names.SqlDatabase)
    }
}

# The SQL server's state (input to Get-SqlPlan).
function Get-SqlState($Names) {
    $rg = $Names.ResourceGroup
    $server = Get-AzShow sql server show -g $rg -n $Names.SqlServer
    if (-not $server) { return @{ Server = $false; Database = $false; Managed = $false; Rules = @() } }
    $admin = @(Invoke-AzJson sql server ad-admin list -g $rg -s $Names.SqlServer) | Select-Object -First 1
    $adOnly = Get-AzShow sql server ad-only-auth get -g $rg -n $Names.SqlServer
    @{
        Server     = $true
        Managed    = Test-ManagedResource $server.tags
        Database   = Test-Az sql db show -g $rg -s $Names.SqlServer -n $Names.SqlDatabase
        AdminLogin = if ($admin) { "$($admin.login)" } else { '' }
        AdminSid   = if ($admin) { "$($admin.sid)" } else { '' }
        AdOnly     = $adOnly -and $adOnly.azureAdOnlyAuthentication -eq $true
        Rules      = @(Invoke-AzJson sql server firewall-rule list -g $rg -s $Names.SqlServer)
    }
}

# The possible outbound IPs of the configuration's apps (they share the owner's plan). Apps not yet created are skipped.
function Get-AppOutboundIps($Names, [bool]$IsOwner) {
    $ips = foreach ($app in (Get-SqlUserApps $Names $IsOwner)) {
        $result = Invoke-AzProcess @('webapp', 'show', '-g', $Names.ResourceGroup, '-n', $app.App, '--query', 'possibleOutboundIpAddresses', '-o', 'tsv')
        if ($result.ExitCode -eq 0) { "$($result.StdOut)".Trim() -split ',' }
    }
    @($ips | ForEach-Object { "$_".Trim() } | Where-Object { $_ -match '^\d{1,3}(\.\d{1,3}){3}$' } | Sort-Object -Unique)
}

function Test-VaultSecret([string]$Vault, [string]$Name) {
    Test-Az keyvault secret show --vault-name $Vault -n $Name --query id
}

# The value goes through a temp file deleted afterwards, so it never reaches a command line or the log.
function Set-VaultSecret([string]$Vault, [string]$Name, [string]$Value) {
    $file = Join-Path ([IO.Path]::GetTempPath()) "wtc-deploy-$([guid]::NewGuid()).txt"
    try {
        [IO.File]::WriteAllText($file, $Value, [System.Text.UTF8Encoding]::new($false))
        Invoke-Az keyvault secret set --vault-name $Vault -n $Name --file $file --encoding utf-8 -o none | Out-Null
    }
    finally { Remove-Item $file -ErrorAction SilentlyContinue -WhatIf:$false }
}

# Writes the Service Bus connection string to the Key Vault secret when it is missing or differs. Never prints it.
function Sync-ServiceBusSecret($Names) {
    $vault = $Names.KeyVault
    $label = "Key Vault secret '$ServiceBusSecretName' in '$vault'"
    if ($Preview) { Write-Host "  would write $label from $($Names.ServiceBusNamespace) when it is missing or differs"; return }

    $value = "$(Invoke-Az servicebus namespace authorization-rule keys list -g $Names.ResourceGroup --namespace-name $Names.ServiceBusNamespace `
            -n RootManageSharedAccessKey --query primaryConnectionString -o tsv)".Trim()
    if ([string]::IsNullOrWhiteSpace($value)) { throw "Could not read the connection string of Service Bus namespace '$($Names.ServiceBusNamespace)'." }
    $current = Invoke-AzProcess @('keyvault', 'secret', 'show', '--vault-name', $vault, '-n', $ServiceBusSecretName, '--query', 'value', '-o', 'tsv')
    if ($current.ExitCode -eq 0 -and "$($current.StdOut)".Trim() -eq $value) { Write-Host "  $label is current"; return }
    try { Set-VaultSecret $vault $ServiceBusSecretName $value }
    catch { throw "Could not write $label; the deployer needs secret get and set on the vault (an access policy)." }
    Write-Host "  $(if ($current.ExitCode -eq 0) { 'updated' } else { 'created' }) $label"
}

function Get-KeyVaultReferences([string]$ResourceGroup, [string]$App) {
    $siteId = Get-SiteId $ResourceGroup $App
    @((Invoke-AzRest get "$siteId/config/configreferences/appsettings").value)
}

function Get-AppPrincipalId([string]$ResourceGroup, [string]$App, [string]$Type) {
    $id = "$(Invoke-Az $Type identity show -g $ResourceGroup -n $App --query principalId -o tsv)".Trim()
    if (-not ($id -as [guid])) { throw "'$App' has no system-assigned identity; run 'infra' first." }
    $id
}

# The secret permissions an identity already holds on an access-policy vault; empty when it has no policy there.
function Get-VaultSecretPermissions($Names, [string]$PrincipalId) {
    @(Invoke-AzJson keyvault show -g $Names.ResourceGroup -n $Names.KeyVault --query "properties.accessPolicies[?objectId=='$PrincipalId'].permissions.secrets | [0]" |
            Where-Object { $_ })
}

# The identity's application (client) id, the SID of its database user. Read from ARM; Graph only as a fallback.
function Get-AppIdentityClientId([string]$ResourceGroup, [string]$App, [string]$PrincipalId) {
    $siteId = Get-SiteId $ResourceGroup $App
    $clientId = try { "$((Invoke-AzRest get "$siteId/providers/Microsoft.ManagedIdentity/identities/default" -ApiVersion $IdentityApiVersion).properties.clientId)" } catch { '' }
    if (-not ($clientId -as [guid])) { $clientId = "$(Invoke-Az ad sp show --id $PrincipalId --query appId -o tsv)".Trim() }
    if (-not ($clientId -as [guid])) { throw "Could not read the client id of '$App' (principal $PrincipalId)." }
    $clientId
}

# The message for a SQL connection that could not be opened (pure). Only a login failure points at the Entra admin
# group; any other error is reported with its SQL error number and message. The access token is never part of it,
# and is masked should the driver ever echo it. $Exception may wrap the SqlException (MethodInvocationException).
function Get-SqlSignInFailureMessage($Names, [string]$Identity, $Exception, [string]$Token) {
    $sqlError = $Exception
    while ($sqlError.InnerException -and -not $sqlError.PSObject.Properties['Number']) { $sqlError = $sqlError.InnerException }
    $number = if ($sqlError.PSObject.Properties['Number']) { $sqlError.Number } else { $null }
    $detail = "$($sqlError.Message)"
    if ($Token) { $detail = $detail.Replace($Token, '<access token>') }
    $target = "SQL server '$($Names.SqlServer)' (database '$($Names.SqlDatabase)')"

    if ($number -eq $SqlFirewallBlockedError -or $detail -match 'not allowed to access the server') {
        return "'$Identity' cannot reach $target`: this machine's IP is blocked by the firewall; run '-Component myip' first."
    }
    if ($number -eq $SqlLoginFailedError -or $detail -match 'Login failed') {
        return "'$Identity' cannot sign in to $target`: add it to the server's Entra admin group."
    }
    $code = if ($null -ne $number) { "SQL error $number" } else { 'error' }
    "'$Identity' cannot open a connection to $target ($code): $detail"
}

# Signed in as the deployer with the Azure CLI token; the deployer must be in the server's Entra admin group.
# Returns the rows of the last result set.
function Invoke-DeployerSql($Names, [string]$Query, [hashtable]$Parameters = @{}) {
    $token = "$(Invoke-Az account get-access-token --resource $SqlResource --query accessToken -o tsv)".Trim()
    $connection = [System.Data.SqlClient.SqlConnection]::new(
        "Server=tcp:$($Names.SqlServer).database.windows.net,1433;Initial Catalog=$($Names.SqlDatabase);Encrypt=True;TrustServerCertificate=False;Connect Timeout=30;")
    $connection.AccessToken = $token
    try {
        try { $connection.Open() }
        catch {
            $identity = "$(Invoke-Az account show --query user.name -o tsv)".Trim()
            throw (Get-SqlSignInFailureMessage $Names $identity $_.Exception $token)
        }
        $command = $connection.CreateCommand()
        $command.CommandText = $Query
        foreach ($key in $Parameters.Keys) { [void]$command.Parameters.AddWithValue($key, $Parameters[$key]) }
        $table = [System.Data.DataTable]::new()
        $reader = $command.ExecuteReader()
        # DataTable.Load moves the reader to the next result set itself, and closes it after the last.
        try { while (-not $reader.IsClosed) { $table = [System.Data.DataTable]::new(); $table.Load($reader) } }
        finally { $reader.Dispose() }
        @($table.Rows)
    }
    finally { $connection.Dispose() }
}

# --------------------------------------------------------------------------------------------
# Components
# --------------------------------------------------------------------------------------------

# The preflight checks that need no Azure call, run before signing in so a configuration mistake fails fast.
function Get-OfflinePreflightProblems($Config, [string[]]$Selected, $AllConfigs, [bool]$IsOwner, [string[]]$OwnedKeys = @()) {
    foreach ($problem in @(Get-PlaceholderProblems $Config $Selected $IsOwner $OwnedKeys)) { $problem }
    # Every configuration in the folder, with this one as merged with its overrides.
    foreach ($problem in @(Get-IsolationProblems $AllConfigs)) { $problem }
    # The queue list, with the configuration's own queue prefix (resolved as Get-IsolationProblems does).
    $queueOwner = if ($Config.uses -and $AllConfigs -and $AllConfigs.Contains("$($Config.uses)")) { $AllConfigs["$($Config.uses)"] } else { $null }
    foreach ($problem in @(Get-QueueConfigProblems $Config (Get-ResourceNames $Config $queueOwner -SkipValidation).QueuePrefix)) { $problem }
    if ($Selected -contains 'settings' -or $Selected -contains 'automation') {
        foreach ($problem in @(Get-TriggerConfigProblems $Config)) { $problem }
    }
    if ($Selected -contains 'sql') {
        foreach ($problem in @(Get-SqlFirewallProblems $Config)) { $problem }
    }

    $artifacts = @(Get-RequiredArtifacts $Selected $Config)
    if ($artifacts.Count -gt 0 -and -not $InPackage) {
        "no package at '$PackageRoot' (artifacts/manifest.json missing); build one with New-DeploymentPackage.ps1 and pass -PackagePath"
    }
    elseif ($artifacts.Count -gt 0) {
        foreach ($artifact in $artifacts) {
            if (-not (Test-Path (Join-Path $PackageArtifacts $artifact))) { "the package has no 'artifacts/$artifact'" }
        }
    }
    $manifest = Get-PackageManifest
    if ($manifest) {
        foreach ($problem in @(Compare-GatedFunctions @($manifest.automationQueueAndTimerFunctions) @($Config.automation.gatedFunctions))) { $problem }
    }
}

function Invoke-PreflightComponent($Config, $Names, [string[]]$Selected, [bool]$IsOwner, [string]$Owner, [string[]]$OwnedKeys = @()) {
    $problems = [System.Collections.Generic.List[string]]::new()
    $rg = $Names.ResourceGroup

    $manifest = Get-PackageManifest
    # The approver checks which build is being promoted.
    if ($manifest) { Write-Host "  package: version $($manifest.version), commit $($manifest.commit), built $($manifest.builtUtc)" }

    $foundation = Get-FoundationState $Names
    $result = Get-FoundationProblems $Names $foundation $IsOwner $Owner $OwnedKeys
    foreach ($note in $result.Notes) { Write-Host "  $note" }
    foreach ($problem in $result.Problems) { $problems.Add($problem) }

    if ($Selected -contains 'sql' -and -not $foundation.SqlServer) {
        foreach ($problem in @(Get-SqlAdminProblems $Config $Names)) { $problems.Add($problem) }
    }
    if (($Selected -contains 'schema' -or $Selected -contains 'sqlusers') -and $foundation.SqlServer) {
        $admins = @(Invoke-AzJson sql server ad-admin list -g $rg -s $Names.SqlServer)
        if ($admins.Count -eq 0) {
            $problems.Add("SQL server '$($Names.SqlServer)' has no Entra admin, so schema and sqlusers cannot sign in; set the admin group by hand (az sql server ad-admin create -g $rg -s $($Names.SqlServer) ...) first")
        }
    }
    # A configuration that owns its vault writes the secret in its own 'settings', as an owner does.
    if (-not $IsOwner -and $OwnedKeys -notcontains 'keyVault' -and $foundation.KeyVault -and -not (Test-VaultSecret $Names.KeyVault $ServiceBusSecretName)) {
        $problems.Add("Key Vault secret '$ServiceBusSecretName' does not exist in '$($Names.KeyVault)' (or the deployer cannot read it); run 'settings' for '$Owner' first")
    }

    $codeApps = @{ gateway = $Names.GatewayApp; backend = $Names.DashboardApi; automation = $Names.AutomationApp }
    $deployed = @($CodeComponents | Where-Object { $Selected -contains $_ -and $codeApps.ContainsKey($_) })
    if ($deployed -and $Selected -notcontains 'settings') {
        foreach ($component in $deployed) {
            $app = $codeApps[$component]
            if (-not (Test-WebApp $rg $app)) { $problems.Add("${app}: does not exist; run a manual run with deployInfra and applySettings"); continue }
            $current = Get-AppSettingList $rg $app
            $expected = switch ($component) {
                'gateway' { Get-GatewaySettings $Config $Names '<insights>' }
                'backend' { Get-DashboardApiSettings $Config $Names (Get-SimulationTargets $Config (Get-AllDeployConfigs $Config)) }
                'automation' {
                    $s = Get-AutomationSettings $Config $Names '<insights>'
                    $a = $Config.automation
                    $gated = Get-GatedSettings @($a.gatedFunctions) ($a.triggersEnabled -eq $true) @($a.keepDisabled)
                    foreach ($key in $gated.Keys) { $s[$key] = $gated[$key] }
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

# Existence and the eim-environment tag of every resource the configuration's infra plan covers. A configuration with
# "uses" probes the vault and workspace only when it owns them ($OwnedKeys), and never the plan. The queues are the
# configuration's serviceBusQueues (existence only).
function Get-ExistingResources($Config, $Names, [bool]$IsOwner, [string[]]$OwnedKeys = @()) {
    $rg = $Names.ResourceGroup
    $existing = @{ Queues = @{}; Managed = @{} }
    $probe = {
        param([string]$Key, [object[]]$Show)
        $resource = Get-AzShow @Show
        $existing[$Key] = $null -ne $resource
        $existing.Managed[$Key] = $resource -and (Test-ManagedResource $resource.tags)
    }
    $existing.ResourceGroup = Test-Az group show -n $rg
    if ($existing.ResourceGroup) {
        & $probe ServiceBusNamespace @('servicebus', 'namespace', 'show', '-g', $rg, '-n', $Names.ServiceBusNamespace)
        if ($IsOwner) {
            & $probe KeyVault @('keyvault', 'show', '-g', $rg, '-n', $Names.KeyVault)
            & $probe Plan @('appservice', 'plan', 'show', '-g', $rg, '-n', $Names.AppServicePlan)
            & $probe LogAnalytics @('resource', 'show', '-g', $rg, '-n', $Names.LogAnalytics, '--resource-type', 'Microsoft.OperationalInsights/workspaces')
            & $probe GatewayStorage @('storage', 'account', 'show', '-g', $rg, '-n', $Names.GatewayStorage)
            & $probe GatewayInsights @('resource', 'show', '-g', $rg, '-n', $Names.GatewayInsights, '--resource-type', 'Microsoft.Insights/components')
            & $probe GatewayApp @('functionapp', 'show', '-g', $rg, '-n', $Names.GatewayApp)
            & $probe DashboardApi @('webapp', 'show', '-g', $rg, '-n', $Names.DashboardApi)
            & $probe StaticWebApp @('staticwebapp', 'show', '-g', $rg, '-n', $Names.StaticWebApp)
        }
        else {
            if ($OwnedKeys -contains 'keyVault') { & $probe KeyVault @('keyvault', 'show', '-g', $rg, '-n', $Names.KeyVault) }
            if ($OwnedKeys -contains 'logAnalytics') {
                & $probe LogAnalytics @('resource', 'show', '-g', $rg, '-n', $Names.LogAnalytics, '--resource-type', 'Microsoft.OperationalInsights/workspaces')
            }
        }
        & $probe AutomationStorage @('storage', 'account', 'show', '-g', $rg, '-n', $Names.AutomationStorage)
        & $probe AutomationInsights @('resource', 'show', '-g', $rg, '-n', $Names.AutomationInsights, '--resource-type', 'Microsoft.Insights/components')
        & $probe AutomationApp @('functionapp', 'show', '-g', $rg, '-n', $Names.AutomationApp)
    }
    foreach ($queue in @(Get-ServiceBusQueues $Config $Names.QueuePrefix | ForEach-Object Name)) {
        $existing.Queues[$queue] = $existing.ServiceBusNamespace -and (Test-Az servicebus queue show -g $rg --namespace-name $Names.ServiceBusNamespace -n $queue)
    }
    $existing
}

function Invoke-ResourceJsonStep($Step) {
    $file = Write-TempJson $Step.Body
    try { $stepArgs = $Step.Args + @('--properties', "@$file", '-o', 'none'); Invoke-Az @stepArgs | Out-Null }
    finally { Remove-Item $file -ErrorAction SilentlyContinue -WhatIf:$false }
}

function Invoke-InfraComponent($Config, $Names, [bool]$IsOwner, [string[]]$OwnedKeys = @()) {
    $rg = $Names.ResourceGroup
    $existing = Get-ExistingResources $Config $Names $IsOwner $OwnedKeys
    $steps = @(Get-InfraPlan $Config $Names $existing $IsOwner $OwnedKeys)
    # An owned workspace does not move an Automation Insights that already exists; warn, never fail.
    $placement = {
        if ($IsOwner -or $OwnedKeys -notcontains 'logAnalytics' -or -not $existing.AutomationInsights) { return }
        $workspaceId = try { "$(Invoke-Az resource show -g $rg -n $Names.AutomationInsights --resource-type Microsoft.Insights/components --query properties.WorkspaceResourceId -o tsv)" } catch { '' }
        foreach ($warning in @(Get-PlacementWarnings $Names $workspaceId)) { Write-DeployWarning $warning }
    }

    if ($Preview) {
        foreach ($step in $steps) { Write-Host "  would $($step.Label)" }
        if (-not ($steps | Where-Object { $_.Label -like 'create *' })) { Write-Host "  every resource exists; only properties, identities and vault access would be converged" }
        & $placement
        return
    }

    foreach ($step in $steps) {
        switch ($step.Kind) {
            'az' { $stepArgs = $step.Args; Invoke-Az @stepArgs | Out-Null }
            'json' { Invoke-ResourceJsonStep $step }
            'insights' {
                $workspaceId = "$(Invoke-Az resource show -g $rg -n $Names.LogAnalytics --resource-type Microsoft.OperationalInsights/workspaces --query id -o tsv)"
                if ([string]::IsNullOrWhiteSpace($workspaceId)) { throw "Log Analytics workspace '$($Names.LogAnalytics)' not found." }
                $step.Body.properties.WorkspaceResourceId = $workspaceId
                Invoke-ResourceJsonStep $step
            }
            'runtime' {
                $app = $step.Args[0]
                $runtime = "$(Invoke-Az functionapp config show -g $rg -n $app --query linuxFxVersion -o tsv)"
                if ($runtime -ine $FunctionRuntime) {
                    Invoke-Az functionapp config set -g $rg -n $app --linux-fx-version $FunctionRuntime -o none | Out-Null
                    Write-Host "  $app runtime was '$runtime'; set to $FunctionRuntime"
                }
            }
            'keyvault' {
                try { $stepArgs = $step.Args; Invoke-Az @stepArgs | Out-Null }
                catch {
                    # A removed vault stays soft-deleted, holding its name, until purged.
                    if ("$_" -notmatch '(?i)soft.?delete|deleted state|recover') { throw }
                    try { Invoke-Az keyvault recover -g $rg -n $Names.KeyVault -l $Config.location -o none | Out-Null }
                    catch { throw "Key Vault '$($Names.KeyVault)' is soft-deleted and could not be recovered. Purge it (az keyvault purge -n $($Names.KeyVault)) or choose another name. $_" }
                    Write-Host "  recovered soft-deleted Key Vault $($Names.KeyVault)"
                }
            }
            'policy' {
                $principalId = Get-AppPrincipalId $rg $step.Body.App $step.Body.Type
                $permissions = Get-SecretPermissionUnion @(Get-VaultSecretPermissions $Names $principalId)
                if ($null -eq $permissions) {
                    Write-Host "  $($step.Body.App) already has secret 'get' on Key Vault $($Names.KeyVault)"
                }
                else {
                    $stepArgs = Resolve-PolicyStepArgs $step.Args $principalId $permissions
                    try { Invoke-Az @stepArgs | Out-Null }
                    catch {
                        throw "Could not grant '$($step.Body.App)' secret 'get' on Key Vault '$($Names.KeyVault)'. Changing access policies needs Key Vault Contributor or Contributor on the vault. $_"
                    }
                }
            }
        }
        Write-Host "  $($step.Label)"
    }
    & $placement
}

# Owners only (Resolve-Components). Runs after infra: the firewall rules need the apps' outbound IPs.
function Invoke-SqlComponent($Config, $Names) {
    $plan = Get-SqlPlan $Config $Names (Get-SqlState $Names) (Get-AppOutboundIps $Names $true)
    foreach ($line in $plan.Report) { Write-Host "  $line" }
    foreach ($warning in $plan.Warnings) { Write-DeployWarning $warning }
    if ($Preview) {
        foreach ($step in $plan.Steps) { Write-Host "  would $($step.Label)" }
        if ($plan.Steps.Count -eq 0) { Write-Host "  nothing to change" }
        return
    }
    foreach ($step in $plan.Steps) {
        $stepArgs = $step.Args
        Invoke-Az @stepArgs | Out-Null
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

function Invoke-SettingsComponent($Config, $Names, $AllConfigs, [bool]$IsOwner, [string]$Owner, [string[]]$OwnedKeys = @()) {
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

    if ($IsOwner) {
        # Built before any write, so a configuration error (such as a wrong simulation queue prefix) changes nothing.
        $dashboard = Get-DashboardApiSettings $Config $Names (Get-SimulationTargets $Config $AllConfigs)
        # Before the app settings that reference it.
        Sync-ServiceBusSecret $Names
        if (& $target $Names.GatewayApp) {
            $gateway = Get-GatewaySettings $Config $Names (& $insights $Names.GatewayInsights)
            Update-AppSettings $rg $Names.GatewayApp $gateway @() 'functionapp'
        }

        # In place: a kept dashboard API keeps every other setting; the Simulation__* settings other than
        # Simulation__Enabled go when simulation is off.
        if (& $target $Names.DashboardApi) {
            $current = Get-AppSettingList $rg $Names.DashboardApi
            $stale = @(Get-StaleVerificationTargetSettings $dashboard $current) + @(Get-StaleSimulationSettings $dashboard $current)
            Update-AppSettings $rg $Names.DashboardApi $dashboard $stale 'webapp'
        }
    }
    elseif ($OwnedKeys -contains 'keyVault') {
        # Its own vault: the owner's namespace connection string, copied before the setting that references it.
        Sync-ServiceBusSecret $Names
    }
    elseif (-not (Test-VaultSecret $Names.KeyVault $ServiceBusSecretName)) {
        throw "Key Vault secret '$ServiceBusSecretName' does not exist in '$($Names.KeyVault)' (or the deployer cannot read it); run 'settings' for '$Owner' first."
    }

    $app = $Names.AutomationApp
    if (-not (& $target $app)) { return }
    $current = Get-AppSettingList $rg $app
    $settings = Get-AutomationSettings $Config $Names (& $insights $Names.AutomationInsights)
    $a = $Config.automation
    # Every gated trigger on every run, so a change made in the portal is put back.
    $gated = Get-GatedSettings @($a.gatedFunctions) ($a.triggersEnabled -eq $true) @($a.keepDisabled)
    foreach ($key in $gated.Keys) { $settings[$key] = $gated[$key] }
    $enabled = @($gated.Values | Where-Object { $_ -eq 'false' }).Count
    Write-Host "  $app`: gated triggers $enabled enabled, $($gated.Count - $enabled) disabled (automation.triggersEnabled is $($a.triggersEnabled -eq $true))"
    $retired = Get-RetiredSettingsToDelete @($a.retiredSettings) $current
    Update-AppSettings $rg $app $settings $retired 'functionapp'
}

function Invoke-MyIpComponent($Config, $Names, [switch]$Remove) {
    $server = $Names.SqlServer
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

# The migrator signs in as the deployer: Active Directory Default with the Azure CLI credential only.
function Invoke-SchemaComponent($Config, $Names) {
    $migrator = Get-PackageArtifact 'dbmigrator/ARM.Integration.DbMigrator.exe'
    $arguments = @('--timeout', '1800')
    if ($Preview) { $arguments += '--whatif' }
    # Puts the SQL token in the Azure CLI cache while the pipeline's federated sign-in is fresh.
    Invoke-Az account get-access-token --resource $SqlResource --query expiresOn -o tsv | Out-Null
    $environment = @{
        EIMDB_MIGRATOR_CONNECTION = Get-SqlConnectionString $Names 'Active Directory Default'
        AZURE_TOKEN_CREDENTIALS   = 'AzureCliCredential'
    }
    $exitCode = (Invoke-Native $migrator $arguments -Environment $environment).ExitCode
    if ($exitCode -eq 0) { return }
    $message = "Migration $(if ($Preview) { 'preview ' })failed with exit code $exitCode. If it could not connect, run '-Component myip' first and check that the deployer is in the Entra admin group of SQL server '$($Names.SqlServer)'."
    if ($Preview) { Write-DeployWarning $message } else { throw $message }
}

# A contained database user for each deployed app's identity, with the roles and EXECUTE. Never drops a user.
function Invoke-SqlUsersComponent($Config, $Names, [bool]$IsOwner) {
    $rg = $Names.ResourceGroup
    $apps = @(Get-SqlUserApps $Names $IsOwner)
    $grants = "$($DatabaseRoles -join ', '), EXECUTE"
    if ($Preview) {
        foreach ($app in $apps) { Write-Host "  would ensure database user [$($app.App)] in $($Names.SqlDatabase) on $($Names.SqlServer): $grants" }
        return
    }

    foreach ($app in $apps) {
        $principalId = Get-AppPrincipalId $rg $app.App $app.Type
        $clientId = Get-AppIdentityClientId $rg $app.App $principalId
        $outcome = "$(@(Invoke-DeployerSql $Names (Get-DatabaseUserSql) @{ '@name' = $app.App; '@clientId' = $clientId })[0][0])"
        if ($outcome -eq 'mismatch') {
            throw "Database user [$($app.App)] in '$($Names.SqlDatabase)' belongs to another identity (the app was probably recreated). The script never drops a user: drop it by hand, then run sqlusers again."
        }
        $row = @(Invoke-DeployerSql $Names (Get-DatabaseUserReadSql) @{ '@name' = $app.App })[0]
        $execute = if ([int]$row.can_execute -eq 1) { 'EXECUTE granted' } else { 'EXECUTE missing' }
        Write-Host "  [$($row.name)] $outcome`: $($row.type_desc), roles $($row.roles), $execute"
    }
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
            $message = "Refusing to deploy to '$app': it lacks $($missing -join ', '). Without them its gated triggers would follow no configuration; run 'settings' first."
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

# What 'verify' reports, in order (pure). Kind 'app' gets Write-AppReport, plus Write-GatedReport when Gated and
# the Key Vault reference report; kind 'staticWebApp' gets the SWA host and its linked backend. An owner reports
# its gateway, dashboard API, static web app and Automation app; a configuration with "uses" its Automation app.
function Get-VerifyTargets($Config, $Names, [bool]$IsOwner) {
    $target = { param($Kind, $Name, $Label, $Gated) [pscustomobject]@{ Kind = $Kind; Name = "$Name"; Label = $Label; Gated = $Gated } }
    if ($IsOwner) {
        & $target 'app' $Names.GatewayApp 'gateway' $false
        & $target 'app' $Names.DashboardApi 'dashboard API' $false
        & $target 'staticWebApp' $Names.StaticWebApp 'dashboard SPA' $false
    }
    & $target 'app' $Names.AutomationApp 'Automation' $true
}

# A Key Vault reference's status with the vault it resolves against, e.g. "Resolved (kv-eim-wtc-test)". Pure.
function Format-ReferenceStatus($Reference) {
    $status = "$($Reference.properties.status)"
    $vault = "$($Reference.properties.vaultName)"
    if ([string]::IsNullOrWhiteSpace($vault)) { $status } else { "$status ($vault)" }
}

function Write-ReferenceReport([string]$ResourceGroup, [string]$App) {
    if (-not (Test-WebApp $ResourceGroup $App)) { return }
    $settings = @(Get-AppSettingList $ResourceGroup $App | ForEach-Object { "$($_.name)" } | Where-Object { $KeyVaultReferenceSettings -contains $_ })
    if ($settings.Count -eq 0) { return }
    $references = Get-KeyVaultReferences $ResourceGroup $App
    Write-Host "    Key Vault references"
    foreach ($setting in $settings) {
        $reference = @($references | Where-Object { "$($_.name)".Split('/')[-1] -eq $setting }) | Select-Object -First 1
        Write-Host ("      {0,-40} {1}" -f $setting, $(if ($reference) { Format-ReferenceStatus $reference } else { '(not reported)' }))
    }
}

# Report only: never fails the run.
function Invoke-VerifyComponent($Config, $Names, [bool]$IsOwner) {
    $rg = $Names.ResourceGroup
    if ($Preview) { Write-Host "  would report host, state, Always On, plan, outbound IPs, trigger states and Key Vault references"; return }
    $gated = @($Config.automation.gatedFunctions)
    try {
        foreach ($target in @(Get-VerifyTargets $Config $Names $IsOwner)) {
            if ($target.Kind -eq 'staticWebApp') {
                if (Test-Az staticwebapp show -g $rg -n $target.Name) {
                    $swaHost = "$(Invoke-Az staticwebapp show -g $rg -n $target.Name --query defaultHostname -o tsv)"
                    $backend = try { @(Invoke-AzJson staticwebapp backends show -g $rg -n $target.Name) | ForEach-Object { ("$($_.backendResourceId)" -split '/')[-1] } } catch { '(none)' }
                    Write-Host "  $($target.Name) ($($target.Label))"
                    Write-Host "    host       https://$swaHost  linked backend: $($backend -join ', ')"
                }
                else { Write-Host "  $($target.Name): not found" }
                continue
            }
            # Write-AppReport prints "not found" for a missing app; the other reports then print nothing.
            Write-AppReport $rg $target.Name $target.Label
            if ($target.Gated) { Write-GatedReport $rg $target.Name $gated }
            Write-ReferenceReport $rg $target.Name
        }
    }
    catch { Write-DeployWarning "verify could not complete: $($_.Exception.Message)" }
}

function Invoke-HealthComponent($Config, $Names, [bool]$IsOwner) {
    $rg = $Names.ResourceGroup
    if ($Preview) { Write-Host "  would wait for the Function App hosts to report Running, check the dashboard API and SPA and the Key Vault references"; return }

    $problems = [System.Collections.Generic.List[string]]::new()
    $functionApps = if ($IsOwner) { @($Names.GatewayApp, $Names.AutomationApp) } else { @($Names.AutomationApp) }
    foreach ($app in $functionApps) {
        if (-not (Test-WebApp $rg $app)) { $problems.Add("${app}: not found"); continue }
        $state = Wait-FunctionHostRunning (Get-SiteId $rg $app)
        if ($state -eq 'Running') { Write-Host "  $app`: host Running" }
        else { $problems.Add("${app}: host is '$state' after 5 minutes, not Running") }
    }

    if ($IsOwner) {
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

    # The Key Vault references must resolve, or the apps run without their Service Bus connection.
    $referenceApps = if ($IsOwner) { @($Names.DashboardApi, $Names.AutomationApp) } else { @($Names.AutomationApp) }
    foreach ($app in $referenceApps) {
        if (-not (Test-WebApp $rg $app)) { continue }
        $settings = @(Get-AppSettingList $rg $app | ForEach-Object { "$($_.name)" })
        if (-not ($settings | Where-Object { $KeyVaultReferenceSettings -contains $_ })) { continue }
        $referenceProblems = @(Get-ReferenceProblems $app $settings (Get-KeyVaultReferences $rg $app))
        foreach ($problem in $referenceProblems) { $problems.Add($problem) }
        if ($referenceProblems.Count -eq 0) { Write-Host "  $app`: Key Vault references resolved" }
    }

    if ($problems.Count -gt 0) { throw "Health check failed:`n  - $($problems -join "`n  - ")" }
    Write-Host "  all checks passed"
}

# --------------------------------------------------------------------------------------------
# Main (skipped when dot-sourced, e.g. by Test-DeployWilson.ps1)
# --------------------------------------------------------------------------------------------

if ($MyInvocation.InvocationName -ne '.') {
    if ([string]::IsNullOrWhiteSpace($Environment)) { throw "-Environment is required: the name of a script/deployment/config/deploy.<name>.json file." }
    $config = Get-DeployConfig $Environment $ConfigDirectory $ConfigOverridesFile $env:EIM_DEPLOY_CONFIG_OVERRIDES
    $allConfigs = Get-AllDeployConfigs $config $ConfigDirectory
    $usesProblems = @(Get-UsesProblems $config $allConfigs)
    if ($usesProblems.Count -gt 0) { throw "Pre-flight check failed; nothing was changed:`n  - $($usesProblems -join "`n  - ")" }
    $isOwner = [string]::IsNullOrWhiteSpace("$($config.uses)")
    $ownerName = if ($isOwner) { "$($config.environment)" } else { "$($config.uses)" }
    $owner = if ($isOwner) { $null } else { $allConfigs[$ownerName] }
    $names = Get-ResourceNames $config $owner
    $ownedKeys = @(Get-OwnedNameKeys $config $owner)
    $selected = @(Resolve-Components $Component $isOwner $ownerName)

    Write-Host "Configuration: $Environment$(if (-not $isOwner) { " (uses $ownerName)" })$(if ($InPackage) { "  package: $PackageRoot" })$(if ($Preview) { '  (preview - no changes)' })"
    Write-Host "Components: $($selected -join ', ')"
    Format-NameListing $names $ownedKeys | ForEach-Object { Write-Host $_ }
    # Before the first az call (Assert-AzureCli runs az account show and set).
    if ($selected -contains 'preflight') {
        $offline = @(Get-OfflinePreflightProblems $config $selected $allConfigs $isOwner $ownedKeys)
        if ($offline.Count -gt 0) { throw "Pre-flight check failed; nothing was changed:`n  - $($offline -join "`n  - ")" }
    }
    Assert-AzureCli $config

    foreach ($name in $selected) {
        Write-Host "== $name"
        switch ($name) {
            'preflight'  { Invoke-PreflightComponent $config $names $selected $isOwner $ownerName $ownedKeys }
            'infra'      { Invoke-InfraComponent $config $names $isOwner $ownedKeys }
            'sql'        { Invoke-SqlComponent $config $names }
            'settings'   { Invoke-SettingsComponent $config $names $allConfigs $isOwner $ownerName $ownedKeys }
            'myip'       { Invoke-MyIpComponent $config $names }
            'schema'     { Invoke-SchemaComponent $config $names }
            'sqlusers'   { Invoke-SqlUsersComponent $config $names $isOwner }
            'myipremove' { Invoke-MyIpComponent $config $names -Remove }
            'gateway'    { Invoke-GatewayComponent $config $names }
            'backend'    { Invoke-BackendComponent $config $names }
            'frontend'   { Invoke-FrontendComponent $config $names }
            'automation' { Invoke-AutomationComponent $config $names }
            'verify'     { Invoke-VerifyComponent $config $names $isOwner }
            'health'     { Invoke-HealthComponent $config $names $isOwner }
        }
    }
}
