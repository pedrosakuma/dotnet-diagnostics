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
> live `collect_thread_snapshot`, `inspect_heap(source="live")`, live
> `capture_method_bytes`, `get_bytes(kind="module")`, and
> `collect_sample(kind="cpu", resolveMethodInstantiations=true)` return
> `PermissionDenied`. Diagnostic-IPC and EventPipe paths — including
> `collect_process_dump` and normal `collect_sample(kind="cpu")` — work without
> kernel ptrace. `collect_sample(kind="off_cpu")` remains unavailable because
> Cloud Run exposes neither host perf access nor `CAP_PERFMON`.
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
