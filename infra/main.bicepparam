using './main.bicep'

param namePrefix = 'loanob'
param environmentName = 'dev'

param sqlAdminLogin = 'loanadmin'
param sqlAdminPassword = readEnvironmentVariable('SQL_ADMIN_PASSWORD', '')

param sqlDatabaseSku = 'S0'
param appServicePlanSku = 'B1'

// Fill these from your Entra ID app registration (see README).
param aadClientId = readEnvironmentVariable('AAD_CLIENT_ID', 'REPLACE_WITH_API_APP_CLIENT_ID')
