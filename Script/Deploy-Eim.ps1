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
    [ValidateSet('dev', 'dev1', 'bat', 'test')]
    [string]$Environment,

    [Parameter(Mandatory = $true)]
    [ValidateSet('queues', 'schema', 'api', 'function', 'gateway', 'frontend', 'all', 'firewall', 'keyvault')]
    [string[]]$Component,

    [string]$ConfigPath
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot

# Queues before schema before code: a Function App that starts against a missing queue or an
# un-migrated database fails on its first trigger rather than at deploy time, which is far harder
# to diagnose. Dashboard last because it only reads what the rest produces.
$ComponentOrder = @('queues', 'schema', 'api', 'function', 'gateway', 'frontend')

# Deliberately outside $ComponentOrder, so 'all' never runs them. Opening a SQL firewall is a
# developer convenience, not a deployment step, and a deployment that quietly widens network access
# is the kind of thing nobody notices until it is audited. The vault is excluded for a different
# reason: nothing reads from it yet, so running it by default would create a second copy of every
# secret for no benefit.
$OnDemandComponents = @('firewall', 'keyvault')

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

    # Checked for presence rather than truth: absent and false both read as falsy, so a forgotten
    # flag would silently mean "no environment hosts the gateway" and 'all' would skip it everywhere
    # without anyone being told.
    if ($null -eq $config.gateway -or $null -eq $config.gateway.hostsGateway) {
        throw "Configuration '$ConfigPath' is missing required key 'gateway.hostsGateway'. Set it false unless this environment owns the shared gateway."
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
        FunctionInsights = Resolve-Name $names.functionInsights "appins-eim-customer-wtc-$env"
        # Shared across every non-production environment. Unlike EIM.Customer, whose Service Bus
        # triggers bind to environment-specific queues, the gateway takes the D365 environment from
        # the request path, so nothing about it is per-environment. Named -nonprod rather than
        # inheriting an environment suffix, which the dashboard did and now regrets.
        GatewayApp      = Resolve-Name $names.gatewayApp     "azf-d365fsc-gateway-wtc-nonprod"
        GatewayStorage  = Resolve-Name $names.gatewayStorage "stwtcd365gwnonprod"
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
        Write-Host "  would ensure insights $($Names.FunctionInsights)"
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

    # Without this the sweeps and handlers run blind: a timer that never fires, or a message that
    # fails classification, leaves no record anywhere. Created through the generic resource API
    # rather than `az monitor app-insights`, which lives in an optional extension and prompts to
    # install itself - an interactive prompt that hangs an unattended deploy.
    $insightsName = $Names.FunctionInsights
    if (-not (az resource show --resource-group $rg --name $insightsName `
                --resource-type Microsoft.Insights/components --query "name" -o tsv 2>$null)) {
        az resource create --resource-group $rg --name $insightsName `
            --resource-type Microsoft.Insights/components --location $Names.Location `
            --properties '{\"Application_Type\":\"web\"}' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not create Application Insights component '$insightsName'." }
    }

    $insightsConnection = az resource show --resource-group $rg --name $insightsName `
        --resource-type Microsoft.Insights/components --query properties.ConnectionString -o tsv
    if ([string]::IsNullOrWhiteSpace($insightsConnection)) { throw "Could not read the Application Insights connection string." }

    $fn = $Config.function
    $settings = @(
        @{ name = 'FUNCTIONS_WORKER_RUNTIME';      value = 'dotnet-isolated' }
        @{ name = 'APPLICATIONINSIGHTS_CONNECTION_STRING'; value = $insightsConnection }
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

function Invoke-FirewallComponent {
    param($Config, $Names, [switch]$Preview)

    # Derived from the connection string so there is no second place to state the server, and no
    # chance of opening a hole in one server while deploying against another.
    $server = [regex]::Match($Config.secrets.eimdbConnectionString,
        'Data Source\s*=\s*tcp:([^.,;]+)\.database\.windows\.net').Groups[1].Value
    if ([string]::IsNullOrWhiteSpace($server)) {
        throw "Could not read the SQL server name from 'secrets.eimdbConnectionString'. Expected a 'Data Source=tcp:<server>.database.windows.net' form."
    }

    $ip = (Invoke-RestMethod -Uri 'https://api.ipify.org' -TimeoutSec 20).Trim()
    if ($ip -notmatch '^\d{1,3}(\.\d{1,3}){3}$') {
        throw "Could not determine this machine's public IP address; got '$ip'."
    }

    # Named for the person, not the address: a rule per developer can be found and removed, whereas
    # one shared 'AllowMyIP' silently moves whenever somebody else runs this.
    $rule = "AllowDev-$($env:USERNAME)"

    if ($Preview) {
        Write-Host "  would allow $ip on $server as firewall rule '$rule'"
        return
    }

    az sql server firewall-rule create --resource-group $Names.ResourceGroup `
        --server $server --name $rule --start-ip-address $ip --end-ip-address $ip | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not create firewall rule '$rule' on '$server'." }

    Write-Host "  $server now allows $ip via '$rule'"
    Write-Host "  remove it when you are done: az sql server firewall-rule delete -g $($Names.ResourceGroup) -s $server -n $rule"
}

function Invoke-KeyVaultComponent {
    param($Config, $Names, [switch]$Preview)

    $vault = if ([string]::IsNullOrWhiteSpace($Config.keyVault.name)) {
        "kv-d365fsc-wtc-nonprod"
    } else { $Config.keyVault.name }

    $secrets = @(
        @{ name = 'asb';   value = $Config.secrets.serviceBusConnectionString }
        @{ name = 'eimdb'; value = $Config.secrets.eimdbConnectionString }
    )

    if ($Preview) {
        Write-Host "  would ensure vault    $vault (RBAC, soft delete 90 days)"
        Write-Host "  would set secrets     $(($secrets.name) -join ', ')"
        Write-Host "  NOTE nothing reads from this vault yet - see Script/Docs/deployment.md"
        return
    }

    $rg = $Names.ResourceGroup

    if (-not (az keyvault show --name $vault --resource-group $rg --query "name" -o tsv 2>$null)) {
        az keyvault create --name $vault --resource-group $rg --location $Names.Location `
            --sku standard --enable-rbac-authorization true `
            --enable-soft-delete true --retention-days 90 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not create key vault '$vault'." }
        Write-Host "  created vault $vault"
    }

    foreach ($secret in $secrets) {
        if ([string]::IsNullOrWhiteSpace($secret.value)) {
            throw "Secret '$($secret.name)' resolved to empty. Storing a blank secret would look like success and fail later at the point of use."
        }

        az keyvault secret set --vault-name $vault --name $secret.name --value $secret.value | Out-Null
        if ($LASTEXITCODE -ne 0) {
            # The vault uses RBAC, so control-plane rights to create it do not grant data-plane
            # rights to write a secret. That distinction is the usual cause of a failure here.
            throw "Could not set secret '$($secret.name)' in '$vault'. If this is a 403, grant yourself the data-plane role: az role assignment create --role ""Key Vault Secrets Officer"" --assignee <your-object-id> --scope $(az keyvault show --name $vault --resource-group $rg --query id -o tsv)"
        }
        Write-Host "  set secret $($secret.name)"
    }

    Write-Host "  NOTE nothing reads from this vault. Connection strings are still held in"
    Write-Host "       ENVIRONMENTTABLE and app settings in plaintext, so these are a second copy"
    Write-Host "       that must be rotated alongside the originals."
}

function Invoke-GatewayComponent {
    param($Config, $Names, [switch]$Preview)

    if ($Preview) {
        Write-Host "  would ensure storage  $($Names.GatewayStorage)"
        Write-Host "  would ensure flex app $($Names.GatewayApp)"
        Write-Host "  would ensure insights appins-d365fsc-gateway-wtc-nonprod"
        Write-Host "  would apply settings, publish, zip and deploy ARM.Integration.D365FSC.Gateway"
        return
    }

    $gw = $Config.gateway
    if (-not $gw) {
        throw "Configuration section 'gateway' is missing. The gateway component needs baseUrl and scale settings."
    }

    $rg = $Names.ResourceGroup

    if (-not (az storage account show --name $Names.GatewayStorage --resource-group $rg --query "name" -o tsv 2>$null)) {
        az storage account create --name $Names.GatewayStorage --resource-group $rg `
            --location $Names.Location --sku Standard_LRS | Out-Null
    }

    # Flex Consumption, not the plan-based path used by the capture Function App. The create verb
    # takes a different shape: no --plan, and instance size and count are app properties rather
    # than plan properties. Always-ready instances matter here because a synchronous caller feels a
    # cold start, and the maximum instance count is the only rate limiter this design has - without
    # it a third party's traffic burst can throttle the shared D365 tenant and take the capture
    # pipeline down with it.
    if (-not (az functionapp show --name $Names.GatewayApp --resource-group $rg --query "name" -o tsv 2>$null)) {
        az functionapp create --resource-group $rg --name $Names.GatewayApp `
            --storage-account $Names.GatewayStorage --flexconsumption-location $Names.Location `
            --runtime dotnet-isolated --runtime-version 10 `
            --instance-memory $gw.instanceMemoryMB `
            --always-ready-instances "http=$($gw.alwaysReadyInstances)" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Flex Consumption create failed. Confirm the region supports it." }
    }

    # Scale bounds are re-applied on every deploy so a configuration change takes effect on an
    # existing app, where the create arguments above no longer run. Note --max-replicas belongs to
    # Container Apps, not Flex Consumption; the maximum instance count is set here instead.
    az functionapp scale config set --name $Names.GatewayApp --resource-group $rg `
        --maximum-instance-count $gw.maximumInstanceCount | Out-Null

    az functionapp scale config always-ready set --name $Names.GatewayApp --resource-group $rg `
        --settings "http=$($gw.alwaysReadyInstances)" | Out-Null

    # Flex Consumption does not auto-provision Application Insights, and the gateway is unusable
    # without it: authorization denials are the only intrusion signal this design has, and a 403
    # with no telemetry record cannot be told apart from a mis-typed grant.
    $insightsName = if ([string]::IsNullOrWhiteSpace($gw.appInsightsName)) {
        "appins-d365fsc-gateway-wtc-nonprod"
    } else { $gw.appInsightsName }
    # Created through the generic resource API rather than `az monitor app-insights`, which lives in
    # an optional CLI extension and prompts to install itself - an interactive prompt that hangs an
    # unattended deploy.
    if (-not (az resource show --resource-group $rg --name $insightsName `
                --resource-type Microsoft.Insights/components --query "name" -o tsv 2>$null)) {
        az resource create --resource-group $rg --name $insightsName `
            --resource-type Microsoft.Insights/components --location $Names.Location `
            --properties '{\"Application_Type\":\"web\"}' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not create Application Insights component '$insightsName'." }
    }

    $insightsConnection = az resource show --resource-group $rg --name $insightsName `
        --resource-type Microsoft.Insights/components --query properties.ConnectionString -o tsv
    if ([string]::IsNullOrWhiteSpace($insightsConnection)) { throw "Could not read the Application Insights connection string." }

    # No FUNCTIONS_WORKER_RUNTIME here. Flex Consumption carries the runtime as an app property set
    # at create time and rejects it as an app setting, failing the whole batch if present.
    $settings = @(
        @{ name = 'EIMDB';                               value = $Config.secrets.eimdbConnectionString }
        @{ name = 'APPLICATIONINSIGHTS_CONNECTION_STRING'; value = $insightsConnection }
        @{ name = 'Gateway__BaseUrl';                    value = $gw.baseUrl }
        @{ name = 'Gateway__MaxRequestBytes';            value = $gw.maxRequestBytes }
        @{ name = 'Gateway__IdempotencyRetention';       value = $gw.idempotencyRetention }
        @{ name = 'Gateway__IdempotencyPurgeSchedule';   value = $gw.idempotencyPurgeSchedule }
    )

    foreach ($setting in $settings) {
        if ([string]::IsNullOrWhiteSpace($setting.value)) {
            throw "Gateway app setting '$($setting.name)' resolved to empty. Deploying without it would produce an app that answers requests but cannot serve followable paging links."
        }
    }

    $settingsFile = Join-Path $RepoRoot "artifacts/gateway-appsettings-$($Names.Environment).json"
    New-Item -ItemType Directory -Force -Path (Split-Path $settingsFile) | Out-Null
    try {
        $settings | ConvertTo-Json -AsArray | Set-Content -Path $settingsFile -Encoding utf8
        az functionapp config appsettings set --name $Names.GatewayApp --resource-group $rg `
            --settings "@$settingsFile" | Out-Null
        # Without this the batch can fail and the deploy still reports success, leaving an app that
        # starts and then cannot read its own configuration.
        if ($LASTEXITCODE -ne 0) { throw "Applying gateway app settings failed." }
    }
    finally {
        Remove-Item $settingsFile -ErrorAction SilentlyContinue
    }

    $publishDir = Join-Path $RepoRoot "artifacts/gateway-publish-$($Names.Environment)"
    $zipPath    = Join-Path $RepoRoot "artifacts/gateway-$($Names.Environment).zip"

    dotnet publish (Join-Path $RepoRoot 'ARM.Integration.D365FSC.Gateway/ARM.Integration.D365FSC.Gateway.csproj') `
        --configuration Release --output $publishDir
    if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path "$publishDir/*" -DestinationPath $zipPath -Force

    az functionapp deployment source config-zip --resource-group $rg --name $Names.GatewayApp --src $zipPath | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Zip deploy failed." }

    # az functionapp show returns empty projections for Flex Consumption sites, so the site
    # properties are read through the generic resource API instead.
    $site = az resource show --resource-group $rg --name $Names.GatewayApp `
        --resource-type Microsoft.Web/sites -o json | ConvertFrom-Json
    Write-Host "  gateway host: https://$($site.properties.defaultHostName)  [$($site.properties.sku), $($site.properties.state)]"
    Write-Host "  NOTE: issue a function-level key per consumer. Never hand out the master key."
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
        # Same value the gateway is deployed with, so the address the dashboard verifies keys against
        # cannot drift from the one the gateway actually answers on.
        @{ name = 'Gateway__BaseUrl';                       value = $Config.gateway.baseUrl }
        @{ name = 'Gateway__AppName';                       value = $Names.GatewayApp }
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
$ordered = @($ComponentOrder | Where-Object { $requested -contains $_ })
$ordered += @($OnDemandComponents | Where-Object { $Component -contains $_ })

# Shared components are deployed once for all non-production environments. Naming one explicitly
# against an environment that does not host it means the request was wrong, so it is refused rather
# than skipped. Asking for 'all' is not such a request - it means "everything that belongs here" -
# so shared components are dropped from the list instead, which is what makes 'all' usable for an
# ordinary environment at all.
$sharedHosts = @{
    api      = [bool]$config.dashboard.hostsDashboard
    frontend = [bool]$config.dashboard.hostsDashboard
    gateway  = [bool]$config.gateway.hostsGateway
}

$notHosted = $ordered | Where-Object { $sharedHosts.ContainsKey($_) -and -not $sharedHosts[$_] }
if ($notHosted) {
    $explicit = $notHosted | Where-Object { $Component -contains $_ }
    if ($explicit) {
        throw "Environment '$Environment' does not host '$($explicit -join ", ")', so it cannot be deployed from here. Deploy shared components from the environment that owns them."
    }

    Write-Host "Skipping shared component(s) not hosted here: $($notHosted -join ', ')"
    $ordered = $ordered | Where-Object { $notHosted -notcontains $_ }
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
    gateway  = ${function:Invoke-GatewayComponent}
    frontend = ${function:Invoke-FrontendComponent}
    firewall = ${function:Invoke-FirewallComponent}
    keyvault = ${function:Invoke-KeyVaultComponent}
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
