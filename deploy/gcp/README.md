# GCP deployment recipes

On-demand diagnostics for .NET applications running on GCP-managed container
hosts. Recipes here deploy `dotnet-diagnostics-mcp` as a **sidecar container**
alongside your application; the sidecar attaches to the app via the .NET
runtime's diagnostic IPC socket (created in `/tmp`), so the target app needs
**no code changes**.

| Recipe | Target host | Multi-container model | External MCP ingress? |
|---|---|---|---|
| [`cloud-run/`](cloud-run/) | Cloud Run (fully-managed, gen2) | One service revision, two containers, shared in-memory `/tmp` volume | No by default — `ingress: internal`. |

For Kubernetes on GKE (or any other cluster), use the generic recipes under
[`../k8s/`](../k8s/) instead. For Azure-managed container hosts, see
[`../azure/`](../azure/). For AWS ECS / Fargate, see
[`../aws/`](../aws/).

> **Cloud Run is a reduced-capability host.** gVisor blocks `ptrace`, so
> `collect_thread_snapshot`, `inspect_live_heap`, live-PID `inspect_dump`,
> `collect_process_dump`, and `collect_off_cpu_sample` all return
> `PermissionDenied`. EventPipe-based tools (counters, cpu_sample,
> exceptions, gc, event_source, activities, allocation_sample) work fine.
> See [`cloud-run/README.md`](cloud-run/README.md) for the full capability
> matrix and pick AWS ECS / Fargate or Kubernetes if you need the blocked
> tools.

## What lives here

- [`cloud-run/`](cloud-run/) — multi-container Cloud Run Service recipe
  (`service.yaml` + README with smoke test).

## Future additions

- A **GKE Autopilot** variant is not needed; the generic Kubernetes recipes
  under [`../k8s/`](../k8s/) already cover that surface (Autopilot supports
  `SYS_PTRACE` via the standard `securityContext.capabilities.add` path).
- **Terraform alternate dialect** is deliberately deferred; the initial
  reference is the Knative-style Service YAML applied with
  `gcloud run services replace`, for parity with the Azure Bicep and AWS
  CloudFormation recipes already in this repo.

See [`../../docs/cloud-recipes-design.md`](../../docs/cloud-recipes-design.md)
for the design that drives these recipes.
