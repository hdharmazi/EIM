# Delete specific queues from a Service Bus namespace
# Usage: .\delete-queue.ps1

$namespaceName = "asb-eim-wtc-nonprod"
$resourceGroup = "d365fsc-integration-nonprod"

$queuesToDelete = @(
  'test-capture-to-d365fsc-customer'
  'test-capture-to-d365fsc-customer-response'
  'test-capture-to-d365fsc-contact'
  'test-capture-to-d365fsc-contact-response'
  'test-capture-to-d365fsc-customeraddress'
  'test-capture-to-d365fsc-customeraddress-response'
  'test-capture-to-d365fsc-partycontact'
  'test-capture-to-d365fsc-partycontact-response'
  'test-d365fsc-to-capture-customer'
  'test-d365fsc-to-capture-contact'
  'test-d365fsc-to-capture-customeraddress'
  'test-d365fsc-to-capture-partycontact'
)

foreach ($queue in $queuesToDelete) {
    Write-Host "Deleting queue: $queue"
    az servicebus queue delete `
        --resource-group $resourceGroup `
        --namespace-name $namespaceName `
        --name $queue
    Write-Host "Deleted: $queue"
}

Write-Host "Done."