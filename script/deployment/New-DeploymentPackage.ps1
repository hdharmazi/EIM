<#
.SYNOPSIS
    Builds a deployment package that runs with only PowerShell 7 and the Azure CLI.

.DESCRIPTION
    Writes artifacts/package/wtc-eim-deploy-<version>/ and a zip of it, containing:
      Deploy-Wilson.ps1, config/deploy.base.json and config/deploy.<name>.json for each -Environment (and for the
        configuration each one "uses", so its names resolve)
      artifacts/wilson-automation.zip, gateway.zip, dashboard-api.zip   (dotnet publish -c Release)
      artifacts/dashboard-frontend.<name>.zip   the SPA, for each configuration without "uses" (bat, prd)
      artifacts/dbmigrator/ARM.Integration.DbMigrator.exe   self-contained win-x64 migrator
      artifacts/manifest.json, README.txt

    The same binaries go to every configuration in the package. The SPA is built with the configuration's Entra
    values; a configuration whose Entra values are still <...> placeholders gets no SPA (the frontend step then
    fails preflight). The package holds no secrets.

.EXAMPLE
    ./script/deployment/New-DeploymentPackage.ps1

.EXAMPLE
    ./script/deployment/New-DeploymentPackage.ps1 -Environment bat, test -Version local
#>
# After the help block: a #Requires line above it stops Get-Help from finding the help.
#Requires -Version 7.2
param(
    [ValidatePattern('^[a-z0-9]+$')]
    [string[]]$Environment = @('bat', 'test', 'prd'),

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

# The resource group a configuration deploys to: names.resourceGroup, or the derived rg-<workload>-<client>-<environment>.
function Get-PackageResourceGroup($Config) {
    if (-not [string]::IsNullOrWhiteSpace("$($Config.names.resourceGroup)")) { return "$($Config.names.resourceGroup)" }
    "rg-$($Config.workload)-$($Config.client)-$($Config.environment)"
}

# The packaged configurations grouped by owner, in package order: each configuration without "uses", with the
# packaged configurations that use it. An owner that is not packaged itself is still listed, from $Directory.
function Get-PackageOwners([string[]]$Environments, [string]$Directory) {
    $owners = [ordered]@{}
    foreach ($name in $Environments) {
        $config = Get-PackageConfig $name $Directory
        $ownerName = if ([string]::IsNullOrWhiteSpace("$($config.uses)")) { $name } else { "$($config.uses)" }
        if (-not $owners.Contains($ownerName)) {
            $owner = if ($ownerName -eq $name) { $config } else { Get-PackageConfig $ownerName $Directory }
            $owners[$ownerName] = [pscustomobject]@{
                Name          = $ownerName
                Packaged      = $Environments -contains $ownerName
                Production    = $owner.production -eq $true
                ResourceGroup = Get-PackageResourceGroup $owner
                Users         = [System.Collections.Generic.List[string]]::new()
            }
        }
        if ($ownerName -ne $name) { $owners[$ownerName].Users.Add($name) }
    }
    @($owners.Values)
}

function Get-PackageReadme([string]$Version, [string]$Commit, [string[]]$Environments, [string]$ConfigDirectory = (Join-Path $PSScriptRoot 'config')) {
    $owners = @(Get-PackageOwners $Environments $ConfigDirectory)
    $groups = @($owners.ResourceGroup | Where-Object { $_ } | Sort-Object -Unique)
    $configurations = foreach ($owner in $owners) {
        $label = if ($owner.Production) { 'production' } else { 'non-production' }
        "  $($owner.Name)  owner ($label, resource group $($owner.ResourceGroup)): Service Bus namespace, Key Vault,"
        '       SQL server and database, plan, Log Analytics, gateway, dashboard API and SPA, its queues and its'
        "       Automation app.$(if (-not $owner.Packaged) { ' Not deployed by this package; its configuration is included for the names.' })"
        foreach ($user in $owner.Users) {
            "  $user  uses $($owner.Name): its own queues and Automation app, on the resources of $($owner.Name)."
        }
        if ($owner.Production) {
            "  $($owner.Name) shares nothing with a non-production configuration, and its dashboard has no Simulation console."
        }
    }
    $steps = foreach ($owner in $owners) {
        ''
        "  $($owner.Name)$(if ($owner.Users.Count) { " (used by $($owner.Users -join ', '))" }):"
        if ($owner.Packaged) {
            '  1. Preview, changes nothing:'
            "       .\Deploy-Wilson.ps1 -Environment $($owner.Name) -Component all, myip, myipremove -WhatIf"
            '  2. Resources, SQL, settings, migrations, database users and the apps:'
            "       .\Deploy-Wilson.ps1 -Environment $($owner.Name) -Component all, myip, myipremove"
        }
        if ($owner.Users.Count) {
            "  $(if ($owner.Packaged) { '3' } else { '1' }). Each configuration that uses it$(if ($owner.Users.Count -gt 1) { " ($($owner.Users -join ', then '))" }), after $($owner.Name):"
            foreach ($user in $owner.Users) { "       .\Deploy-Wilson.ps1 -Environment $user -Component all, myip, myipremove" }
        }
    }
    @"
Wilson EIM deployment package $Version (commit $Commit)
Configurations: $($Environments -join ', ')

REQUIREMENTS
  - PowerShell 7.2 or later (pwsh) and Azure CLI 2.60 or later, on Windows.
  - Node.js LTS for the frontend step only (it uses the Static Web Apps CLI through npx).
  - az login as a user with Contributor on the resource group you deploy: $($groups -join ', ').
    Creating a missing resource group needs Contributor on the subscription.
  - For schema and sqlusers: membership of the SQL server's Entra admin group.
  There are no deployment secrets. The apps reach SQL with their managed identities and Service Bus through
  the Key Vault secret eim-servicebus, which 'settings' writes for an owner.

CONFIGURATIONS
$($configurations -join "`n")
  These are deployment configurations, not ENVIRONMENT values in eimdb (DEV2, for example, has no
  Function App). Development runs locally; no configuration deploys a development environment.
  The gated queue and timer triggers follow automation.triggersEnabled in each configuration.

STEPS
  Open PowerShell 7 (pwsh) in this folder and run the commands there; 'pwsh -File' would not accept
  the comma-separated component lists.

  0. Check the configuration: config\deploy.base.json and config\deploy.<name>.json. Nothing there is
     secret. Every command that changes something first runs 'preflight', which stops before any
     change on unfilled <...> values, a wrong "uses", missing owner resources, missing package files
     and (for code-only runs) settings an app lacks. Run it alone with:
       .\Deploy-Wilson.ps1 -Environment <name> -Component preflight
$($steps -join "`n")

  myip opens the SQL firewall for this machine around schema and sqlusers. If a run stops before
  myipremove, close it with:
       .\Deploy-Wilson.ps1 -Environment <name> -Component myipremove

  'verify' prints the state of every app; 'health' fails unless the hosts run, the dashboard answers
  and the Key Vault references resolve.

Guides: docs/deployment/deployment.md and docs/deployment/production-go-live.md in the repository.
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
    # Deploy-Wilson.ps1 resolves a configuration with "uses" from its owner's file, so the owner's file goes along.
    $ownerFiles = @($configs.Values | ForEach-Object { "$($_.Config.uses)" } | Where-Object { $_ -and -not $configs.Contains($_) } | Select-Object -Unique)
    foreach ($name in $ownerFiles) { Get-PackageConfig $name $configDir | Out-Null }

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

        # The SPA belongs to the owner: one dashboard serves the owner and the configurations that use it.
        $frontendDir = Join-Path $RepoRoot 'ARM.Integration.Dashboard.Frontend'
        $frontends = @()
        $owners = @($configs.Keys | Where-Object { [string]::IsNullOrWhiteSpace("$($configs[$_].Config.uses)") })
        if ($owners) {
            Write-Host "== npm ci (dashboard frontend)"
            Invoke-Tool 'npm' @('--prefix', $frontendDir, 'ci')
        }
        foreach ($name in $owners) {
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
        foreach ($name in $ownerFiles) { Copy-Item (Join-Path $configDir "deploy.$name.json") (Join-Path $packageDir "config/deploy.$name.json") }

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
