param namespaceName string = 'asb-eim-wtc-nonprod'
param location string = resourceGroup().location

resource ns 'Microsoft.ServiceBus/namespaces@2024-01-01'existing = {
  name: namespaceName
}

param queueNames array = [
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
  'test-d365fsc-dataevent'
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
