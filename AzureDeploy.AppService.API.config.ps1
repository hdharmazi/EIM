$simSettings = @(
  @{ name = "Simulation__ServiceBusConnectionString"; value = $SERVICEBUS_CONNECTION }
  @{ name = "Simulation__D365Queue";                  value = "dev-d365fsc-dataevent" }
  @{ name = "Simulation__CaptureToD365Queue";         value = "dev-capture-to-d365fsc" }
  @{ name = "Simulation__CaptureToD365ResponseQueue"; value = "dev-capture-to-d365fsc-response" }
  @{ name = "Simulation__D365ToCaptureQueue";         value = "dev-d365fsc-to-capture" }
)
$simFile = "./artifacts/sim-appsettings.json"
New-Item -ItemType Directory -Force -Path (Split-Path $simFile) | Out-Null
$simSettings | ConvertTo-Json -AsArray | Set-Content -Path $simFile -Encoding utf8

az webapp config appsettings set --name $API_APP --resource-group $RESOURCE_GROUP --settings "@$simFile"

Remove-Item $simFile -ErrorAction SilentlyContinue