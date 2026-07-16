# Azure Container Instances (ACI) recipe

On-demand diagnostics for .NET applications running on **Azure Container
Instances**. The recipe deploys `dotnet-diagnostics-mcp` as a **sidecar
container** in the same ACI container group as your application. The sidecar
attaches to the app via the .NET runtime's diagnostic IPC socket (created in
`/tmp` and shared through an `EmptyDir` volume), so the target app needs **no
code changes**.

| Artifact | Purpose |
|---|---|
| [`main.bicep`](main.bicep) | Bicep template: one container group with two containers (`app` + `diag`) sharing `/tmp`. |

For Azure Container Apps or App Service, see [`../`](../). For AKS or any
Kubernetes cluster, use [`../../k8s/`](../../k8s/).

> ## ⚠️ EventPipe-only on ACI
>
> ACI **does not grant `CAP_SYS_PTRACE`** and does **not** share a PID
> namespace across containers in a group. The socket-based EventPipe tools
> work (they discover the target by parsing the `/tmp` socket filename, which
> the shared `EmptyDir` exposes), but the `ptrace`-backed tools do **not**.
>
> | MCP tool family | Works on ACI? | Notes |
> |---|---|---|
> | Diagnostic IPC / EventPipe — `inspect_process`, `collect_process_dump`, `collect_sample(kind="cpu")`, `collect_events` (counters / GC / exceptions / startup / kestrel / networking), `collect_sample(kind="allocation")` | ✅ Yes | Needs shared `/tmp` + UID match. Dump capture writes through diagnostic IPC; keep the artifact root on the shared volume. |
> | ClrMD / `ptrace` — live `collect_thread_snapshot`, `inspect_heap(source="live")`, live `capture_method_bytes`, `get_bytes(kind="module")`, `collect_sample(kind="cpu", resolveMethodInstantiations=true)` | ❌ No | ACI grants no `CAP_SYS_PTRACE` and no shared PID namespace. |
> | `perf` off-CPU — `collect_sample(kind="off_cpu")` | ❌ No | No `CAP_PERFMON` / host kernel access. |
>
> If you need a live full heap walk or thread snapshot, use the
> [AKS recipe](../../k8s/) (`securityContext.capabilities.add: [SYS_PTRACE]`)
> or the [ECS/EC2 recipe](../../aws/ecs-ec2/) instead.

---

## Prerequisites

1. **Azure CLI 2.60+** and **Bicep 0.30+**:
   ```bash
   az --version
   az bicep version
   az bicep upgrade   # if behind
   ```
2. **A resource group** in the target subscription:
   ```bash
   az group create -n diag-rg -l eastus
   ```
3. **A subnet delegated to ACI** (for the recommended `Private` IP type). The
   subnet must carry a delegation to
   `Microsoft.ContainerInstance/containerGroups`:
   ```bash
   az network vnet subnet create \
     --resource-group diag-rg --vnet-name diag-vnet --name aci-subnet \
     --address-prefixes 10.0.1.0/24 \
     --delegations Microsoft.ContainerInstance/containerGroups
   ```
   Pass its resource ID as `subnetId`. (Set `ipAddressType=Public` only if you
   accept an internet-facing diagnostic endpoint — discouraged.)
4. **Container images reachable by ACI** — your app image plus the diagnostics
   sidecar image (default `ghcr.io/pedrosakuma/dotnet-diagnostics:0.17.0`;
   pass `registryServer / registryUsername / registryPassword` for private
   registries such as ACR).
5. **A bearer token** for the MCP HTTP transport:
   ```bash
   export DIAG_TOKEN=$(openssl rand -hex 32)
   ```
   It is injected as a **secure** environment variable (`MCP_BEARER_TOKEN`).

## UID matching (read this first)

Both containers must agree on the UID that creates and reads
`/tmp/dotnet-diagnostic-<pid>-<unique>-socket`. The default
`dotnet-diagnostics-mcp` image runs as UID **10001**. Most Microsoft
`mcr.microsoft.com/dotnet/aspnet:*` images run as **root**. ACI exposes **no
per-container `runAsUser`**, so the alignment must happen at **image-build
time**:

- **Easiest**: rebuild the diag image with `USER root` (drop the non-root user
  from your fork of `deploy/Dockerfile`).
- **Most secure**: rebuild your app image with `USER 10001` and make `/tmp`
  writable by that UID.

If the UIDs do not match you'll see `Permission denied` when the diag
container opens the diagnostic socket. See `AGENTS.md` → "Diagnostic socket
UID".

## Validate without deploying (zero cost)

```bash
# Compile to ARM JSON (errors out on schema issues).
az bicep build --file deploy/azure/container-instances/main.bicep

# Full dry-run against the API (requires login, creates nothing):
az deployment group validate \
  --resource-group diag-rg \
  --template-file deploy/azure/container-instances/main.bicep \
  --parameters \
      name=diag-demo \
      appImage=mcr.microsoft.com/dotnet/samples:aspnetapp \
      diagBearerToken=$DIAG_TOKEN \
      subnetId=$SUBNET_ID
```

## Deploy

```bash
az deployment group create \
  --resource-group diag-rg \
  --template-file deploy/azure/container-instances/main.bicep \
  --parameters \
      name=diag-demo \
      appImage=$APP_IMAGE \
      diagImage=ghcr.io/pedrosakuma/dotnet-diagnostics:0.17.0 \
      diagBearerToken=$DIAG_TOKEN \
      subnetId=$SUBNET_ID
```

## Smoke test

ACI has no `exec` over a shared PID namespace, but `az container exec` opens a
shell **inside the diag container**, which is all you need to confirm the
shared socket:

```bash
az container exec \
  --resource-group diag-rg --name diag-demo \
  --container-name diag --exec-command "/bin/sh"
# inside the diag container:
ls /tmp/dotnet-diagnostic-*-socket
curl -sH "Authorization: Bearer $MCP_BEARER_TOKEN" http://localhost:8787/health
```

If `ls /tmp/dotnet-diagnostic-*` is empty, the UID is mismatched (see above)
or the app has diagnostics disabled (`DOTNET_EnableDiagnostics=0`).

## Connecting an MCP client

With `ipAddressType=Private`, reach the group's private IP from a peered VNet
(VPN, Bastion, or a jump host):

```jsonc
{
  "mcpServers": {
    "azure-aci-diag": {
      "type": "http",
      "url": "http://<container-group-private-ip>:8787/mcp",
      "headers": { "Authorization": "Bearer <DIAG_TOKEN>" }
    }
  }
}
```

Avoid `ipAddressType=Public` for the diagnostic endpoint. If you must, front
it with an NSG that allows only your operator network.

## Cleaning up

```bash
az container delete -n diag-demo -g diag-rg -y
```

## Out of scope

- **`ptrace` / off-CPU tools** — not available on ACI (see the capability
  matrix above). Choose AKS or ECS/EC2 for those.
- **Public-by-default ingress** — the recipe defaults to a private IP.

## Reference

- [`AGENTS.md`](../../../AGENTS.md) — diagnostic socket UID and `CAP_SYS_PTRACE` invariants
- [Issue #394](https://github.com/pedrosakuma/dotnet-diagnostics/issues/394) — Wave C cloud recipes

## Production: pin to a digest

The defaults use a released version tag
(`ghcr.io/pedrosakuma/dotnet-diagnostics:0.17.0`) rather than `:latest`. For
production pin to a **content-addressable digest**:

```bash
docker buildx imagetools inspect \
  ghcr.io/pedrosakuma/dotnet-diagnostics:0.17.0 \
  --format '{{json .Manifest}}' | jq -r .digest
# -> sha256:...
# Use ghcr.io/pedrosakuma/dotnet-diagnostics@sha256:<digest> in your parameters.
```

Mirror the digest into your private registry (ACR) for air-gapped or
pull-quota-limited environments.
