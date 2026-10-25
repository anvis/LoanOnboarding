// Loan onboarding infrastructure: App Service (API), Function App, Azure SQL, Blob Storage, Service Bus,
// Application Insights. Connection strings are wired into the app settings automatically.
//
// Tables cannot be created by Bicep. Run database/schema.sql after this deployment (the deploy-infra workflow does it).

targetScope = 'resourceGroup'

@description('Short lowercase prefix for resource names (letters and digits, max 8).')
@maxLength(8)
param namePrefix string = 'loanob'

@description('Environment name used in resource names.')
@maxLength(4)
param environmentName string = 'dev'

param location string = resourceGroup().location

@description('SQL administrator login.')
param sqlAdminLogin string = 'loanadmin'

@secure()
@description('SQL administrator password.')
param sqlAdminPassword string

@description('Azure SQL database SKU name. Basic and S0 are the cheapest.')
param sqlDatabaseSku string = 'S0'

@description('App Service plan SKU shared by the API and the Function App. Always On needs B1 or higher.')
param appServicePlanSku string = 'B1'

@description('Entra tenant id used to validate API tokens.')
param aadTenantId string = tenant().tenantId

@description('Client id of the API app registration in Entra ID.')
param aadClientId string = 'REPLACE_WITH_API_APP_CLIENT_ID'

@description('Token audience. Defaults to api://<clientId>.')
param aadAudience string = 'api://${aadClientId}'

@secure()
@description('Key used to HMAC national identifiers. Keep it stable; rotate through Key Vault in production.')
param identifierHashKey string = uniqueString(resourceGroup().id, 'identifier-hash-key')

@description('Optional public IP to allow through the SQL firewall (for running schema.sql from your machine).')
param allowedClientIp string = ''

var suffix = take(uniqueString(resourceGroup().id), 6)
var baseName = '${namePrefix}-${environmentName}-${suffix}'
var docsStorageName = take(toLower('${namePrefix}${environmentName}docs${suffix}'), 24)
var funcStorageName = take(toLower('${namePrefix}${environmentName}func${suffix}'), 24)
var sqlServerName = toLower('sql-${baseName}')
var sqlDatabaseName = 'LoanOnboarding'
var documentsContainer = 'loan-documents'

// ---------------------------------------------------------------- Monitoring
resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${baseName}'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${baseName}'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

// ---------------------------------------------------------------- Storage
resource docsStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: docsStorageName
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource docsBlobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: docsStorage
  name: 'default'
}

resource docsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: docsBlobService
  name: documentsContainer
  properties: { publicAccess: 'None' }
}

resource funcStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: funcStorageName
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

// ---------------------------------------------------------------- Service Bus
// Standard tier is the minimum: topics, sessions and duplicate detection are not in Basic.
resource serviceBus 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: 'sb-${baseName}'
  location: location
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
  properties: {
    minimumTlsVersion: '1.2'
  }
}

// Least-privilege rule for the Function App (send to topics it publishes to, listen on its subscriptions).
resource sbFunctionRule 'Microsoft.ServiceBus/namespaces/authorizationRules@2024-01-01' = {
  parent: serviceBus
  name: 'function-app'
  properties: { rights: ['Listen', 'Send'] }
}

var stageSubscriptions = [
  { name: 'sub-verification', stage: 'Verification' }
  { name: 'sub-customer-check', stage: 'CustomerCheck' }
  { name: 'sub-credit-check', stage: 'CreditCheck' }
  { name: 'sub-underwriting', stage: 'Underwriting' }
]

resource commandsTopic 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  parent: serviceBus
  name: 'loan.stage.commands'
  properties: {
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: 'PT10M'
    defaultMessageTimeToLive: 'P7D'
  }
}

// One subscription per stage team. The rule named $Default replaces the match-everything default rule.
resource stageSubs 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2024-01-01' = [for s in stageSubscriptions: {
  parent: commandsTopic
  name: s.name
  properties: {
    requiresSession: true
    maxDeliveryCount: 10
    lockDuration: 'PT1M'
    deadLetteringOnMessageExpiration: true
    defaultMessageTimeToLive: 'P7D'
  }
}]

resource stageRules 'Microsoft.ServiceBus/namespaces/topics/subscriptions/rules@2024-01-01' = [for (s, i) in stageSubscriptions: {
  parent: stageSubs[i]
  name: '$Default'
  properties: {
    filterType: 'SqlFilter'
    sqlFilter: { sqlExpression: 'stage = \'${s.stage}\'' }
  }
}]

resource resultsTopic 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  parent: serviceBus
  name: 'loan.stage.results'
  properties: {
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: 'PT10M'
    defaultMessageTimeToLive: 'P7D'
  }
}

resource orchestratorSub 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2024-01-01' = {
  parent: resultsTopic
  name: 'sub-orchestrator'
  properties: {
    requiresSession: true
    maxDeliveryCount: 10
    lockDuration: 'PT1M'
    deadLetteringOnMessageExpiration: true
  }
}

resource notificationsTopic 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  parent: serviceBus
  name: 'loan.notifications'
  properties: {
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: 'PT10M'
    defaultMessageTimeToLive: 'P7D'
  }
}

resource edocSub 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2024-01-01' = {
  parent: notificationsTopic
  name: 'sub-edoc'
  properties: {
    maxDeliveryCount: 10
    lockDuration: 'PT1M'
    deadLetteringOnMessageExpiration: true
  }
}

resource eventsTopic 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  parent: serviceBus
  name: 'loan.events'
  properties: {
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: 'PT10M'
    defaultMessageTimeToLive: 'P7D'
  }
}

resource dataSyncSub 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2024-01-01' = {
  parent: eventsTopic
  name: 'sub-datasync'
  properties: {
    maxDeliveryCount: 10
    lockDuration: 'PT1M'
    deadLetteringOnMessageExpiration: true
  }
}

resource dataSyncRule 'Microsoft.ServiceBus/namespaces/topics/subscriptions/rules@2024-01-01' = {
  parent: dataSyncSub
  name: '$Default'
  properties: {
    filterType: 'SqlFilter'
    sqlFilter: { sqlExpression: 'sys.Label = \'LoanBooked\'' }
  }
}

resource originationSub 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2024-01-01' = {
  parent: eventsTopic
  name: 'sub-origination'
  properties: {
    maxDeliveryCount: 10
    lockDuration: 'PT1M'
    deadLetteringOnMessageExpiration: true
  }
}

resource originationRule 'Microsoft.ServiceBus/namespaces/topics/subscriptions/rules@2024-01-01' = {
  parent: originationSub
  name: '$Default'
  properties: {
    filterType: 'SqlFilter'
    sqlFilter: { sqlExpression: 'sys.Label = \'SyncCompleted\'' }
  }
}

// ---------------------------------------------------------------- Azure SQL
resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: sqlServerName
  location: location
  properties: {
    administratorLogin: sqlAdminLogin
    administratorLoginPassword: sqlAdminPassword
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: sqlDatabaseName
  location: location
  sku: { name: sqlDatabaseSku }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
  }
}

// 0.0.0.0 means "Azure services", which lets the App Service and the Function App connect.
resource sqlAllowAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource sqlAllowClient 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = if (!empty(allowedClientIp)) {
  parent: sqlServer
  name: 'AllowClientIp'
  properties: {
    startIpAddress: allowedClientIp
    endIpAddress: allowedClientIp
  }
}

// ---------------------------------------------------------------- Hosting
resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: 'asp-${baseName}'
  location: location
  kind: 'linux'
  sku: { name: appServicePlanSku }
  properties: { reserved: true }
}

var sqlConnectionString = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Initial Catalog=${sqlDatabaseName};User ID=${sqlAdminLogin};Password=${sqlAdminPassword};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'
var docsConnectionString = 'DefaultEndpointsProtocol=https;AccountName=${docsStorage.name};AccountKey=${docsStorage.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}'
var funcStorageConnectionString = 'DefaultEndpointsProtocol=https;AccountName=${funcStorage.name};AccountKey=${funcStorage.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}'
var serviceBusConnectionString = sbFunctionRule.listKeys().primaryConnectionString

resource webApp 'Microsoft.Web/sites@2023-12-01' = {
  name: 'app-${baseName}'
  location: location
  kind: 'app,linux'
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      healthCheckPath: '/health'
      appSettings: [
        { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
        { name: 'ConnectionStrings__Sql', value: sqlConnectionString }
        { name: 'ConnectionStrings__BlobStorage', value: docsConnectionString }
        { name: 'Blob__ContainerName', value: documentsContainer }
        { name: 'AzureAd__Instance', value: environment().authentication.loginEndpoint }
        { name: 'AzureAd__TenantId', value: aadTenantId }
        { name: 'AzureAd__ClientId', value: aadClientId }
        { name: 'AzureAd__Audience', value: aadAudience }
        { name: 'Loan__IdentifierHashKey', value: identifierHashKey }
      ]
    }
  }
}

resource functionApp 'Microsoft.Web/sites@2023-12-01' = {
  name: 'func-${baseName}'
  location: location
  kind: 'functionapp,linux'
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNET-ISOLATED|10.0'
      alwaysOn: true // timer triggers (outbox dispatcher, sweeper) need the host to stay running
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      appSettings: [
        { name: 'AzureWebJobsStorage', value: funcStorageConnectionString }
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'dotnet-isolated' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
        { name: 'ConnectionStrings__Sql', value: sqlConnectionString }
        { name: 'ServiceBusConnection', value: serviceBusConnectionString }
        { name: 'Loan__IdentifierHashKey', value: identifierHashKey }
      ]
    }
  }
}

// ---------------------------------------------------------------- Outputs (no secrets)
output webAppName string = webApp.name
output webAppUrl string = 'https://${webApp.properties.defaultHostName}'
output functionAppName string = functionApp.name
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output sqlServerName string = sqlServer.name
output sqlDatabaseName string = sqlDatabaseName
output serviceBusNamespace string = serviceBus.name
output documentsStorageAccount string = docsStorage.name
