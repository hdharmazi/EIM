param namespaceName string = 'asb-eim-wtc-nonprod'
param location string = resourceGroup().location

resource ns 'Microsoft.ServiceBus/namespaces@2024-01-01'existing = {
  name: namespaceName
}

param queueNames array = [
  'bat-capture-to-d365fsc'
  'bat-capture-to-d365fsc-response'
  'bat-d365fsc-to-capture'
  'bat-d365fsc-dataevent'
  'test-capture-to-d365fsc'
  'test-capture-to-d365fsc-response'
  'test-d365fsc-dataevent'
  'test-d365fsc-to-capture'
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
