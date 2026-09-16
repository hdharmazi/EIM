param location string = resourceGroup().location
param keyVaultName string = 'kv-d365fsc-wtc-nonprod2'

@secure()
param asbConnectionString string

resource kv 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
  }
}

resource secret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: kv
  name: 'asb'
  properties: {
    value: asbConnectionString
  }
}