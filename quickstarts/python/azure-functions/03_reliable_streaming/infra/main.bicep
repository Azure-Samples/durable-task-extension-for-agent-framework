targetScope = 'subscription'

@minLength(1)
@maxLength(64)
@description('Name of the environment which is used to generate a short unique hash used in all resources.')
param environmentName string

@minLength(1)
@description('Primary location for all resources')
@metadata({
  azd: {
    type: 'location'
  }
})
param location string

@description('Skip the creation of the virtual network and private endpoint')
param skipVnet bool = true

@description('Name of the API service')
param apiServiceName string = ''

@description('Name of the user assigned identity')
param apiUserAssignedIdentityName string = ''

@description('Name of the application insights resource')
param applicationInsightsName string = ''

@description('Name of the app service plan')
param appServicePlanName string = ''

@description('Name of the log analytics workspace')
param logAnalyticsName string = ''

@description('Name of the resource group')
param resourceGroupName string = ''

@description('Name of the storage account')
param storageAccountName string = ''

@description('Name of the virtual network')
param vNetName string = ''

@description('Disable local authentication for Azure Monitor')
param disableLocalAuth bool = true

@description('Name of the Durable Task Scheduler')
param dtsName string = ''

@description('Name of the task hub')
param taskHubName string = ''

@description('Durable Task Scheduler SKU name')
param dtsSkuName string = 'Consumption'

@description('Id of the user or app to assign application roles')
param principalId string = deployer().objectId

@description('Name of the Azure AI Services account')
param aiServicesName string = 'agentaiservices'

@description('Model name for deployment')
param modelName string = 'gpt-4o-mini'

@description('Model format for deployment')
param modelFormat string = 'OpenAI'

@description('Model version for deployment')
param modelVersion string = '2024-07-18'

@description('Model deployment SKU name')
param modelSkuName string = 'S0'

@description('Model deployment capacity')
param modelCapacity int = 10

@description('Model deployment location')
param modelLocation string = location

@description('The AI Service Account full ARM Resource ID. Optional - if not provided, the resource will be created.')
param aiServiceAccountResourceId string = ''

// Variables
var abbrs = loadJsonContent('./abbreviations.json')
var resourceToken = toLower(uniqueString(subscription().id, rg.id, environmentName, location))
var aiResourceToken = toLower(uniqueString(subscription().id, rg.id, environmentName, modelLocation))
var tags = { 'azd-env-name': environmentName }
var functionAppName = !empty(apiServiceName) ? apiServiceName : '${abbrs.webSitesFunctions}api-${resourceToken}'
var deploymentStorageContainerName = 'app-package-${take(functionAppName, 32)}-${take(toLower(uniqueString(functionAppName, resourceToken)), 7)}'

// Organize resources in a resource group
resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: !empty(resourceGroupName) ? resourceGroupName : '${abbrs.resourcesResourceGroups}${environmentName}'
  location: location
  tags: tags
}

// User assigned managed identity using AVM
module apiUserAssignedIdentity 'br/public:avm/res/managed-identity/user-assigned-identity:0.4.0' = {
  name: 'apiUserAssignedIdentity-${resourceToken}'
  scope: rg
  params: {
    name: !empty(apiUserAssignedIdentityName) ? apiUserAssignedIdentityName : '${abbrs.managedIdentityUserAssignedIdentities}api-${resourceToken}'
    location: location
    tags: tags
  }
}

// Backing storage for Azure Functions using AVM
module storage 'br/public:avm/res/storage/storage-account:0.14.3' = {
  scope: rg
  name: 'storage-${resourceToken}'
  params: {
    name: !empty(storageAccountName) ? storageAccountName : '${abbrs.storageStorageAccounts}${resourceToken}'
    location: location
    tags: tags
    kind: 'StorageV2'
    skuName: 'Standard_LRS'
    allowSharedKeyAccess: false
    blobServices: {
      containers: [
        { name: deploymentStorageContainerName }
      ]
    }
    publicNetworkAccess: skipVnet ? 'Enabled' : 'Disabled'
    networkAcls: skipVnet ? {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    } : {
      defaultAction: 'Deny'
      bypass: 'AzureServices'
    }
    roleAssignments: [
      {
        principalId: apiUserAssignedIdentity.outputs.principalId
        roleDefinitionIdOrName: 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b' // Storage Blob Data Owner
        principalType: 'ServicePrincipal'
      }
      {
        principalId: apiUserAssignedIdentity.outputs.principalId
        roleDefinitionIdOrName: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe' // Storage Blob Data Contributor
        principalType: 'ServicePrincipal'
      }
      {
        principalId: apiUserAssignedIdentity.outputs.principalId
        roleDefinitionIdOrName: '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3' // Storage Table Data Contributor
        principalType: 'ServicePrincipal'
      }
      {
        principalId: apiUserAssignedIdentity.outputs.principalId
        roleDefinitionIdOrName: '974c5e8b-45b9-4653-ba55-5f855dd0fb88' // Storage Queue Data Contributor
        principalType: 'ServicePrincipal'
      }
      {
        principalId: principalId
        roleDefinitionIdOrName: 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b' // Storage Blob Data Owner
        principalType: 'User'
      }
      {
        principalId: principalId
        roleDefinitionIdOrName: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe' // Storage Blob Data Contributor
        principalType: 'User'
      }
      {
        principalId: principalId
        roleDefinitionIdOrName: '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3' // Storage Table Data Contributor
        principalType: 'User'
      }
      {
        principalId: principalId
        roleDefinitionIdOrName: '974c5e8b-45b9-4653-ba55-5f855dd0fb88' // Storage Queue Data Contributor
        principalType: 'User'
      }
    ]
  }
}

// App Service Plan using AVM - Flex Consumption
module appServicePlan 'br/public:avm/res/web/serverfarm:0.3.0' = {
  name: 'appserviceplan-${resourceToken}'
  scope: rg
  params: {
    name: !empty(appServicePlanName) ? appServicePlanName : '${abbrs.webServerFarms}${resourceToken}'
    location: location
    tags: tags
    skuName: 'FC1' // Flex Consumption
    reserved: true
  }
}

// Function App using AVM - Flex Consumption (Python)
module api 'br/public:avm/res/web/site:0.11.1' = {
  name: 'api-${resourceToken}'
  scope: rg
  params: {
    name: functionAppName
    location: location
    tags: union(tags, { 'azd-service-name': 'api' })
    kind: 'functionapp,linux'
    serverFarmResourceId: appServicePlan.outputs.resourceId
    managedIdentities: {
      userAssignedResourceIds: [apiUserAssignedIdentity.outputs.resourceId]
    }
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: 'https://${storage.outputs.name}.blob.${environment().suffixes.storage}/${deploymentStorageContainerName}'
          authentication: {
            type: 'UserAssignedIdentity'
            userAssignedIdentityResourceId: apiUserAssignedIdentity.outputs.resourceId
          }
        }
      }
      scaleAndConcurrency: {
        instanceMemoryMB: 2048
        maximumInstanceCount: 100
      }
      runtime: {
        name: 'python'
        version: '3.11'
      }
    }
    virtualNetworkSubnetId: skipVnet ? '' : null
    siteConfig: {
      alwaysOn: false
      appSettings: [
        { name: 'AzureWebJobsStorage__credential', value: 'managedidentity' }
        { name: 'AzureWebJobsStorage__clientId', value: apiUserAssignedIdentity.outputs.clientId }
        { name: 'AzureWebJobsStorage__blobServiceUri', value: 'https://${storage.outputs.name}.blob.${environment().suffixes.storage}' }
        { name: 'AzureWebJobsStorage__queueServiceUri', value: 'https://${storage.outputs.name}.queue.${environment().suffixes.storage}' }
        { name: 'AzureWebJobsStorage__tableServiceUri', value: 'https://${storage.outputs.name}.table.${environment().suffixes.storage}' }
        { name: 'AzureWebJobsStorage__accountName', value: storage.outputs.name }
        { name: 'DURABLE_TASK_SCHEDULER_CONNECTION_STRING', value: 'Endpoint=${dts.outputs.dts_URL};Authentication=ManagedIdentity;ClientID=${apiUserAssignedIdentity.outputs.clientId}' }
        { name: 'TASKHUB_NAME', value: dts.outputs.TASKHUB_NAME }
        { name: 'AZURE_OPENAI_ENDPOINT', value: aiServiceExists ? reference(aiServiceAccountResourceId, '2023-05-01').endpoint : aiServices.outputs.endpoint }
        { name: 'AZURE_OPENAI_CHAT_DEPLOYMENT_NAME', value: modelName }
        { name: 'AZURE_CLIENT_ID', value: apiUserAssignedIdentity.outputs.clientId }
        { name: 'APPLICATIONINSIGHTS_AUTHENTICATION_STRING', value: 'ClientId=${apiUserAssignedIdentity.outputs.clientId};Authorization=AAD' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: monitoring.outputs.connectionString }
      ]
    }
  }
}

// AI Services configuration
var aiServiceExists = aiServiceAccountResourceId != ''
var aiServiceName = '${aiServicesName}${aiResourceToken}'

// AI Services (Cognitive Services) using AVM with model deployment
module aiServices 'br/public:avm/res/cognitive-services/account:0.9.0' = if (!aiServiceExists) {
  scope: rg
  name: 'aiServices-${aiResourceToken}'
  params: {
    name: aiServiceName
    location: modelLocation
    tags: tags
    kind: 'AIServices'
    customSubDomainName: toLower(aiServiceName)
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: true
    sku: modelSkuName
    deployments: [
      {
        name: modelName
        model: {
          format: modelFormat
          name: modelName
          version: modelVersion
        }
        sku: {
          name: 'GlobalStandard'
          capacity: modelCapacity
        }
      }
    ]
    roleAssignments: [
      {
        principalId: apiUserAssignedIdentity.outputs.principalId
        roleDefinitionIdOrName: '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd' // Cognitive Services OpenAI User
        principalType: 'ServicePrincipal'
      }
      {
        principalId: principalId
        roleDefinitionIdOrName: '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd' // Cognitive Services OpenAI User
        principalType: 'User'
      }
    ]
  }
}

// Log Analytics Workspace using AVM
module logAnalytics 'br/public:avm/res/operational-insights/workspace:0.7.0' = {
  scope: rg
  name: 'logs-${resourceToken}'
  params: {
    name: !empty(logAnalyticsName) ? logAnalyticsName : '${abbrs.operationalInsightsWorkspaces}${resourceToken}'
    location: location
    tags: tags
  }
}

// Application Insights using AVM
module monitoring 'br/public:avm/res/insights/component:0.4.1' = {
  scope: rg
  name: 'monitoring-${resourceToken}'
  params: {
    name: !empty(applicationInsightsName) ? applicationInsightsName : '${abbrs.insightsComponents}${resourceToken}'
    location: location
    tags: tags
    workspaceResourceId: logAnalytics.outputs.resourceId
    disableLocalAuth: disableLocalAuth
    roleAssignments: [
      {
        principalId: apiUserAssignedIdentity.outputs.principalId
        roleDefinitionIdOrName: '3913510d-42f4-4e42-8a64-420c390055eb' // Monitoring Metrics Publisher
        principalType: 'ServicePrincipal'
      }
    ]
  }
}

// Durable Task Scheduler role assignments
var durableTaskDataContributorRoleDefinitionId = '0ad04412-c4d5-4796-b79c-f76d14c8d402'

module dtsRoleApi 'br/public:avm/ptn/authorization/resource-role-assignment:0.1.1' = {
  scope: rg
  name: 'dtsRoleApi-${resourceToken}'
  params: {
    principalId: apiUserAssignedIdentity.outputs.principalId
    roleDefinitionId: durableTaskDataContributorRoleDefinitionId
    principalType: 'ServicePrincipal'
    resourceId: dts.outputs.dts_ID
  }
}

module dtsRoleUser 'br/public:avm/ptn/authorization/resource-role-assignment:0.1.1' = {
  scope: rg
  name: 'dtsRoleUser-${resourceToken}'
  params: {
    principalId: principalId
    roleDefinitionId: durableTaskDataContributorRoleDefinitionId
    principalType: 'User'
    resourceId: dts.outputs.dts_ID
  }
}

// Durable Task Scheduler
module dts './app/dts.bicep' = {
  scope: rg
  name: 'dtsResource-${resourceToken}'
  params: {
    name: !empty(dtsName) ? dtsName : '${abbrs.dts}${resourceToken}'
    taskhubname: !empty(taskHubName) ? taskHubName : '${abbrs.taskhub}${resourceToken}'
    location: location
    tags: tags
    ipAllowlist: [
      '0.0.0.0/0'
    ]
    skuName: dtsSkuName
  }
}

// App outputs
output APPLICATIONINSIGHTS_CONNECTION_STRING string = monitoring.outputs.connectionString
output AZURE_LOCATION string = location
output SERVICE_API_NAME string = api.outputs.name
output SERVICE_API_URI string = 'https://${api.outputs.defaultHostname}'
output AZURE_FUNCTION_APP_NAME string = api.outputs.name
output RESOURCE_GROUP string = rg.name
output AZURE_OPENAI_ENDPOINT string = aiServiceExists ? reference(aiServiceAccountResourceId, '2023-05-01').endpoint : aiServices.outputs.endpoint
output AZURE_OPENAI_DEPLOYMENT_NAME string = modelName
