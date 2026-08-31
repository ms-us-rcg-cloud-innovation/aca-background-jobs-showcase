// =============================================================================
// Azure Container Apps - Background Jobs Showcase
// Provisions everything needed to demonstrate Hangfire-style background
// processing on Azure-native primitives:
//   - Container Apps Environment (+ Log Analytics)
//   - Azure Container Registry (referenced; images pushed by deploy script)
//   - User-assigned managed identity (passwordless auth to SB, SQL, ACR)
//   - Azure Service Bus namespace + queue (the durable job queue)
//   - Azure SQL server + database (job state; matches customer "MS SQL only")
//   - 2 Container Apps:  producer-api (enqueue/schedule) + dashboard (monitor)
//   - 3 Container Apps Jobs:
//        event-worker      -> KEDA Service Bus scaler  (fire-and-forget/delayed)
//        scheduled-worker  -> CRON trigger             (recurring)
//        manual-worker     -> manual trigger           (on-demand batch)
// Resource-group scoped. Deploy with scripts/deploy.ps1 or scripts/deploy.sh.
// =============================================================================

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('Azure region for the SQL server/database. Defaults to `location`; override when the primary region has SQL capacity restrictions.')
param sqlLocation string = location

@description('Short prefix used to name resources (lowercase letters/numbers).')
@minLength(3)
@maxLength(12)
param namePrefix string = 'acajobs'

@description('Name of an existing Azure Container Registry that holds the images.')
param acrName string

@description('Container image tag to deploy (e.g. a git SHA or "latest").')
param imageTag string = 'latest'

@description('Entra ID (AAD) admin login/display name for the SQL server.')
param aadAdminLogin string

@description('Entra ID (AAD) admin object id (a user or group) for the SQL server.')
param aadAdminObjectId string

// ---- Fixed values --------------------------------------------------------
var queueName = 'jobs'
var tags = { workload: 'aca-background-jobs-showcase', managedBy: 'bicep' }

var sbDataOwnerRoleId = '090c5cfd-751d-490a-894a-3ce6f1109419' // Azure Service Bus Data Owner
var acrPullRoleId     = '7f951dda-4ed3-4680-a7ca-43fe172d538d' // AcrPull

var sbNamespaceName = '${namePrefix}-sb-${uniqueString(resourceGroup().id)}'
var sqlServerName   = '${namePrefix}-sql-${uniqueString(resourceGroup().id)}'
var sqlDbName       = 'jobs'
var envName         = '${namePrefix}-env'
var lawName         = '${namePrefix}-law'
var uamiName        = '${namePrefix}-id'

// ---- Managed identity ----------------------------------------------------
resource uami 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: uamiName
  location: location
  tags: tags
}

// ---- Log Analytics + Container Apps Environment --------------------------
resource law 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: lawName
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource env 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: envName
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: law.properties.customerId
        sharedKey: law.listKeys().primarySharedKey
      }
    }
  }
}

// ---- Existing ACR + AcrPull for the identity -----------------------------
resource acr 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' existing = {
  name: acrName
}

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, uami.id, acrPullRoleId)
  scope: acr
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    principalId: uami.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// ---- Service Bus (the durable job queue) ---------------------------------
resource sb 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: sbNamespaceName
  location: location
  tags: tags
  sku: { name: 'Standard', tier: 'Standard' }
}

resource sbQueue 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' = {
  parent: sb
  name: queueName
  properties: {
    maxDeliveryCount: 5           // after 5 attempts -> dead-letter (Hangfire-style retry cap)
    lockDuration: 'PT5M'
    deadLetteringOnMessageExpiration: true
  }
}

resource sbRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(sb.id, uami.id, sbDataOwnerRoleId)
  scope: sb
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', sbDataOwnerRoleId)
    principalId: uami.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// ---- Azure SQL (job state) ----------------------------------------------
// Entra ID (AAD) only authentication — no SQL admin login/password. Passwordless
// end to end, and required by MCAPS "Safe Secrets Standard" policy.
resource sql 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: sqlServerName
  location: sqlLocation
  tags: tags
  properties: {
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: 'User'
      login: aadAdminLogin
      sid: aadAdminObjectId
      tenantId: tenant().tenantId
      azureADOnlyAuthentication: true
    }
  }
}

resource sqlDb 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sql
  name: sqlDbName
  location: sqlLocation
  tags: tags
  sku: { name: 'S0', tier: 'Standard' }
}

// Allow other Azure services (incl. Container Apps) to reach the server.
resource sqlFwAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sql
  name: 'AllowAllAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// ---- Shared config -------------------------------------------------------
var acrLoginServer = acr.properties.loginServer
var uamiClientId = uami.properties.clientId
var sqlConnString = 'Server=tcp:${sql.properties.fullyQualifiedDomainName},1433;Database=${sqlDbName};Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;'
var sbFqns = '${sbNamespaceName}.servicebus.windows.net'

// Environment variables shared by every container. AZURE_CLIENT_ID pins
// DefaultAzureCredential (and SqlClient "Active Directory Default") to the UAMI.
var commonEnv = [
  { name: 'AZURE_CLIENT_ID', value: uamiClientId }
  { name: 'SQL_CONNECTION_STRING', value: sqlConnString }
  { name: 'SERVICEBUS_FQNS', value: sbFqns }
  { name: 'SERVICEBUS_QUEUE', value: queueName }
]

var identityBlock = {
  type: 'UserAssigned'
  userAssignedIdentities: {
    '${uami.id}': {}
  }
}

var registriesBlock = [
  {
    server: acrLoginServer
    identity: uami.id
  }
]

// ---- Container App: producer-api -----------------------------------------
resource producer 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${namePrefix}-producer-api'
  location: location
  tags: tags
  identity: identityBlock
  properties: {
    managedEnvironmentId: env.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
      }
      registries: registriesBlock
    }
    template: {
      containers: [
        {
          name: 'producer-api'
          image: '${acrLoginServer}/producer-api:${imageTag}'
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: commonEnv
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 3
        rules: [
          { name: 'http', http: { metadata: { concurrentRequests: '50' } } }
        ]
      }
    }
  }
  dependsOn: [ acrPull, sbRole ]
}

// ---- Container App: dashboard --------------------------------------------
resource dashboard 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${namePrefix}-dashboard'
  location: location
  tags: tags
  identity: identityBlock
  properties: {
    managedEnvironmentId: env.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
      }
      registries: registriesBlock
    }
    template: {
      containers: [
        {
          name: 'dashboard'
          image: '${acrLoginServer}/dashboard:${imageTag}'
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: commonEnv
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 2
        rules: [
          { name: 'http', http: { metadata: { concurrentRequests: '50' } } }
        ]
      }
    }
  }
  dependsOn: [ acrPull ]
}

// ---- Job: event-worker (KEDA Service Bus scaler) -------------------------
// Fire-and-forget + delayed work. Scales from 0 based on queue depth.
resource eventJob 'Microsoft.App/jobs@2024-10-02-preview' = {
  name: '${namePrefix}-event-worker'
  location: location
  tags: tags
  identity: identityBlock
  properties: {
    environmentId: env.id
    configuration: {
      triggerType: 'Event'
      replicaTimeout: 1800
      replicaRetryLimit: 1
      registries: registriesBlock
      eventTriggerConfig: {
        replicaCompletionCount: 1
        parallelism: 1
        scale: {
          minExecutions: 0
          maxExecutions: 10
          pollingInterval: 30
          rules: [
            {
              name: 'servicebus'
              type: 'azure-servicebus'
              metadata: {
                queueName: queueName
                namespace: sbNamespaceName
                messageCount: '5'
              }
              identity: uami.id
            }
          ]
        }
      }
    }
    template: {
      containers: [
        {
          name: 'event-worker'
          image: '${acrLoginServer}/event-worker:${imageTag}'
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: commonEnv
        }
      ]
    }
  }
  dependsOn: [ acrPull, sbRole, sbQueue ]
}

// ---- Job: scheduled-worker (CRON) ----------------------------------------
// Recurring work. CRON schedule lives on the platform, not an always-on server.
resource scheduledJob 'Microsoft.App/jobs@2024-10-02-preview' = {
  name: '${namePrefix}-scheduled-worker'
  location: location
  tags: tags
  identity: identityBlock
  properties: {
    environmentId: env.id
    configuration: {
      triggerType: 'Schedule'
      replicaTimeout: 1800
      replicaRetryLimit: 1
      registries: registriesBlock
      scheduleTriggerConfig: {
        cronExpression: '0 2 * * *' // every day at 02:00 UTC
        parallelism: 1
        replicaCompletionCount: 1
      }
    }
    template: {
      containers: [
        {
          name: 'scheduled-worker'
          image: '${acrLoginServer}/scheduled-worker:${imageTag}'
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: commonEnv
        }
      ]
    }
  }
  dependsOn: [ acrPull, sbRole ]
}

// ---- Job: manual-worker (on-demand) --------------------------------------
// Long-running batch started on demand (az containerapp job start ...).
resource manualJob 'Microsoft.App/jobs@2024-10-02-preview' = {
  name: '${namePrefix}-manual-worker'
  location: location
  tags: tags
  identity: identityBlock
  properties: {
    environmentId: env.id
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 3600
      replicaRetryLimit: 0
      registries: registriesBlock
      manualTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
      }
    }
    template: {
      containers: [
        {
          name: 'manual-worker'
          image: '${acrLoginServer}/manual-worker:${imageTag}'
          resources: { cpu: json('1.0'), memory: '2Gi' }
          env: concat(commonEnv, [ { name: 'BATCH_SIZE', value: '10' } ])
        }
      ]
    }
  }
  dependsOn: [ acrPull ]
}

// ---- Outputs -------------------------------------------------------------
output producerUrl string = 'https://${producer.properties.configuration.ingress.fqdn}'
output dashboardUrl string = 'https://${dashboard.properties.configuration.ingress.fqdn}'
output sqlServerFqdn string = sql.properties.fullyQualifiedDomainName
output sqlDatabase string = sqlDbName
output serviceBusNamespace string = sbNamespaceName
output managedIdentityClientId string = uamiClientId
output managedIdentityName string = uamiName
output acrLoginServer string = acrLoginServer
output eventJobName string = eventJob.name
output scheduledJobName string = scheduledJob.name
output manualJobName string = manualJob.name
