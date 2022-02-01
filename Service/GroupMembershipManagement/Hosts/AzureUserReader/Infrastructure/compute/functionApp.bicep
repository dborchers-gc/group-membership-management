@description('Function app name.')
@minLength(1)
param name string

@description('Function app kind.')
@allowed([
  'functionapp'
  'linux'
  'container'
])
param kind string = 'functionapp'

@description('Function app location.')
param location string

@description('Service plan name.')
@minLength(1)
param servicePlanName string

@description('Array of key vault references to be set in app settings')
param secretSettings array

resource functionApp 'Microsoft.Web/sites@2018-02-01' = {
  name: name
  location: location
  kind: kind
  properties: {
    serverFarmId: resourceId('Microsoft.Web/serverfarms', servicePlanName)
    clientAffinityEnabled: false
    httpsOnly: true
    siteConfig: {
      appSettings: secretSettings
    }
  }
  identity: {
    type: 'SystemAssigned'
  }
}

output msi string = functionApp.identity.principalId
output hostName string = 'https://${functionApp.properties.defaultHostName}'
output adfKey string = listkeys('${functionApp.id}/host/default', '2018-11-01').functionKeys.default
