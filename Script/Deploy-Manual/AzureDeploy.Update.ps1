<#
=============================================================================
ARM.Integration.Wilson - incremental update (Azure Container Apps)

Rebuilds the changed image(s) in ACR and rolls out a new revision to the
existing Container Apps. Does NOT recreate any infrastructure and does NOT
need secrets (image swaps reuse the app's existing secrets/env vars).

Usage:
  ./AzureDeploy.Update.ps1                         # updates ALL three apps
  ./AzureDeploy.Update.ps1 -Component api          # only the API
  ./AzureDeploy.Update.ps1 -Component frontend     # only the frontend
  ./AzureDeploy.Update.ps1 -Component function     # only eim-customer
  ./AzureDeploy.Update.ps1 -Component api -ImageTag 2026-07-08-1

Prereqs: `az login` already done, Azure CLI + containerapp extension.
=============================================================================
#>
param(
    [ValidateSet('all', 'api', 'frontend', 'function')]
    [string]$Component = 'all',

    # Unique tag per deploy so Container Apps creates a new revision.
    [string]$ImageTag = (Get-Date -Format 'yyyy-MM-dd-HHmmss')
)

$ErrorActionPreference = 'Stop'

# -----------------------------------------------------------------------------
# Variables — must match AzureDeploy
# -----------------------------------------------------------------------------
$RESOURCE_GROUP = "d365fsc-integration-nonprod"
$ACR_NAME       = "wtcd365fscintegrationacrdev"

$API_IMAGE  = "arm-integration-dashboard-api"
$WEB_IMAGE  = "arm-integration-dashboard-frontend"
$FUNC_IMAGE = "arm-integration-eim-customer"

# Frontend Entra (Azure AD) settings — baked into the SPA at BUILD time (not secret)
$VITE_ENTRA_TENANT_ID = "5e8a5341-629d-4421-9dbf-f5a5224ce6e7"
$VITE_ENTRA_CLIENT_ID = "317063cf-07c0-46ea-8a97-a92eaa7d807e"
$VITE_API_SCOPE       = "api://317063cf-07c0-46ea-8a97-a92eaa7d807e/access_as_user"

Write-Host "Updating component '$Component' with tag '$ImageTag'..." -ForegroundColor Cyan

# -----------------------------------------------------------------------------
# API
# -----------------------------------------------------------------------------
function Update-Api {
    Write-Host "`n=== dashboard-api ===" -ForegroundColor Yellow
    az acr build `
      --registry $ACR_NAME `
      --image "${API_IMAGE}:$ImageTag" `
      --file ARM.Integration.Dashboard.API/Dockerfile `
      .
    az containerapp update `
      --name dashboard-api `
      --resource-group $RESOURCE_GROUP `
      --image "$ACR_NAME.azurecr.io/${API_IMAGE}:$ImageTag"
}

# -----------------------------------------------------------------------------
# Frontend (rebuild with the same VITE_* build args)
# -----------------------------------------------------------------------------
function Update-Frontend {
    Write-Host "`n=== dashboard-frontend ===" -ForegroundColor Yellow
    az acr build `
      --registry $ACR_NAME `
      --image "${WEB_IMAGE}:$ImageTag" `
      --file ARM.Integration.Dashboard.Frontend/Dockerfile `
      --build-arg "VITE_ENTRA_TENANT_ID=$VITE_ENTRA_TENANT_ID" `
      --build-arg "VITE_ENTRA_CLIENT_ID=$VITE_ENTRA_CLIENT_ID" `
      --build-arg "VITE_API_SCOPE=$VITE_API_SCOPE" `
      ARM.Integration.Dashboard.Frontend
    az containerapp update `
      --name dashboard-frontend `
      --resource-group $RESOURCE_GROUP `
      --image "$ACR_NAME.azurecr.io/${WEB_IMAGE}:$ImageTag"
}

# -----------------------------------------------------------------------------
# EIM.Customer function (single-instance app — a quick restart on new revision)
# -----------------------------------------------------------------------------
function Update-Function {
    Write-Host "`n=== eim-customer ===" -ForegroundColor Yellow
    az acr build `
      --registry $ACR_NAME `
      --image "${FUNC_IMAGE}:$ImageTag" `
      --file ARM.Integration.EIM.Customer/Dockerfile `
      .
    az containerapp update `
      --name eim-customer `
      --resource-group $RESOURCE_GROUP `
      --image "$ACR_NAME.azurecr.io/${FUNC_IMAGE}:$ImageTag"
}

switch ($Component) {
    'api'      { Update-Api }
    'frontend' { Update-Frontend }
    'function' { Update-Function }
    'all'      { Update-Api; Update-Frontend; Update-Function }
}

Write-Host "`nDone. Rolled out tag '$ImageTag' for '$Component'." -ForegroundColor Green
