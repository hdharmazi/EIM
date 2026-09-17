param namespaceName string = 'asb-eim-wtc-nonprod'
param queueName string = 'sample'
param location string = resourceGroup().location

resource ns 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: namespaceName
  location: location
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
}

param queueNames array = [
  'bat-capture-to-d365fsc-customer'
  'bat-capture-to-d365fsc-customer-response'
  'bat-capture-to-d365fsc-contact'
  'bat-capture-to-d365fsc-contact-response'
  'bat-capture-to-d365fsc-customeraddress'
  'bat-capture-to-d365fsc-customeraddress-response'
  'bat-capture-to-d365fsc-partycontact'
  'bat-capture-to-d365fsc-partycontact-response'
  'bat-d365fsc-to-capture_customer'
  'bat-d365fsc-to-capture_contact'
  'bat-d365fsc-to-capture_customeraddress'
  'bat-d365fsc-to-capture_partycontact'
  'bat-d365fsc-dataevent'
]
resource queue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: ns
  name: queueName
  properties: {
    lockDuration: 'PT1M'
    maxDeliveryCount: 10
    defaultMessageTimeToLive: 'P14D'
    deadLetteringOnMessageExpiration: true
    enableBatchedOperations: true
    requiresDuplicateDetection: false
    requiresSession: false
    status: 'Active'
  }
}
