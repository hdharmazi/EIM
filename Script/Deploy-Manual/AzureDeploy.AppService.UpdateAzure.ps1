

$RESOURCE_GROUP = "d365fsc-integration-nonprod"
$SWA_NAME       = "wtc-dashboard-frontend-dev"

# NEW Entra values
$env:VITE_ENTRA_TENANT_ID = "446b6761-0bd4-4dad-821a-cbdbe4b7b4de"
$env:VITE_ENTRA_CLIENT_ID = "5d1e26b3-6d06-4d54-b6ae-9387bca0bcc6"
$env:VITE_API_SCOPE       = "api://5d1e26b3-6d06-4d54-b6ae-9387bca0bcc6/user_impersonation"
$env:VITE_API_BASE_URL    = ""    # same-origin (linked backend) — keep empty

npm --prefix ARM.Integration.Dashboard.Frontend run build

$SWA_TOKEN = az staticwebapp secrets list --name $SWA_NAME --resource-group $RESOURCE_GROUP --query "properties.apiKey" -o tsv
npx --yes @azure/static-web-apps-cli deploy ARM.Integration.Dashboard.Frontend/dist --deployment-token $SWA_TOKEN --env production


$API_APP = "wtc-dashboard-api-dev"
$aad = @(
  @{ name = "AzureAd__TenantId"; value = "446b6761-0bd4-4dad-821a-cbdbe4b7b4de" }
  @{ name = "AzureAd__ClientId"; value = "5d1e26b3-6d06-4d54-b6ae-9387bca0bcc6" }
  @{ name = "AzureAd__Audience"; value = "api://5d1e26b3-6d06-4d54-b6ae-9387bca0bcc6" }
)
$f = "./artifacts/aad.json"; New-Item -ItemType Directory -Force -Path (Split-Path $f) | Out-Null
$aad | ConvertTo-Json -AsArray | Set-Content $f -Encoding utf8
az webapp config appsettings set --name $API_APP --resource-group $RESOURCE_GROUP --settings "@$f"
Remove-Item $f -ErrorAction SilentlyContinue