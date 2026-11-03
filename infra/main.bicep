// Infrastructure as code for the Capacity Router.
// Creates: Log Analytics workspace, Application Insights, a Linux App Service plan and the web app.
// Deploy with:
//   az group create -n rg-capacity-router -l centralindia
//   az deployment group create -g rg-capacity-router -f infra/main.bicep -p appName=capacityrouter-<yourname>

@description('Globally unique name for the web app, e.g. capacityrouter-anjali')
param appName string

param location string = resourceGroup().location

@description('App Service plan size. F1 is free; B1 is cheap and supports always-on.')
@allowed([
  'F1'
  'B1'
])
param skuName string = 'F1'

resource logs 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: '${appName}-logs'
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${appName}-ai'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

resource plan 'Microsoft.Web/serverfarms@2023-01-01' = {
  name: '${appName}-plan'
  location: location
  kind: 'linux'
  sku: {
    name: skuName
  }
  properties: {
    reserved: true
  }
}

resource app 'Microsoft.Web/sites@2023-01-01' = {
  name: appName
  location: location
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|8.0'
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appSettings: [
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: insights.properties.ConnectionString
        }
      ]
    }
  }
}

output appUrl string = 'https://${app.properties.defaultHostName}'
