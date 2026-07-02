# Azure Functions on Azure Container Apps recipe

On-demand diagnostics for **Azure Functions** running as a container on the
**Azure Container Apps** environment (the GA, 2025 hosting path). The recipe
deploys your Functions container image as a Container App with a co-located
`dotnet-diagnostics-mcp` **sidecar**, sharing `/tmp` through an `EmptyDir`
volume. The sidecar attaches to the function worker via the .NET runtime's
diagnostic IPC socket, so the function app needs **no code changes**.

This recipe mirrors the [Container Apps recipe](../container-apps/); the only
material differences are the Functions runtime image, the
`AzureWebJobsStorage` / `FUNCTIONS_WORKER_RUNTIME` configuration, and the
**isolated-worker process-discovery nuance** described below.

| Artifact | Purpose |
|---|---|
| [`main.bicep`](main.bicep) | Bicep template: a Container App running the Functions image (`app`) + the diag sidecar, sharing `/tmp`. |

For plain web apps on Container Apps, see [`../container-apps/`](../container-apps/).
For AKS or any Kubernetes cluster, use [`../../k8s/`](../../k8s/).

> **Why a `Microsoft.App/containerApps` resource and not `Microsoft.Web/sites`?**
> The fully-managed *Functions-on-Container-Apps* hosting kind
> (`Microsoft.Web/sites`) does not let you attach an arbitrary sidecar. To run
> the diagnostics sidecar next to the function we deploy the **Functions
> runtime image directly as a Container App**, which is a supported way to host
> Functions and gives us the multi-container template. You own the base image
> (`FROM mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated8.0`).

---

## Targeting the isolated worker (read this first)

The **.NET isolated** model (`FUNCTIONS_WORKER_RUNTIME=dotnet-isolated`) runs
**two** .NET processes inside the `app` container:

1. the **Functions host** (`FunctionsNetHost`) — the platform process, and
2. the **isolated worker** (`dotnet <YourApp>.dll`) — your function code.

Both publish a diagnostic socket in `/tmp`, so `inspect_process(view="list")`
returns **two** processes. Almost always you want the **worker** — it runs your
business logic, allocations, and any hangs. Identify it by the assembly name
in the command line (your `*.dll`), not `FunctionsNetHost`. Pass that PID to
the collectors (`processId`); the diag tools never auto-pick when two .NET
processes are present.

If you use the **in-process** model instead (`FUNCTIONS_WORKER_RUNTIME=dotnet`,
legacy), there is a single process and no disambiguation is needed — but
in-process is out of support for new apps, so this recipe defaults to
`dotnet-isolated`.

## Capability matrix

Identical to the [Container Apps recipe](../container-apps/) — Container Apps
grants no `CAP_SYS_PTRACE`:

| MCP tool family | Works on Functions-on-ACA? | Notes |
|---|---|---|
| EventPipe — `inspect_process`, `collect_sample(kind="cpu")`, `collect_events`, `collect_sample(kind="allocation")` | ✅ Yes | Needs shared `/tmp` + UID match. Target the worker PID. |
| ClrMD / `ptrace` — `collect_thread_snapshot`, `inspect_heap(source="live")`, `collect_process_dump` | ❌ No | Container Apps grants no `CAP_SYS_PTRACE`. Use AKS for these. |
| `perf` off-CPU — `collect_sample(kind="off_cpu")` | ❌ No | No `CAP_PERFMON` / host access. |

## Prerequisites

1. **Azure CLI 2.60+** and **Bicep 0.30+** (`az bicep upgrade` if behind).
2. **A resource group** and an existing **Container Apps managed environment**
   (`environmentId`).
3. **A storage account connection string** for the Functions host
   (`AzureWebJobsStorage`) — required even for a container deployment.
4. **A Functions container image** built from an `azure-functions/dotnet-isolated`
   base, reachable by the environment (pass `registryServer/...` for private
   registries).
5. **The diag sidecar image** (default
   `ghcr.io/pedrosakuma/dotnet-diagnostics:0.15.0`).
6. **A bearer token** for the MCP HTTP transport:
   ```bash
   export DIAG_TOKEN=$(openssl rand -hex 32)
   ```

## UID matching (read this first)

Both containers must agree on the UID that creates and reads
`/tmp/dotnet-diagnostic-<pid>-<unique>-socket`. The default
`dotnet-diagnostics-mcp` image runs as UID **10001**; the `azure-functions`
base images run as **root**. Container Apps exposes **no per-container
`runAsUser`**, so align at **image-build time**:

- **Easiest**: rebuild the diag image with `USER root`.
- **Most secure**: rebuild your Functions image with `USER 10001` and make
  `/tmp` writable by that UID.

If the UIDs do not match you'll see `Permission denied` when the diag
container opens the diagnostic socket. See `AGENTS.md` → "Diagnostic socket
UID".

## Validate without deploying (zero cost)

```bash
az bicep build --file deploy/azure/functions-aca/main.bicep

az deployment group validate \
  --resource-group diag-rg \
  --template-file deploy/azure/functions-aca/main.bicep \
  --parameters \
      name=fn-diag \
      environmentId=$ENV_ID \
      appImage=$FUNCTIONS_IMAGE \
      diagBearerToken=$DIAG_TOKEN \
      azureWebJobsStorage="$STORAGE_CONN"
```

## Deploy

```bash
az deployment group create \
  --resource-group diag-rg \
  --template-file deploy/azure/functions-aca/main.bicep \
  --parameters \
      name=fn-diag \
      environmentId=$ENV_ID \
      appImage=$FUNCTIONS_IMAGE \
      diagImage=ghcr.io/pedrosakuma/dotnet-diagnostics:0.15.0 \
      diagBearerToken=$DIAG_TOKEN \
      azureWebJobsStorage="$STORAGE_CONN"
```

> Keep `minReplicas >= 1`. Functions-on-ACA can scale to zero, which tears down
> the diagnostic socket; a floor of 1 keeps a live target for ad-hoc
> investigations.

## Smoke test

```bash
az containerapp exec \
  --name fn-diag --resource-group diag-rg --container diag \
  --command /bin/sh

# inside the diag container:
ls /tmp/dotnet-diagnostic-*-socket    # expect TWO sockets: host + worker
curl -sH "Authorization: Bearer $MCP_BEARER_TOKEN" http://localhost:8787/health
```

Seeing two sockets is expected and correct for the isolated model — pick the
worker when you connect a client (see "Targeting the isolated worker").

## Connecting an MCP client

With `ingressTarget=diag` + `externalIngress=true`:

```jsonc
{
  "mcpServers": {
    "azure-functions-diag": {
      "type": "http",
      "url": "https://<fqdn-from-bicep-output>/mcp",
      "headers": { "Authorization": "Bearer <DIAG_TOKEN>" }
    }
  }
}
```

When ingress targets `app` (default), the MCP endpoint is internal only — use
`az containerapp exec` to tunnel, or attach a private endpoint.

## Cleaning up

```bash
az containerapp delete -n fn-diag -g diag-rg -y
```

## Out of scope

- **`ptrace` / off-CPU tools** — Container Apps grants no `CAP_SYS_PTRACE`
  (see the capability matrix). Use AKS for a full heap walk / thread snapshot.
- **The managed `Microsoft.Web/sites` Functions-on-ACA kind** — it does not
  support arbitrary sidecars, which is why this recipe deploys the Functions
  image as a Container App directly.

## Reference

- [`AGENTS.md`](../../../AGENTS.md) — diagnostic socket UID and `CAP_SYS_PTRACE` invariants
- [Issue #394](https://github.com/pedrosakuma/dotnet-diagnostics/issues/394) — Wave C cloud recipes

## Production: pin to a digest

The defaults use a released version tag
(`ghcr.io/pedrosakuma/dotnet-diagnostics:0.15.0`) rather than `:latest`. For
production pin to a **content-addressable digest**:

```bash
docker buildx imagetools inspect \
  ghcr.io/pedrosakuma/dotnet-diagnostics:0.15.0 \
  --format '{{json .Manifest}}' | jq -r .digest
# -> sha256:...
# Use ghcr.io/pedrosakuma/dotnet-diagnostics@sha256:<digest> in your parameters.
```

Mirror the digest into your private registry (ACR) for air-gapped or
pull-quota-limited environments.
