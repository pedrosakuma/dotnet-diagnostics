// dotnet-diagnostics-mcp — Azure Container Instances (ACI) recipe.
//
// Deploys a single container group with two co-located containers sharing /tmp via
// an EmptyDir volume: the user application (`app`) and the diagnostics MCP server
// (`diag`). The diag container attaches to the app via the runtime diagnostic IPC
// socket created in the shared /tmp.
//
// IMPORTANT — capability ceiling: ACI does NOT grant CAP_SYS_PTRACE and does not
// share a PID namespace between containers in a group. Socket-based EventPipe tools
// work (they discover the target by parsing the /tmp socket filename), but the
// ptrace-backed tools (collect_thread_snapshot, inspect_heap(source="live"),
// collect_process_dump) do NOT. This is an inherent ACI limitation — use the AKS
// (../../k8s/) or ECS/EC2 (../../aws/ecs-ec2/) recipes when you need a full heap
// walk or thread snapshot. See the README capability matrix.
//
// Validate without deploying:
//   az bicep build --file deploy/azure/container-instances/main.bicep
//   az deployment group validate \
//     --resource-group <rg> \
//     --template-file deploy/azure/container-instances/main.bicep \
//     --parameters name=diag-demo appImage=<your-app-image> diagBearerToken=<token> \
//                  subnetId=/subscriptions/.../subnets/<delegated-subnet>
//
// Smoke after a real deploy:
//   az container exec -n <name> -g <rg> --container-name diag --exec-command "/bin/sh"
//   # inside diag:
//   ls /tmp/dotnet-diagnostic-*  # should show the app's socket

@description('Container group name.')
param name string

@description('Azure region. Defaults to the resource group location.')
param location string = resourceGroup().location

@description('Image for the application container. Must be reachable by ACI.')
param appImage string

@description('Image for the dotnet-diagnostics-mcp sidecar. Defaults to a released version tag (never :latest) so the group is deterministic; for production override with a digest pin (image@sha256:...). See README.')
param diagImage string = 'ghcr.io/pedrosakuma/dotnet-diagnostics:0.14.0'

@description('TCP port the application container listens on.')
param appPort int = 8080

@description('TCP port the diagnostics MCP container listens on.')
param diagPort int = 8787

@description('Bearer token enforced by the diagnostics MCP server. Provide a long random value; rotate on demand. Injected as a secure environment variable.')
@secure()
param diagBearerToken string

@description('IP address exposure for the container group. "Private" requires subnetId (a subnet delegated to Microsoft.ContainerInstance/containerGroups) and keeps the MCP endpoint off the public internet — strongly preferred. "Public" assigns an internet-facing IP and is discouraged for the diagnostic endpoint.')
@allowed([
  'Private'
  'Public'
])
param ipAddressType string = 'Private'

@description('Resource ID of a subnet delegated to Microsoft.ContainerInstance/containerGroups. Required when ipAddressType = "Private".')
param subnetId string = ''

@description('CPU cores reserved for the application container.')
param appCpu int = 1

@description('Memory (GB) reserved for the application container.')
param appMemoryInGB int = 1

@description('CPU cores reserved for the diag container.')
param diagCpu int = 1

@description('Memory (GB) reserved for the diag container.')
param diagMemoryInGB int = 1

@description('Optional container registry login server, e.g. "myregistry.azurecr.io". Leave empty when images are anonymously pullable.')
param registryServer string = ''

@description('Username for `registryServer`. Ignored when registryServer is empty.')
param registryUsername string = ''

@description('Password for `registryServer`. Ignored when registryServer is empty.')
@secure()
param registryPassword string = ''

var hasRegistry = !empty(registryServer)
var isPrivate = ipAddressType == 'Private'
var sharedVolumeName = 'diag-tmp'

resource containerGroup 'Microsoft.ContainerInstance/containerGroups@2023-05-01' = {
  name: name
  location: location
  properties: {
    osType: 'Linux'
    sku: 'Standard'
    restartPolicy: 'Always'
    subnetIds: isPrivate ? [
      {
        id: subnetId
      }
    ] : []
    imageRegistryCredentials: hasRegistry ? [
      {
        server: registryServer
        username: registryUsername
        password: registryPassword
      }
    ] : []
    ipAddress: {
      type: ipAddressType
      ports: [
        {
          protocol: 'TCP'
          port: diagPort
        }
      ]
    }
    volumes: [
      {
        name: sharedVolumeName
        emptyDir: {}
      }
    ]
    containers: [
      {
        name: 'app'
        properties: {
          image: appImage
          // The runtime creates /tmp/dotnet-diagnostic-<pid>-<unique>-socket when
          // DOTNET_EnableDiagnostics != 0 (default). The shared EmptyDir over /tmp
          // exposes that socket to the diag sidecar.
          ports: [
            {
              protocol: 'TCP'
              port: appPort
            }
          ]
          environmentVariables: [
            {
              name: 'DOTNET_EnableDiagnostics'
              value: '1'
            }
          ]
          volumeMounts: [
            {
              name: sharedVolumeName
              mountPath: '/tmp'
            }
          ]
          resources: {
            requests: {
              cpu: appCpu
              memoryInGB: appMemoryInGB
            }
          }
        }
      }
      {
        name: 'diag'
        properties: {
          image: diagImage
          // The diag image's UID must match the app's UID so the socket created by the
          // app is readable. The default dotnet-diagnostics-mcp image runs as 10001; ACI
          // exposes no per-container runAsUser, so alignment must happen at image-build
          // time (rebuild diag with USER root, or app with USER 10001). See README.
          command: [
            'dotnet'
            'DotnetDiagnostics.Mcp.dll'
          ]
          ports: [
            {
              protocol: 'TCP'
              port: diagPort
            }
          ]
          environmentVariables: [
            {
              name: 'ASPNETCORE_URLS'
              value: 'http://0.0.0.0:${diagPort}'
            }
            {
              name: 'MCP_BEARER_TOKEN'
              secureValue: diagBearerToken
            }
          ]
          volumeMounts: [
            {
              name: sharedVolumeName
              mountPath: '/tmp'
            }
          ]
          resources: {
            requests: {
              cpu: diagCpu
              memoryInGB: diagMemoryInGB
            }
          }
        }
      }
    ]
  }
}

output containerGroupName string = containerGroup.name
output ipAddressType string = ipAddressType
output diagEndpoint string = isPrivate
  ? 'Private IP ${containerGroup.properties.ipAddress.ip}:${diagPort} — reach from a peered VNet, or `az container exec -n ${containerGroup.name} -g ${resourceGroup().name} --container-name diag --exec-command "/bin/sh"`.'
  : 'http://${containerGroup.properties.ipAddress.ip}:${diagPort}/mcp (public — discouraged; restrict with an NSG or switch to Private).'
