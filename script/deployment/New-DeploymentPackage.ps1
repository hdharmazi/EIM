<#
.SYNOPSIS
    Builds a deployment package that runs with only PowerShell 7 and the Azure CLI.

.DESCRIPTION
    Writes artifacts/package/wtc-eim-deploy-<version>/ and a zip of it, containing:
      Deploy-Wilson.ps1, config/deploy.base.json and config/deploy.<name>.json for each -Environment
      artifacts/wilson-automation.zip, gateway.zip, dashboard-api.zip   (dotnet publish -c Release)
      artifacts/dashboard-frontend.<name>.zip   the SPA, for each shared-scope configuration (nonprod, prdshared)
      artifacts/dbmigrator/ARM.Integration.DbMigrator.exe   self-contained win-x64 migrator
      artifacts/manifest.json, README.txt

    The same binaries go to every configuration in the package. The SPA is built with the configuration's Entra
    values; a configuration whose Entra values are still <...> placeholders gets no SPA (the frontend step then
    fails preflight). Secrets are never part of the package.

.EXAMPLE
    ./script/deployment/New-DeploymentPackage.ps1

.EXAMPLE
    ./script/deployment/New-DeploymentPackage.ps1 -Environment nonprod, bat -Version local
#>
# After the help block: a #Requires line above it stops Get-Help from finding the help.
#Requires -Version 7.2
param(
    [ValidatePattern('^[a-z0-9]+$')]
    [string[]]$Environment = @('nonprod', 'bat', 'test'),

    [string]$OutputPath,
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

# Through System.Diagnostics.Process for the same reason as Deploy-Wilson.ps1: '&' can fail after dotnet has run.
function Invoke-Tool([string]$Name, [string[]]$ArgumentList, [hashtable]$Environment = @{}, [switch]$Capture) {
    $command = Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $command) { throw "'$Name' not found on PATH." }
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($command.Source)
    foreach ($argument in $ArgumentList) { $startInfo.ArgumentList.Add($argument) }
    $startInfo.UseShellExecute = $false
    $startInfo.WorkingDirectory = $RepoRoot
    foreach ($key in $Environment.Keys) { $startInfo.Environment[$key] = "$($Environment[$key])" }
    $startInfo.RedirectStandardOutput = [bool]$Capture
    $process = [System.Diagnostics.Process]::Start($startInfo)
    try {
        $stdout = if ($Capture) { $process.StandardOutput.ReadToEnd() }
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "$Name $($ArgumentList -join ' ') failed with exit code $($process.ExitCode)." }
        if ($Capture) { $stdout.Trim() }
    }
    finally { $process.Dispose() }
}

function New-Zip([string]$SourceDir, [string]$ZipPath) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
    # ZipFile rather than Compress-Archive, which skips dot-folders such as .azurefunctions.
    [System.IO.Compression.ZipFile]::CreateFromDirectory($SourceDir, $ZipPath)
}

function Test-PackagePlaceholder([string]$Value) { [string]::IsNullOrWhiteSpace($Value) -or $Value -match '^<.*>$' }

# Every function name in a functions.metadata file (JSON text).
function Get-FunctionNames([string]$MetadataJson) {
    @($MetadataJson | ConvertFrom-Json | ForEach-Object { "$($_.name)" } | Where-Object { $_ } | Sort-Object)
}

# Queue, timer and other non-HTTP triggers: the functions that consume work as soon as the app starts, and so the
# ones automation.gatedFunctions must list. Durable orchestration/activity/entity triggers only run when started.
function Get-QueueAndTimerFunctions([string]$MetadataJson) {
    $ignored = 'httpTrigger', 'orchestrationTrigger', 'activityTrigger', 'entityTrigger'
    @($MetadataJson | ConvertFrom-Json | Where-Object {
            $trigger = @($_.bindings | Where-Object { "$($_.type)" -like '*Trigger' })[0].type
            $trigger -and $ignored -notcontains $trigger
        } | ForEach-Object { "$($_.name)" } | Sort-Object)
}

# Same merge as Deploy-Wilson.ps1: objects key by key, anything else replaced.
function Merge-PackageConfig($Target, $Patch) {
    foreach ($property in $Patch.PSObject.Properties) {
        $current = $Target.PSObject.Properties[$property.Name]
        if ($current -and $current.Value -is [System.Management.Automation.PSCustomObject] -and $property.Value -is [System.Management.Automation.PSCustomObject]) {
            Merge-PackageConfig $current.Value $property.Value
            continue
        }
        $Target | Add-Member -NotePropertyName $property.Name -NotePropertyValue $property.Value -Force
    }
}

# deploy.base.json merged with deploy.<name>.json, as Deploy-Wilson.ps1 reads it (without the overrides layers).
function Get-PackageConfig([string]$Name, [string]$Directory) {
    $path = Join-Path $Directory "deploy.$Name.json"
    if ($Name -eq 'base' -or -not (Test-Path $path)) { throw "No configuration at '$path'." }
    $own = Get-Content $path -Raw | ConvertFrom-Json
    if ($own.environment -ne $Name) { throw "'$path' declares environment '$($own.environment)'." }
    $config = Get-Content (Join-Path $Directory 'deploy.base.json') -Raw | ConvertFrom-Json
    Merge-PackageConfig $config $own
    $config
}

# The packaged configurations grouped by stamp (same sharedSuffix), in package order. Each stamp names its shared
# configuration (from the package, or else from the configuration folder) and its resource group.
function Get-PackageStamps([string[]]$Environments, [string]$Directory) {
    $configs = [ordered]@{}
    foreach ($name in $Environments) { $configs[$name] = Get-PackageConfig $name $Directory }
    $stamps = [ordered]@{}
    foreach ($name in $configs.Keys) {
        $suffix = "$($configs[$name].sharedSuffix)"
        if (-not $stamps.Contains($suffix)) {
            $stamps[$suffix] = [pscustomobject]@{
                Suffix        = $suffix
                Production    = $configs[$name].production -eq $true
                ResourceGroup = "$($configs[$name].names.resourceGroup)"
                Shared        = $null
                Environments  = [System.Collections.Generic.List[string]]::new()
            }
        }
        if ($configs[$name].scope -eq 'shared') { $stamps[$suffix].Shared = $name } else { $stamps[$suffix].Environments.Add($name) }
    }
    foreach ($stamp in $stamps.Values) {
        if ($stamp.Shared) { continue }
        $stamp.Shared = @(Get-ChildItem -Path $Directory -Filter 'deploy.*.json' | ForEach-Object { $_.BaseName -replace '^deploy\.', '' } |
                Where-Object { $_ -ne 'base' } | Where-Object {
                    $other = Get-PackageConfig $_ $Directory
                    "$($other.sharedSuffix)" -eq $stamp.Suffix -and $other.scope -eq 'shared'
                } | Sort-Object)[0]
        if (-not $stamp.Shared) { $stamp.Shared = '<shared configuration>' }
    }
    @($stamps.Values)
}

function Get-PackageReadme([string]$Version, [string]$Commit, [string[]]$Environments, [string]$ConfigDirectory = (Join-Path $PSScriptRoot 'config')) {
    $stamps = @(Get-PackageStamps $Environments $ConfigDirectory)
    $groups = @($stamps.ResourceGroup | Where-Object { $_ } | Sort-Object -Unique)
    $configurations = foreach ($stamp in $stamps) {
        $label = if ($stamp.Production) { 'production stamp' } else { 'non-production stamp' }
        "  $($stamp.Shared)  shared scope ($label, resource group $($stamp.ResourceGroup)): App Service plan, Log"
        '           Analytics, gateway, dashboard API and SPA, database migrations.'
        if ($stamp.Environments.Count) {
            "  $($stamp.Environments -join ', ')  environment scope: the five queues and the Automation Function App."
        }
        if ($stamp.Production) {
            "  The production stamp shares nothing with the non-production one: 'remove' never runs against it and"
            '  the dashboard has no Simulation console. Its Service Bus namespace, SQL server and database exist'
            '  before the first run (docs/deployment/production-go-live.md); the script never creates them.'
        }
    }
    $steps = foreach ($stamp in $stamps) {
        $first = if ($stamp.Environments.Count) { $stamp.Environments[0] } else { '<environment>' }
        ''
        "  Stamp $($stamp.Shared)$(if ($stamp.Environments.Count) { " ($($stamp.Environments -join ', '))" }):"
        '  1. Preview, changes nothing:'
        "       .\Deploy-Wilson.ps1 -Environment $($stamp.Shared) -Component all -WhatIf"
        '  2. Shared resources, settings, migrations and the shared apps:'
        "       .\Deploy-Wilson.ps1 -Environment $($stamp.Shared) -Component all"
        "  3. Each environment$(if ($stamp.Environments.Count) { " ($($stamp.Environments -join ', then '))" }):"
        "       .\Deploy-Wilson.ps1 -Environment $first -Component all"
        '     The queue and timer triggers of a new Automation app stay disabled until cutover:'
        "       .\Deploy-Wilson.ps1 -Environment $first -Component cutover     $(if ($stamp.Production) { '(no legacy app, so no rollback)' } else { '(rollback undoes it)' })"
        if (-not $stamp.Production) {
            '  4. Gateway keys (once, after the shared apps run):'
            "       .\Deploy-Wilson.ps1 -Environment $($stamp.Shared) -Component cutover"
        }
    }
    @"
Wilson EIM deployment package $Version (commit $Commit)
Configurations: $($Environments -join ', ')

REQUIREMENTS
  - PowerShell 7.2 or later (pwsh) and Azure CLI 2.60 or later, on Windows.
  - Node.js LTS for the frontend step only (it uses the Static Web Apps CLI through npx).
  - az login as a user with Contributor on the resource group of the stamp you deploy: $($groups -join ', ').
  - Secrets come from the environment variables EIM_DEPLOY_EIMDB and EIM_DEPLOY_SERVICEBUS, or a hidden
    prompt when they are not set. They are never written to disk in this folder.

CONFIGURATIONS
$($configurations -join "`n")
  These are deployment configurations, not ENVIRONMENT values in eimdb (DEV2, for example, has no
  Function App). Development runs locally; no configuration deploys a development environment.

STEPS
  Open PowerShell 7 (pwsh) in this folder and run the commands there; 'pwsh -File' would not accept
  the comma-separated component lists.

  0. Check the configuration: config\deploy.base.json and config\deploy.<name>.json. Nothing there is
     secret. Every command that changes something first runs 'preflight', which stops before any
     change on unfilled <...> values, missing secrets, missing package files and (for code-only runs)
     settings an app lacks. Run it alone with:
       .\Deploy-Wilson.ps1 -Environment <name> -Component preflight
$($steps -join "`n")

  'verify' prints the state of every app; 'health' fails unless the hosts run and the dashboard answers.

REMOVE (non-production only, after cutover and the observation window; operator machine only)
       .\Deploy-Wilson.ps1 -Environment <name> -Component remove -WhatIf
       .\Deploy-Wilson.ps1 -Environment <name> -Component remove
  Deletes only the resources listed under "legacy" in the configuration. A shared configuration may also
  list retired Automation apps in legacy.retiredAutomationApps; remove refuses until every gated function
  on them is disabled (see the runbook). Remove refuses every production configuration.

Runbooks: docs/deployment/resource-rename-cutover.md and docs/deployment/production-go-live.md in the
repository.
Full help: Get-Help .\Deploy-Wilson.ps1 -Full
"@
}

# Main (skipped when dot-sourced, e.g. by Test-DeployWilson.ps1)
if ($MyInvocation.InvocationName -ne '.') {
    if (-not $OutputPath) { $OutputPath = Join-Path $RepoRoot 'artifacts/package' }
    $configDir = Join-Path $PSScriptRoot 'config'
    $basePath = Join-Path $configDir 'deploy.base.json'
    if (-not (Test-Path $basePath)) { throw "No base configuration at '$basePath'." }

    $configs = [ordered]@{}
    foreach ($name in $Environment) {
        $configs[$name] = @{ Path = (Join-Path $configDir "deploy.$name.json"); Config = (Get-PackageConfig $name $configDir) }
    }

    $commit = Invoke-Tool 'git' @('rev-parse', '--short', 'HEAD') -Capture
    if (Invoke-Tool 'git' @('status', '--porcelain') -Capture) { Write-Warning "The working tree has uncommitted changes; they are included in the package." }
    if (-not $Version) { $Version = "$(Get-Date -AsUTC -Format 'yyyyMMdd-HHmm')-$commit" }

    $packageDir = Join-Path $OutputPath "wtc-eim-deploy-$Version"
    $artifacts = Join-Path $packageDir 'artifacts'
    $work = Join-Path $OutputPath "work-$Version"
    foreach ($dir in $packageDir, $work) { if (Test-Path $dir) { Remove-Item $dir -Recurse -Force } }
    New-Item -ItemType Directory -Path $artifacts, (Join-Path $packageDir 'config'), $work | Out-Null
    $build = @{ MSBUILDTERMINALLOGGER = 'off' }

    try {
        $projects = [ordered]@{
            'wilson-automation' = 'Wilson.Integration.Automation/Wilson.Integration.Automation.csproj'
            'gateway'           = 'ARM.Integration.D365FSC.Gateway/ARM.Integration.D365FSC.Gateway.csproj'
            'dashboard-api'     = 'ARM.Integration.Dashboard.API/ARM.Integration.Dashboard.API.csproj'
        }
        $queueAndTimerFunctions = @()
        $gatewayFunctions = @()
        foreach ($label in $projects.Keys) {
            Write-Host "== publish $label"
            $publishDir = Join-Path $work $label
            Invoke-Tool 'dotnet' @('publish', (Join-Path $RepoRoot $projects[$label]), '--configuration', 'Release', '--output', $publishDir, '-nologo', '-v', 'q') $build
            New-Zip $publishDir (Join-Path $artifacts "$label.zip")
            $metadata = Join-Path $publishDir 'functions.metadata'
            if ($label -eq 'wilson-automation') { $queueAndTimerFunctions = Get-QueueAndTimerFunctions (Get-Content $metadata -Raw) }
            if ($label -eq 'gateway') { $gatewayFunctions = Get-FunctionNames (Get-Content $metadata -Raw) }
        }

        Write-Host "== publish dbmigrator (self-contained win-x64)"
        Invoke-Tool 'dotnet' @('publish', (Join-Path $RepoRoot 'ARM.Integration.DbMigrator/ARM.Integration.DbMigrator.csproj'),
            '--configuration', 'Release', '--runtime', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true',
            '-p:IncludeNativeLibrariesForSelfExtract=true', '--output', (Join-Path $artifacts 'dbmigrator'), '-nologo', '-v', 'q') $build

        # The SPA belongs to the shared scope: one dashboard serves every environment.
        $frontendDir = Join-Path $RepoRoot 'ARM.Integration.Dashboard.Frontend'
        $frontends = @()
        $shared = @($configs.Keys | Where-Object { $configs[$_].Config.scope -eq 'shared' })
        if ($shared) {
            Write-Host "== npm ci (dashboard frontend)"
            Invoke-Tool 'npm' @('--prefix', $frontendDir, 'ci')
        }
        foreach ($name in $shared) {
            $entra = $configs[$name].Config.entra
            if (@('tenantId', 'clientId', 'scope' | Where-Object { Test-PackagePlaceholder $entra.$_ }).Count -gt 0) {
                Write-Warning "$name`: entra is incomplete; no frontend built for it (the frontend step will fail preflight)."
                continue
            }
            Write-Host "== build frontend for $name"
            Invoke-Tool 'npm' @('--prefix', $frontendDir, 'run', 'build') @{
                VITE_ENTRA_TENANT_ID = $entra.tenantId
                VITE_ENTRA_CLIENT_ID = $entra.clientId
                VITE_API_SCOPE       = "api://$($entra.clientId)/$($entra.scope)"
                VITE_API_BASE_URL    = ''
            }
            New-Zip (Join-Path $frontendDir 'dist') (Join-Path $artifacts "dashboard-frontend.$name.zip")
            $frontends += $name
        }

        Copy-Item (Join-Path $PSScriptRoot 'Deploy-Wilson.ps1') $packageDir
        Copy-Item $basePath (Join-Path $packageDir 'config/deploy.base.json')
        foreach ($name in $configs.Keys) { Copy-Item $configs[$name].Path (Join-Path $packageDir "config/deploy.$name.json") }

        [ordered]@{
            version                          = $Version
            commit                           = $commit
            builtUtc                         = (Get-Date -AsUTC -Format 'o')
            environments                     = @($configs.Keys)
            frontend                         = $frontends
            automationQueueAndTimerFunctions = @($queueAndTimerFunctions)
            gatewayFunctions                 = @($gatewayFunctions)
        } | ConvertTo-Json | Set-Content (Join-Path $artifacts 'manifest.json') -Encoding utf8NoBOM

        Set-Content (Join-Path $packageDir 'README.txt') (Get-PackageReadme $Version $commit @($configs.Keys) $configDir) -Encoding utf8NoBOM

        $zip = "$packageDir.zip"
        New-Zip $packageDir $zip
        Write-Host "Package: $packageDir"
        Write-Host "Zip:     $zip"
    }
    finally {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}
