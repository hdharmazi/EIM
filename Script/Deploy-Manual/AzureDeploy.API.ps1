# 0. Make sure you're logged in to the right subscription
az login
az account show

# 1. Publish + zip
$API_PUBLISH = "./artifacts/api-publish"
$API_ZIP     = "./artifacts/api.zip"
dotnet publish ARM.Integration.Dashboard.API/ARM.Integration.Dashboard.API.csproj `
  --configuration Release `
  --output $API_PUBLISH
if (Test-Path $API_ZIP) { Remove-Item $API_ZIP }
Compress-Archive -Path "$API_PUBLISH/*" -DestinationPath $API_ZIP

# 2. Zip-deploy to the existing Web App
az webapp deploy `
  --resource-group "d365fsc-integration-nonprod" `
  --name "wtc-dashboard-api-dev" `
  --src-path $API_ZIP `
  --type zip