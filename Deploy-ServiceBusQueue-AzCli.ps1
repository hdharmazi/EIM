<#
.SYNOPSIS
    Creates a Service Bus queue in an existing namespace (Azure CLI).

.DESCRIPTION
    Creates all five Service Bus queues for a new environment in one run,
    using the Azure CLI (az). Queues created (prefix = Environment value):

      {env}-capture-to-d365fsc
      {env}-capture-to-d365fsc-response
      {env}-d365fsc-businessevents
      {env}-d365fsc-dataevent
      {env}-d365fsc-to-capture

    Queues that already exist are skipped (no error). After running, update
    appsettings.json and set the connection string via dotnet user-secrets.

.PARAMETER Environment
    The environment prefix (e.g. dev1, dev2, staging, uat, prod).
    e.g. -Environment dev1  →  dev1-d365fsc-dataevent, dev1-capture-to-d365fsc, …

.PARAMETER ResourceGroupName
    The resource group that contains the Service Bus namespace.

.PARAMETER NamespaceName
    The existing Service Bus namespace to add the queue to.

.PARAMETER MaxSizeInMB
    Queue maximum size in MB. Allowed values: 1024, 2048, 3072, 4096, 5120.
    Defaults to 1024.

.PARAMETER DefaultMessageTimeToLiveDays
    How long messages remain in the queue before expiry (days). Defaults to 14.

.PARAMETER LockDurationSeconds
    Message lock duration in seconds (1–300). Defaults to 60.

.PARAMETER MaxDeliveryCount
    Number of delivery attempts before dead-lettering. Defaults to 10.

.PARAMETER EnableDeadLetteringOnMessageExpiration
    Dead-letter messages on expiry. Defaults to $true.

.EXAMPLE
    # Creates all five queues for dev1
    .\Deploy-ServiceBusQueue-AzCli.ps1 `
        -Environment dev1 `
        -ResourceGroupName rg-capture-dev `
        -NamespaceName sb-capture-dev
#>

[CmdletBinding(SupportsShouldProcess)]
param (
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $Environment,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $ResourceGroupName,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string] $NamespaceName,

    [ValidateSet(1024, 2048, 3072, 4096, 5120)]
    [int] $MaxSizeInMB = 1024,

    [ValidateRange(1, 365)]
    [int] $DefaultMessageTimeToLiveDays = 14,

    [ValidateRange(1, 300)]
    [int] $LockDurationSeconds = 60,

    [ValidateRange(1, 2000)]
    [int] $MaxDeliveryCount = 10,

    [bool] $EnableDeadLetteringOnMessageExpiration = $true
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Verify Azure CLI is available ─────────────────────────────────────────────
if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    Write-Error "Azure CLI ('az') was not found. Install it from https://aka.ms/installazurecli and re-run."
    exit 1
}

# ── Queue definitions for this environment ───────────────────────────────────
$queueSuffixes = @(
    'capture-to-d365fsc'
    'capture-to-d365fsc-response'
    'd365fsc-businessevents'
    'd365fsc-dataevent'
    'd365fsc-to-capture'
)

# ── Format shared queue parameters ───────────────────────────────────────────
# ISO 8601 duration strings required by the CLI
$ttlIso  = "P$($DefaultMessageTimeToLiveDays)D"
$lockIso = "PT$($LockDurationSeconds)S"
$deadLetterOnExpiry = $EnableDeadLetteringOnMessageExpiration.ToString().ToLower()

Write-Host ""
Write-Host "Service Bus queue deployment (Azure CLI)" -ForegroundColor Cyan
Write-Host "  Environment   : $Environment"
Write-Host "  Queues        : $($queueSuffixes.Count) queues to create"
Write-Host "  Resource group: $ResourceGroupName"
Write-Host "  Namespace     : $NamespaceName"
Write-Host ""

# ── Ensure the user is signed in ─────────────────────────────────────────────
$accountJson = az account show 2>$null
if (-not $accountJson) {
    Write-Host "No active Azure CLI session found. Signing in..." -ForegroundColor Yellow
    az login | Out-Null
    $accountJson = az account show
}

$account = $accountJson | ConvertFrom-Json
Write-Host "Using account     : $($account.user.name)" -ForegroundColor DarkGray
Write-Host "Using subscription: $($account.name) ($($account.id))" -ForegroundColor DarkGray
Write-Host ""

# ── Verify the namespace exists ───────────────────────────────────────────────
Write-Host "Verifying namespace '$NamespaceName'..." -NoNewline
$namespaceJson = az servicebus namespace show `
    --resource-group $ResourceGroupName `
    --name $NamespaceName `
    2>$null

if (-not $namespaceJson) {
    Write-Host ""
    Write-Error "Namespace '$NamespaceName' not found in resource group '$ResourceGroupName'. Check the names and your active subscription."
    exit 1
}

$namespace = $namespaceJson | ConvertFrom-Json
Write-Host " OK (SKU: $($namespace.sku.name))" -ForegroundColor Green
Write-Host ""

# ── Create each queue ─────────────────────────────────────────────────────────
$created = @()
$skipped = @()

foreach ($suffix in $queueSuffixes) {
    $queueName = "$Environment-$suffix"

    $existingJson = az servicebus queue show `
        --resource-group $ResourceGroupName `
        --namespace-name $NamespaceName `
        --name $queueName `
        2>$null

    if ($existingJson) {
        Write-Host "  [skip]    $queueName" -ForegroundColor DarkGray
        $skipped += $queueName
        continue
    }

    if ($PSCmdlet.ShouldProcess("$NamespaceName/$queueName", "Create Service Bus Queue")) {
        Write-Host "  [create]  $queueName ..." -NoNewline

        $queueJson = az servicebus queue create `
            --resource-group $ResourceGroupName `
            --namespace-name $NamespaceName `
            --name $queueName `
            --max-size $MaxSizeInMB `
            --default-message-time-to-live $ttlIso `
            --lock-duration $lockIso `
            --max-delivery-count $MaxDeliveryCount `
            --enable-dead-lettering-on-message-expiration $deadLetterOnExpiry `
            --enable-partitioning false `
            2>&1

        if ($LASTEXITCODE -ne 0) {
            Write-Host " FAILED" -ForegroundColor Red
            Write-Error "Queue creation failed for '$queueName':`n$queueJson"
            exit 1
        }

        Write-Host " OK" -ForegroundColor Green
        $created += $queueName
    }
}

Write-Host ""
Write-Host "Summary:" -ForegroundColor Cyan
Write-Host "  Created : $($created.Count)  ($($created -join ', '))"
Write-Host "  Skipped : $($skipped.Count)  (already existed)"
Write-Host ""

# ── Retrieve the connection string ────────────────────────────────────────────
Write-Host "Retrieving connection string..." -ForegroundColor DarkGray
$keysJson = az servicebus namespace authorization-rule keys list `
    --resource-group $ResourceGroupName `
    --namespace-name $NamespaceName `
    --name RootManageSharedAccessKey `
    2>$null

if ($keysJson) {
    $keys = $keysJson | ConvertFrom-Json

    Write-Host "Next steps — configure the application for environment '$Environment':" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "  1. Update appsettings.json with the relevant queue name, e.g.:"
    Write-Host "       ""ServiceBus"": { ""QueueName"": ""$Environment-d365fsc-dataevent"" }" -ForegroundColor White
    Write-Host ""
    Write-Host "  2. Set the connection string (do NOT commit this value):"
    Write-Host "       dotnet user-secrets set ""ConnectionStrings:ServiceBus"" ""<connection-string>""" -ForegroundColor White
    Write-Host ""
    Write-Host "  Primary connection string (treat as a secret):" -ForegroundColor Yellow
    Write-Host "  $($keys.primaryConnectionString)" -ForegroundColor DarkYellow
} else {
    Write-Host ""
    Write-Warning "Could not retrieve the connection string automatically. Retrieve it from the Azure portal."
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
