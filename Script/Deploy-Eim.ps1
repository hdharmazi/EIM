<#
.SYNOPSIS
    Deploys ARM.Integration.Wilson components to a non-production environment.

.DESCRIPTION
    Replaces the per-environment scripts under Script/Deploy-Manual/, which had drifted from each
    other and from what was actually deployed. Names are derived from the environment rather than
    retyped, and every value that varies lives in Script/config/deploy.<environment>.json.

    Components:
      queues    Service Bus queues for the environment
      schema    database migrations
      api       dashboard API          (shared - only from the hosting environment)
      function  EIM.Customer Function App
      frontend  dashboard SPA          (shared - only from the hosting environment)

.PARAMETER Environment
    dev1, bat or test.

.PARAMETER Component
    One or more components, or all. Order is enforced regardless of the order given.

.PARAMETER WhatIf
    Report the derived names and the intended actions. Makes no mutating call.

.EXAMPLE
    ./Script/Deploy-Eim.ps1 -Environment dev1 -Component all -WhatIf

.EXAMPLE
    ./Script/Deploy-Eim.ps1 -Environment bat -Component function
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('dev1', 'bat', 'test')]
    [string]$Environment,

    [Parameter(Mandatory = $true)]
    [ValidateSet('queues', 'schema', 'api', 'function', 'frontend', 'all')]
    [string[]]$Component,

    [string]$ConfigPath
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot

# Queues before schema before code: a Function App that starts against a missing queue or an
# un-migrated database fails on its first trigger rather than at deploy time, which is far harder
# to diagnose. Dashboard last because it only reads what the rest produces.
$ComponentOrder = @('queues', 'schema', 'api', 'function', 'frontend')

# --------------------------------------------------------------------------------------------
# Configuration
# --------------------------------------------------------------------------------------------

function Get-DeployConfig {
    param([string]$Environment, [string]$ConfigPath)

    if (-not $ConfigPath) {
        $ConfigPath = Join-Path $PSScriptRoot "config/deploy.$Environment.json"
    }

    if (-not (Test-Path $ConfigPath)) {
        throw "No configuration at '$ConfigPath'. Copy Script/config/deploy.example.json and fill it in."
    }

    $config = Get-Content $ConfigPath -Raw | ConvertFrom-Json

    # Environment variables win, so a pipeline can run without a file on disk. Named for the key
    # they replace so a missing-key error points straight at the variable to set.
    $overrides = @{
        'EIM_DEPLOY_SUBSCRIPTION_ID' = { $config.subscriptionId = $args[0] }
        'EIM_DEPLOY_RESOURCE_GROUP'  = { $config.resourceGroup = $args[0] }
        'EIM_DEPLOY_EIMDB'           = { $config.secrets.eimdbConnectionString = $args[0] }
        'EIM_DEPLOY_SERVICEBUS'      = { $config.secrets.serviceBusConnectionString = $args[0] }
    }
    foreach ($name in $overrides.Keys) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if (-not [string]::IsNullOrWhiteSpace($value)) { & $overrides[$name] $value }
    }

    if ($config.environment -ne $Environment) {
        throw "Configuration '$ConfigPath' declares environment '$($config.environment)' but '$Environment' was requested."
    }

    $required = @{
        'resourceGroup'                       = $config.resourceGroup
        'location'                            = $config.location
        'appServicePlan'                      = $config.appServicePlan
        'serviceBusNamespace'                 = $config.serviceBusNamespace
        'secrets.eimdbConnectionString'       = $config.secrets.eimdbConnectionString
        'secrets.serviceBusConnectionString'  = $config.secrets.serviceBusConnectionString
    }
    foreach ($key in $required.Keys) {
        if ([string]::IsNullOrWhiteSpace($required[$key])) {
            throw "Configuration '$ConfigPath' is missing required key '$key'."
        }
    }

    return $config
}

# --------------------------------------------------------------------------------------------
# Naming
# --------------------------------------------------------------------------------------------

function Get-ResourceNames {
    param($Config)

    $env = $Config.environment
    $names = $Config.names

    function Resolve-Name($override, $derived) {
        if ($override -and -not [string]::IsNullOrWhiteSpace($override)) { return $override }
        return $derived
    }

    return [pscustomobject]@{
        Environment     = $env
        ResourceGroup   = $Config.resourceGroup
        Location        = $Config.location
        # Shared across every environment by deliberate cost decision, so it is configuration
        # rather than something derived per environment.
        AppServicePlan  = $Config.appServicePlan
        ServiceBus      = $Config.serviceBusNamespace
        FunctionApp     = Resolve-Name $names.functionApp    "azf-eim-customer-wtc-$env"
        StorageAccount  = Resolve-Name $names.storageAccount "stwtceimcustomer$env"
        QueuePrefix     = Resolve-Name $names.queuePrefix    $env
        ApiAppName      = $Config.dashboard.apiAppName
        FrontendAppName = $Config.dashboard.frontendAppName
    }
}

function Get-QueueNames {
    param($Names)

    $p = $Names.QueuePrefix
    return [pscustomobject]@{
        D365FscDataEventQueue      = "$p-d365fsc-dataevent"
        D365BusinessEventQueue     = "$p-d365fsc-businessevents"
        D365ToCaptureQueue         = "$p-d365fsc-to-capture"
        CaptureToD365Queue         = "$p-capture-to-d365fsc"
        CaptureToD365QueueResponse = "$p-capture-to-d365fsc-response"
    }
}

# --------------------------------------------------------------------------------------------
# Azure CLI
# --------------------------------------------------------------------------------------------

function Assert-AzureCli {
    param($Config)

    if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
        throw "Azure CLI not found on PATH. Install it, then run 'az login'."
    }

    az account show 1>$null 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "Not logged in to Azure. Run 'az login' first."
    }

    if (-not [string]::IsNullOrWhiteSpace($Config.subscriptionId)) {
        az account set --subscription $Config.subscriptionId
        if ($LASTEXITCODE -ne 0) { throw "Could not select subscription '$($Config.subscriptionId)'." }
    }

    $current = az account show --query "name" -o tsv
    Write-Host "Subscription: $current"
}

# --------------------------------------------------------------------------------------------
# Components
# --------------------------------------------------------------------------------------------

function Invoke-QueuesComponent {
    param($Config, $Names, [switch]$Preview)

    $queues = Get-QueueNames $Names
    $all = $queues.PSObject.Properties.Value

    if ($Preview) {
        Write-Host "  would ensure queues on $($Names.ServiceBus): $($all -join ', ')"
        return
    }

    $script = Join-Path $PSScriptRoot 'Deploy-Manual/Deploy-ServiceBusQueue-AzCli.ps1'
    if (-not (Test-Path $script)) { throw "Queue script not found at '$script'." }

    foreach ($queue in $all) {
        & $script -ResourceGroup $Names.ResourceGroup -Namespace $Names.ServiceBus -QueueName $queue
        if ($LASTEXITCODE -ne 0) { throw "Queue '$queue' failed." }
    }
}

function Invoke-SchemaComponent {
    param($Config, $Names, [switch]$Preview)

    if ($Preview) {
        Write-Host "  would run database migrations against the configured eimdb"
        return
    }

    $project = Join-Path $RepoRoot 'ARM.Integration.DbMigrator'

    # Passed by environment variable so the connection string never reaches a command line, where
    # it would be visible to any process listing and captured in shell history.
    $previous = $env:EIMDB_MIGRATOR_CONNECTION
    try {
        $env:EIMDB_MIGRATOR_CONNECTION = $Config.secrets.eimdbConnectionString
        dotnet run --project $project -- --timeout 1800
        if ($LASTEXITCODE -ne 0) { throw "Migration failed with exit code $LASTEXITCODE." }
    }
    finally {
        $env:EIMDB_MIGRATOR_CONNECTION = $previous
    }
}

function Invoke-FunctionComponent {
    param($Config, $Names, [switch]$Preview)

    $queues = Get-QueueNames $Names

    if ($Preview) {
        Write-Host "  would ensure plan     $($Names.AppServicePlan)"
        Write-Host "  would ensure storage  $($Names.StorageAccount)"
        Write-Host "  would ensure function $($Names.FunctionApp)"
        Write-Host "  would apply settings, publish, zip and deploy ARM.Integration.EIM.Customer"
        return
    }

    $rg = $Names.ResourceGroup

    if (-not (az appservice plan show --name $Names.AppServicePlan --resource-group $rg --query "name" -o tsv 2>$null)) {
        az appservice plan create --name $Names.AppServicePlan --resource-group $rg `
            --location $Names.Location --sku P0V3 --is-linux | Out-Null
    }

    if (-not (az storage account show --name $Names.StorageAccount --resource-group $rg --query "name" -o tsv 2>$null)) {
        az storage account create --name $Names.StorageAccount --resource-group $rg `
            --location $Names.Location --sku Standard_LRS | Out-Null
    }

    if (-not (az functionapp show --name $Names.FunctionApp --resource-group $rg --query "name" -o tsv 2>$null)) {
        az functionapp create --resource-group $rg --plan $Names.AppServicePlan --name $Names.FunctionApp `
            --storage-account $Names.StorageAccount --runtime dotnet-isolated --runtime-version 10 `
            --functions-version 4 --os-type Linux | Out-Null
    }

    $fn = $Config.function
    $settings = @(
        @{ name = 'FUNCTIONS_WORKER_RUNTIME';      value = 'dotnet-isolated' }
        @{ name = 'ServiceBus';                    value = $Config.secrets.serviceBusConnectionString }
        @{ name = 'EIMDB';                         value = $Config.secrets.eimdbConnectionString }
        @{ name = 'D365FscDataEventQueue';         value = $queues.D365FscDataEventQueue }
        @{ name = 'D365BusinessEventQueue';        value = $queues.D365BusinessEventQueue }
        @{ name = 'D365ToCaptureQueue';            value = $queues.D365ToCaptureQueue }
        @{ name = 'CaptureToD365Queue';            value = $queues.CaptureToD365Queue }
        @{ name = 'CaptureToD365QueueResponse';    value = $queues.CaptureToD365QueueResponse }
        @{ name = 'PollSchedule';                  value = $fn.pollSchedule }
        @{ name = 'AwaitingD365SweepSchedule';     value = $fn.awaitingD365SweepSchedule }
        @{ name = 'AwaitingD365StuckTimeoutHours'; value = $fn.awaitingD365StuckTimeoutHours }
        @{ name = 'ReleaseSweepSchedule';          value = $fn.releaseSweepSchedule }
        @{ name = 'ReleaseSweepBatchSize';         value = $fn.releaseSweepBatchSize }
        @{ name = 'RetrySweepSchedule';            value = $fn.retrySweepSchedule }
        @{ name = 'RetrySweepBatchSize';           value = $fn.retrySweepBatchSize }
        @{ name = 'RetryMaxAttempts';              value = $fn.retryMaxAttempts }
        @{ name = 'RetryBaseDelaySeconds';         value = $fn.retryBaseDelaySeconds }
        @{ name = 'RetryMaxDelaySeconds';          value = $fn.retryMaxDelaySeconds }
        @{ name = 'DefaultEnvironment';            value = $Names.Environment }
        @{ name = 'ExcludedUsers';                 value = $fn.excludedUsers }
    )

    foreach ($setting in $settings) {
        if ([string]::IsNullOrWhiteSpace($setting.value)) {
            throw "App setting '$($setting.name)' resolved to empty. A trigger whose setting is missing fails to bind and stops the whole host."
        }
    }

    # Written to a file rather than passed inline: connection strings contain characters the shell
    # mangles, and this keeps them off the command line.
    $settingsFile = Join-Path $RepoRoot "artifacts/func-appsettings-$($Names.Environment).json"
    New-Item -ItemType Directory -Force -Path (Split-Path $settingsFile) | Out-Null
    try {
        $settings | ConvertTo-Json -AsArray | Set-Content -Path $settingsFile -Encoding utf8
        az functionapp config appsettings set --name $Names.FunctionApp --resource-group $rg `
            --settings "@$settingsFile" | Out-Null
    }
    finally {
        Remove-Item $settingsFile -ErrorAction SilentlyContinue
    }

    $publishDir = Join-Path $RepoRoot "artifacts/eim-customer-publish-$($Names.Environment)"
    $zipPath    = Join-Path $RepoRoot "artifacts/eim-customer-$($Names.Environment).zip"

    dotnet publish (Join-Path $RepoRoot 'ARM.Integration.EIM.Customer/ARM.Integration.EIM.Customer.csproj') `
        --configuration Release --output $publishDir
    if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path "$publishDir/*" -DestinationPath $zipPath -Force

    az functionapp deployment source config-zip --resource-group $rg --name $Names.FunctionApp --src $zipPath | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Zip deploy failed." }

    az functionapp restart --name $Names.FunctionApp --resource-group $rg | Out-Null

    $hostName = az functionapp show --name $Names.FunctionApp --resource-group $rg --query "defaultHostName" -o tsv
    Write-Host "  function host: https://$hostName"
}

function Invoke-ApiComponent {
    param($Config, $Names, [switch]$Preview)

    if ($Preview) {
        Write-Host "  would ensure web app $($Names.ApiAppName)"
        Write-Host "  would apply app settings including AzureAd from configuration"
        Write-Host "  would publish, zip and deploy ARM.Integration.Dashboard.API"
        return
    }

    $rg = $Names.ResourceGroup

    if (-not (az webapp show --resource-group $rg --name $Names.ApiAppName --query "name" -o tsv 2>$null)) {
        throw "Web app '$($Names.ApiAppName)' does not exist. Create it before deploying; this script does not provision the dashboard."
    }

    $queues = Get-QueueNames $Names
    $entra = $Config.entra

    # appsettings.json ships only AzureAd:Instance, so if these are not set here authentication
    # fails outright. The previous script set none of them.
    $settings = @(
        @{ name = 'ASPNETCORE_ENVIRONMENT';                 value = 'Production' }
        @{ name = 'ConnectionStrings__EIMDB';               value = $Config.secrets.eimdbConnectionString }
        @{ name = 'AzureAd__TenantId';                      value = $entra.tenantId }
        @{ name = 'AzureAd__ClientId';                      value = $entra.clientId }
        @{ name = 'AzureAd__Audience';                      value = $entra.audience }
        @{ name = 'Simulation__ServiceBusConnectionString'; value = $Config.secrets.serviceBusConnectionString }
        @{ name = 'Simulation__D365FscDataEventQueue';      value = $queues.D365FscDataEventQueue }
        @{ name = 'Simulation__CaptureToD365Queue';         value = $queues.CaptureToD365Queue }
        @{ name = 'Simulation__CaptureToD365ResponseQueue'; value = $queues.CaptureToD365QueueResponse }
        @{ name = 'Simulation__D365ToCaptureQueue';         value = $queues.D365ToCaptureQueue }
    )

    foreach ($setting in $settings) {
        if ([string]::IsNullOrWhiteSpace($setting.value)) {
            throw "App setting '$($setting.name)' resolved to empty."
        }
    }

    $settingsFile = Join-Path $RepoRoot 'artifacts/api-appsettings.json'
    New-Item -ItemType Directory -Force -Path (Split-Path $settingsFile) | Out-Null
    try {
        $settings | ConvertTo-Json -AsArray | Set-Content -Path $settingsFile -Encoding utf8
        az webapp config appsettings set --name $Names.ApiAppName --resource-group $rg `
            --settings "@$settingsFile" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Applying API app settings failed." }
    }
    finally {
        Remove-Item $settingsFile -ErrorAction SilentlyContinue
    }

    $publishDir = Join-Path $RepoRoot 'artifacts/api-publish'
    $zipPath    = Join-Path $RepoRoot 'artifacts/api.zip'

    dotnet publish (Join-Path $RepoRoot 'ARM.Integration.Dashboard.API/ARM.Integration.Dashboard.API.csproj') `
        --configuration Release --output $publishDir
    if ($LASTEXITCODE -ne 0) { throw "API publish failed." }

    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path "$publishDir/*" -DestinationPath $zipPath -Force

    az webapp deploy --resource-group $rg --name $Names.ApiAppName --src-path $zipPath --type zip | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "API zip deploy failed." }

    $apiHost = az webapp show --resource-group $rg --name $Names.ApiAppName --query "defaultHostName" -o tsv
    Write-Host "  api: https://$apiHost"
}

function Invoke-FrontendComponent {
    param($Config, $Names, [switch]$Preview)

    if ($Preview) {
        Write-Host "  would build the SPA with the configured Entra tenant, client and scope"
        Write-Host "  would ensure static web app $($Names.FrontendAppName) and link $($Names.ApiAppName) as its backend"
        Write-Host "  would deploy ARM.Integration.Dashboard.Frontend/dist"
        return
    }

    $rg = $Names.ResourceGroup
    $entra = $Config.entra
    $frontendDir = Join-Path $RepoRoot 'ARM.Integration.Dashboard.Frontend'

    if (-not (az staticwebapp show --name $Names.FrontendAppName --resource-group $rg --query "name" -o tsv 2>$null)) {
        throw "Static web app '$($Names.FrontendAppName)' does not exist. Create it before deploying; a linked backend needs the Standard SKU."
    }

    # The same tenant and client the API validates against. Building the SPA against a different
    # registration produces a site that loads and then fails every call, which is what happened
    # when two registrations were in play.
    $env:VITE_ENTRA_TENANT_ID = $entra.tenantId
    $env:VITE_ENTRA_CLIENT_ID = $entra.clientId
    $env:VITE_API_SCOPE       = "api://$($entra.clientId)/$($entra.scope)"
    # Empty on purpose. The linked backend proxies /api same-origin; pointing this at the API host
    # would take the browser cross-origin and require CORS that is not configured.
    $env:VITE_API_BASE_URL    = ''

    npm --prefix $frontendDir ci
    if ($LASTEXITCODE -ne 0) { throw "npm ci failed." }
    npm --prefix $frontendDir run build
    if ($LASTEXITCODE -ne 0) { throw "SPA build failed." }

    $apiId = az webapp show --resource-group $rg --name $Names.ApiAppName --query id -o tsv
    if ([string]::IsNullOrWhiteSpace($apiId)) { throw "Could not resolve the API resource id to link as backend." }

    # Idempotent in effect: re-linking the same backend is accepted, and the link must be
    # re-checked after every deploy because losing it silently breaks every /api call.
    az staticwebapp backends link --name $Names.FrontendAppName --resource-group $rg `
        --backend-resource-id $apiId --backend-region $Names.Location 2>$null | Out-Null

    $token = az staticwebapp secrets list --name $Names.FrontendAppName --resource-group $rg `
        --query "properties.apiKey" -o tsv
    if ([string]::IsNullOrWhiteSpace($token)) { throw "Could not read the static web app deployment token." }

    npx --yes @azure/static-web-apps-cli deploy (Join-Path $frontendDir 'dist') `
        --deployment-token $token --env production
    if ($LASTEXITCODE -ne 0) { throw "SPA deploy failed." }

    $webUrl = az staticwebapp show --name $Names.FrontendAppName --resource-group $rg --query "defaultHostname" -o tsv
    Write-Host "  dashboard: https://$webUrl"
}

# --------------------------------------------------------------------------------------------
# Main
# --------------------------------------------------------------------------------------------

$config = Get-DeployConfig -Environment $Environment -ConfigPath $ConfigPath
$names  = Get-ResourceNames -Config $config

$requested = if ($Component -contains 'all') { $ComponentOrder } else { $Component }
$ordered = $ComponentOrder | Where-Object { $requested -contains $_ }

# Refused rather than silently skipped: asking to deploy the shared dashboard from a environment
# that does not own it means the request was wrong, and quietly ignoring it hides that.
$shared = @('api', 'frontend') | Where-Object { $ordered -contains $_ }
if ($shared -and -not $config.dashboard.hostsDashboard) {
    throw "Environment '$Environment' does not host the shared dashboard, so '$($shared -join ", ")' cannot be deployed from it."
}

$preview = -not $PSCmdlet.ShouldProcess($Environment, "deploy $($ordered -join ', ')")

Write-Host ""
Write-Host "Environment     $($names.Environment)"
Write-Host "Resource group  $($names.ResourceGroup) ($($names.Location))"
Write-Host "App Service plan$([char]0x20) $($names.AppServicePlan)"
Write-Host "Function app    $($names.FunctionApp)"
Write-Host "Storage account $($names.StorageAccount)"
Write-Host "Queue prefix    $($names.QueuePrefix)-"
Write-Host "Components      $($ordered -join ' -> ')"
Write-Host ""

if (-not $preview) { Assert-AzureCli -Config $config }

$handlers = @{
    queues   = ${function:Invoke-QueuesComponent}
    schema   = ${function:Invoke-SchemaComponent}
    api      = ${function:Invoke-ApiComponent}
    function = ${function:Invoke-FunctionComponent}
    frontend = ${function:Invoke-FrontendComponent}
}

foreach ($step in $ordered) {
    Write-Host "[$step]"
    try {
        & $handlers[$step] -Config $config -Names $names -Preview:$preview
    }
    catch {
        # Named and fatal. A half-deployed environment that reports success is worse than a stop.
        Write-Error "Component '$step' failed: $($_.Exception.Message)"
        exit 1
    }
}

Write-Host ""
Write-Host $(if ($preview) { "WhatIf complete. Nothing was changed." } else { "Deployment complete." })
