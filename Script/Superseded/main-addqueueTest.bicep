param namespaceName string = 'asb-eim-wtc-nonprod'
param location string = resourceGroup().location

resource ns 'Microsoft.ServiceBus/namespaces@2024-01-01'existing = {
  name: namespaceName
}

param queueNames array = [
  'dev-capture-to-d365fsc'
  'dev-capture-to-d365fsc-response'
  'dev-d365fsc-to-capture'
  'dev-d365fsc-dataevent'
]

resource queue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = [for q in queueNames: {
  parent: ns
  name: q
  properties: {
    lockDuration: 'PT1M'
    maxDeliveryCount: 10
    defaultMessageTimeToLive: 'P14D'
    deadLetteringOnMessageExpiration: true
    enableBatchedOperations: true
    requiresDuplicateDetection: true
    requiresSession: false
    status: 'Active'
  }
}]
