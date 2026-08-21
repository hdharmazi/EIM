# =============================================================================
# ARM.Integration.Wilson - deploy EIM.Customer Function App to dev1 (CODE deploy)
# -----------------------------------------------------------------------------
# What this script does:
#   1) Ensures Resource Group, App Service Plan, Storage Account exist
#   2) Ensures Function App exists (Linux, .NET isolated, Functions v4)
#   3) Applies app settings for dev1
#   4) Publishes ARM.Integration.EIM.Customer and zip deploys the package
#
# Prereqs:
#   - Azure CLI logged in (az login)
#   - .NET 10 SDK installed
# =============================================================================

$ErrorActionPreference = "Stop"

# -----------------------------------------------------------------------------
# Variables — adjust if needed
# -----------------------------------------------------------------------------
$SUBSCRIPTION_ID = "84abae0f-07f1-4df8-aacd-197d2e7f1cac"   # optional: set to target subscription id/name, or leave empty
$RESOURCE_GROUP  = "d365fsc-integration-nonprod"
$LOCATION        = "centralus"
$PLAN_NAME       = "asp-appservice-p0v3-linux-dev1"

# Must be globally unique, 3-24 chars, lowercase alphanumeric
$STORAGE_ACCOUNT = "stwtceimcustomerdev1"

# Must be globally unique -> *.azurewebsites.net
$FUNCTION_APP    = "azf-eim-customer-wtc-dev1"

# Secrets / config — fill real values before run (or pass via pipeline variables)
$EIMDB_CONNECTION_STRING = "Data Source=tcp:azql-wtc-sandbox.database.windows.net,1433;Initial Catalog=eimdb;User ID=sadmin;Password=b3Y[7TBk;Pooling=False;Connect Timeout=30;Encrypt=True;Trust Server Certificate=False;Authentication=SqlPassword;Connect Retry Count=1;Connect Retry Interval=10;Command Timeout=30"
$SERVICEBUS_CONNECTION   = "Endpoint=sb://asb-eim-wtc-nonprod.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=4gzdDB8HYmPSui549y8z+o+zHtFySmtM9+ASbLQswiw="

# Queue/config names for dev1
$D365DataEventQueue         = "dev1-d365fsc-dataevent"
$D365BusinessEventQueue     = "dev1-d365fsc-businessevents"
$D365ToCaptureQueue         = "dev1-d365fsc-to-capture"
$CaptureToD365Queue         = "dev1-capture-to-d365fsc"
$CaptureToD365QueueResponse = "dev1-capture-to-d365fsc-response"
$DefaultEnvironment         = "dev1"

# Publish artifacts
$FUNC_PUBLISH = "./artifacts/eim-customer-publish-dev1"
$FUNC_ZIP     = "./artifacts/eim-customer-dev1.zip"
$SETTINGS_FILE = "./artifacts/func-appsettings-dev1.json"

# -----------------------------------------------------------------------------
# 0) Azure login + subscription
# -----------------------------------------------------------------------------
az account show 1>$null 2>$null
if ($LASTEXITCODE -ne 0) {
  az login | Out-Null
}

if (![string]::IsNullOrWhiteSpace($SUBSCRIPTION_ID)) {
  az account set --subscription $SUBSCRIPTION_ID
}

# -----------------------------------------------------------------------------
# 1) Ensure Resource Group + Linux App Service Plan exist
# -----------------------------------------------------------------------------
az group create --name $RESOURCE_GROUP --location $LOCATION | Out-Null

$planExists = az appservice plan show --name $PLAN_NAME --resource-group $RESOURCE_GROUP --query "name" -o tsv 2>$null
if ([string]::IsNullOrWhiteSpace($planExists)) {
  az appservice plan create `
    --name $PLAN_NAME `
    --resource-group $RESOURCE_GROUP `
    --location $LOCATION `
    --sku P0V3 `
    --is-linux | Out-Null
}

# -----------------------------------------------------------------------------
# 2) Ensure Storage Account exists
# -----------------------------------------------------------------------------
$storageExists = az storage account show --name $STORAGE_ACCOUNT --resource-group $RESOURCE_GROUP --query "name" -o tsv 2>$null
if ([string]::IsNullOrWhiteSpace($storageExists)) {
  az storage account create `
    --name $STORAGE_ACCOUNT `
    --resource-group $RESOURCE_GROUP `
    --location $LOCATION `
    --sku Standard_LRS | Out-Null
}

# -----------------------------------------------------------------------------
# 3) Ensure Function App exists
# -----------------------------------------------------------------------------
$funcExists = az functionapp show --name $FUNCTION_APP --resource-group $RESOURCE_GROUP --query "name" -o tsv 2>$null
if ([string]::IsNullOrWhiteSpace($funcExists)) {
  az functionapp create `
    --resource-group $RESOURCE_GROUP `
    --plan $PLAN_NAME `
    --name $FUNCTION_APP `
    --storage-account $STORAGE_ACCOUNT `
    --runtime dotnet-isolated `
    --runtime-version 10 `
    --functions-version 4 `
    --os-type Linux | Out-Null
}

# -----------------------------------------------------------------------------
# 4) Apply app settings (JSON file to avoid quoting issues)
# -----------------------------------------------------------------------------
$funcSettings = @(
  @{ name = "FUNCTIONS_WORKER_RUNTIME";      value = "dotnet-isolated" }
  @{ name = "ServiceBus";                    value = $SERVICEBUS_CONNECTION }
  @{ name = "EIMDB";                         value = $EIMDB_CONNECTION_STRING }
  @{ name = "D365DataEventQueue";            value = $D365DataEventQueue }
  @{ name = "D365BusinessEventQueue";        value = $D365BusinessEventQueue }
  @{ name = "D365ToCaptureQueue";            value = $D365ToCaptureQueue }
  @{ name = "CaptureToD365Queue";            value = $CaptureToD365Queue }
  @{ name = "CaptureToD365QueueResponse";    value = $CaptureToD365QueueResponse }
  @{ name = "CaptureToD365QueueActive";      value = "True" }
  @{ name = "PollSchedule";                  value = "0 */1 * * * *" }
  @{ name = "AwaitingD365SweepSchedule";     value = "0 */15 * * * *" }
  @{ name = "AwaitingD365StuckTimeoutHours"; value = "4" }
  @{ name = "DefaultEnvironment";            value = $DefaultEnvironment }
  @{ name = "ExcludedUsers";                 value = "51E452DB-CF29-4EDA-951C-50332FBB11AA;991452DB-CF29-4EDA-951C-50332FBB1322" }
)

New-Item -ItemType Directory -Force -Path (Split-Path $SETTINGS_FILE) | Out-Null
$funcSettings | ConvertTo-Json -AsArray | Set-Content -Path $SETTINGS_FILE -Encoding utf8

az functionapp config appsettings set `
  --name $FUNCTION_APP `
  --resource-group $RESOURCE_GROUP `
  --settings "@$SETTINGS_FILE" | Out-Null

Remove-Item $SETTINGS_FILE -ErrorAction SilentlyContinue

# -----------------------------------------------------------------------------
# 5) Publish + zip deploy function code
# -----------------------------------------------------------------------------
dotnet publish ARM.Integration.EIM.Customer/ARM.Integration.EIM.Customer.csproj `
  --configuration Release `
  --output $FUNC_PUBLISH

if (Test-Path $FUNC_ZIP) { Remove-Item $FUNC_ZIP -Force }
Compress-Archive -Path "$FUNC_PUBLISH/*" -DestinationPath $FUNC_ZIP -Force

az functionapp deployment source config-zip `
  --resource-group $RESOURCE_GROUP `
  --name $FUNCTION_APP `
  --src $FUNC_ZIP | Out-Null

# Optional restart for clean startup
az functionapp restart --name $FUNCTION_APP --resource-group $RESOURCE_GROUP | Out-Null

$host = az functionapp show --name $FUNCTION_APP --resource-group $RESOURCE_GROUP --query "defaultHostName" -o tsv
Write-Host "Deployment complete. Function host: https://$host"
