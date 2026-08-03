$SERVICEBUS_CONNECTION     = "Endpoint=sb://asb-eim-wtc-nonprod.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=4gzdDB8HYmPSui549y8z+o+zHtFySmtM9+ASbLQswiw="
$API_APP      = "wtc-dashboard-api-dev"

$simSettings = @(
  @{ name = "Simulation__ServiceBusConnectionString"; value = $SERVICEBUS_CONNECTION }
  @{ name = "Simulation__D365Queue";                  value = "bat-d365fsc-dataevent" }
  @{ name = "Simulation__CaptureToD365Queue";         value = "bat-capture-to-d365fsc" }
  @{ name = "Simulation__CaptureToD365ResponseQueue"; value = "bat-capture-to-d365fsc-response" }
  @{ name = "Simulation__D365ToCaptureQueue";         value = "bat-d365fsc-to-capture" }
)
$simFile = "./artifacts/sim-appsettings.json"
New-Item -ItemType Directory -Force -Path (Split-Path $simFile) | Out-Null
$simSettings | ConvertTo-Json -AsArray | Set-Content -Path $simFile -Encoding utf8

az webapp config appsettings set --name $API_APP --resource-group $RESOURCE_GROUP --settings "@$simFile"

Remove-Item $simFile -ErrorAction SilentlyContinue