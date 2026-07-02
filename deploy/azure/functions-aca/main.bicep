// dotnet-diagnostics-mcp — Azure Functions on Azure Container Apps recipe.
//
// Deploys a Functions container image as a Container App with a co-located diag
// sidecar, sharing /tmp via an EmptyDir volume. This is the GA (2025) path for
// running Azure Functions on the Container Apps environment while keeping full
// control over the multi-container template — which is what lets us add the
// diagnostics sidecar. (The fully-managed Microsoft.Web/sites Functions-on-ACA
// kind does not expose arbitrary sidecars, so this recipe deploys the Functions
// runtime image directly as a Microsoft.App/containerApps app.)
//
// ISOLATED-WORKER NOTE: the .NET isolated model runs TWO processes in the app
// container — the Functions host (FunctionsNetHost) and the isolated worker
// (`dotnet <YourApp>.dll`) that runs your function code. BOTH publish a
// diagnostic socket in /tmp, so inspect_process(view="list") shows two .NET
// processes. Target the WORKER (your assembly), not the host, when collecting.
// See the README "Targeting the isolated worker" section.
//
// Validate without deploying:
//   az bicep build --file deploy/azure/functions-aca/main.bicep
//   az deployment group validate \
//     --resource-group <rg> \
//     --template-file deploy/azure/functions-aca/main.bicep \
//     --parameters name=fn-diag environmentId=<env-id> appImage=<functions-image> \
//                  diagBearerToken=<token> azureWebJobsStorage=<storage-conn>
//
// Smoke after a real deploy:
//   az containerapp exec -n <name> -g <rg> --container diag -- /bin/sh
//   # inside diag:
//   ls /tmp/dotnet-diagnostic-*  # should show host + worker sockets

@description('Container App name.')
param name string

@description('Azure region. Defaults to the resource group location.')
param location string = resourceGroup().location

@description('Resource ID of an existing Microsoft.App/managedEnvironments instance.')
param environmentId string

@description('Functions container image for the application (e.g. built FROM mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated8.0). Must be reachable by the Container App.')
param appImage string

@description('Image for the dotnet-diagnostics-mcp sidecar. Defaults to a released version tag (never :latest) so revisions are deterministic; for production override with a digest pin (image@sha256:...). See README.')
param diagImage string = 'ghcr.io/pedrosakuma/dotnet-diagnostics:0.15.0'

@description('TCP port the Functions host listens on. The azure-functions base images listen on 80.')
param appPort int = 80

@description('TCP port the diagnostics MCP container listens on.')
param diagPort int = 8787

@description('Functions worker runtime. Defaults to dotnet-isolated (the model that spawns a separate worker process — see README).')
param functionsWorkerRuntime string = 'dotnet-isolated'

@description('Storage account connection string the Functions host requires (AzureWebJobsStorage).')
@secure()
param azureWebJobsStorage string

@description('Which container ingress should target. Set to "diag" to expose the MCP endpoint externally; defaults to "app" (the Functions HTTP endpoint).')
@allowed([
  'app'
  'diag'
])
param ingressTarget string = 'app'

@description('Make ingress externally reachable. When false, the Container App is only reachable from within its environment.')
param externalIngress bool = true

@description('Bearer token enforced by the diagnostics MCP server. Provide a long random value; rotate on demand.')
@secure()
param diagBearerToken string

@description('Minimum number of replicas. Keep >= 1 so the diagnostic socket is always live (Functions-on-ACA can scale to zero otherwise).')
@minValue(0)
param minReplicas int = 1

@description('Maximum number of replicas.')
@minValue(1)
param maxReplicas int = 1

@description('Optional ACR-style registry to authenticate against, e.g. "myregistry.azurecr.io". Leave empty when images are anonymously pullable.')
param registryServer string = ''

@description('Username for `registryServer`. Ignored when registryServer is empty.')
param registryUsername string = ''

@description('Password for `registryServer`. Ignored when registryServer is empty.')
@secure()
param registryPassword string = ''

var hasRegistry = !empty(registryServer)
var ingressPort = ingressTarget == 'diag' ? diagPort : appPort
var sharedVolumeName = 'diag-tmp'

resource functionApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: name
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    environmentId: environmentId
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: externalIngress
        targetPort: ingressPort
        transport: 'auto'
        allowInsecure: false
      }
      secrets: concat([
        {
          name: 'diag-bearer-token'
          value: diagBearerToken
        }
        {
          name: 'azure-webjobs-storage'
          value: azureWebJobsStorage
        }
      ], hasRegistry ? [
        {
          name: 'registry-password'
          value: registryPassword
        }
      ] : [])
      registries: hasRegistry ? [
        {
          server: registryServer
          username: registryUsername
          passwordSecretRef: 'registry-password'
        }
      ] : []
    }
    template: {
      containers: [
        {
          name: 'app'
          image: appImage
          // The isolated worker (your assembly) creates a diagnostic socket in /tmp when
          // DOTNET_EnableDiagnostics != 0 (default). Sharing /tmp via EmptyDir exposes
          // both the host and worker sockets to the diag sidecar.
          env: [
            {
              name: 'AzureWebJobsStorage'
              secretRef: 'azure-webjobs-storage'
            }
            {
              name: 'FUNCTIONS_WORKER_RUNTIME'
              value: functionsWorkerRuntime
            }
            {
              name: 'DOTNET_EnableDiagnostics'
              value: '1'
            }
          ]
          volumeMounts: [
            {
              volumeName: sharedVolumeName
              mountPath: '/tmp'
            }
          ]
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
        }
        {
          name: 'diag'
          image: diagImage
          // The diag image's UID must match the worker's UID so the socket created by the
          // worker is readable. The default dotnet-diagnostics-mcp image runs as 10001; the
          // azure-functions base images run as root. Container Apps exposes no per-container
          // runAsUser, so align at image-build time (rebuild diag with USER root, or the
          // functions image with USER 10001). See README.
          command: [
            'dotnet'
            'DotnetDiagnostics.Mcp.dll'
          ]
          env: [
            {
              name: 'ASPNETCORE_URLS'
              value: 'http://0.0.0.0:${diagPort}'
            }
            {
              name: 'MCP_BEARER_TOKEN'
              secretRef: 'diag-bearer-token'
            }
          ]
          volumeMounts: [
            {
              volumeName: sharedVolumeName
              mountPath: '/tmp'
            }
          ]
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
        }
      ]
      volumes: [
        {
          name: sharedVolumeName
          storageType: 'EmptyDir'
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
      }
    }
  }
}

output containerAppFqdn string = functionApp.properties.configuration.ingress.fqdn
output containerAppName string = functionApp.name
output ingressContainer string = ingressTarget
output diagMcpEndpoint string = ingressTarget == 'diag'
  ? 'https://${functionApp.properties.configuration.ingress.fqdn}/mcp'
  : 'Run `az containerapp exec -n ${functionApp.name} -g ${resourceGroup().name} --container diag -- curl http://localhost:${diagPort}/health` to reach the diag endpoint from inside the app.'
