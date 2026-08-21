# 0. Make sure you're logged in to the right subscription
az login
az account show

$RESOURCE_GROUP = "d365fsc-integration-nonprod"
$SWA_NAME       = "wtc-dashboard-frontend-dev"

# Entra (Azure AD) settings baked into the SPA at BUILD time — must match the
# values currently configured on the API (see AzureDeploy.AppService.UpdateAzure.ps1)
$env:VITE_ENTRA_TENANT_ID = "446b6761-0bd4-4dad-821a-cbdbe4b7b4de"
$env:VITE_ENTRA_CLIENT_ID = "5d1e26b3-6d06-4d54-b6ae-9387bca0bcc6"
$env:VITE_API_SCOPE       = "api://5d1e26b3-6d06-4d54-b6ae-9387bca0bcc6/user_impersonation"
$env:VITE_API_BASE_URL    = ""    # same-origin (linked backend) — keep empty

# 1. Build the SPA
npm --prefix ARM.Integration.Dashboard.Frontend ci
npm --prefix ARM.Integration.Dashboard.Frontend run build

# 2. Deploy the built SPA using the SWA deployment token (SWA CLI via npx)
$SWA_TOKEN = az staticwebapp secrets list `
  --name $SWA_NAME `
  --resource-group $RESOURCE_GROUP `
  --query "properties.apiKey" -o tsv
npx --yes @azure/static-web-apps-cli deploy ARM.Integration.Dashboard.Frontend/dist `
  --deployment-token $SWA_TOKEN `
  --env production

$WEB_URL = az staticwebapp show --name $SWA_NAME --resource-group $RESOURCE_GROUP --query "defaultHostname" -o tsv
Write-Host "Dashboard is available at: https://$WEB_URL"
