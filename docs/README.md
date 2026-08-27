# Documentation

> **v0.24.0 — Native contention/off-CPU diagnosis matures; MCP Tasks moves to the finalized SEP-2663 shape** ([CHANGELOG](../CHANGELOG.md#0240--2026-08-19))
>
> - **MCP SDK bumped `1.4.0` → `2.2.0`; MCP Tasks moved to the finalized SEP-2663 extension** — a
>   breaking wire-format change for clients relying on the old experimental `tasks/get`/`tasks/result`
>   polling shape. Clients on older protocol revisions keep working unaffected.
> - **New native-lock-contention collector, off-CPU-to-syscall attribution, and a CPU
>   microarchitecture-efficiency snapshot** — `collect_sample(kind="native-lock-contention" |
>   "cpu-efficiency")` and syscall-correlated off-CPU sampling.
> - **Cross-collector investigation digests for `collect_batch`** correlate native-lock and
>   off-CPU evidence in one collection window, shared by the CLI and BenchmarkDotNet diagnoser.
> - Still current: non-loopback cleartext HTTP is refused by default (see
>   [`client-setup.md` → Transport security](./client-setup.md#transport-security-non-loopback)),
>   high-risk operations pause for explicit acknowledgement — CLI callers pass
>   `--acknowledge-risk high|critical` (see
>   [`authorization.md` → per-call confirmation](./authorization.md#per-call-confirmation) and
>   [`cli-reference.md` → Risk preflight](./cli-reference.md#safety-preflight)) — and the canonical
>   operation matrix lives in [`production-safety.md`](./production-safety.md).

## New user onboarding path

1. **Install and bind to loopback** for initial evaluation:
   [`consumer-install.md` → § 1](./consumer-install.md#1-pick-a-distribution)
2. **Run a first low-risk diagnostic** (`inspect_process(view="list")` — no acknowledgement
   required) to confirm connectivity:
   [`consumer-install.md` → § 4](./consumer-install.md#4-first-diagnostic-and-safety-orientation)
3. **Read the safety model** before collecting traces or dumps — operations are classified
   low / moderate / high / critical and high/critical require deliberate approval:
   [`production-safety.md`](./production-safety.md)
4. **Make the production go/no-go decision** with explicit topology, transport, identity,
   approval, evidence, smoke, and rollback pass conditions:
   [`production-readiness checklist`](./production-safety.md#production-readiness-checklist)
5. **Plan evidence retention and disposal** before the first capture — diagnostic data can
   contain PII, credentials, and business-sensitive content:
   [`production-safety.md` → Retention, access, and disposal](./production-safety.md#retention-access-and-disposal)

---

The repo ships **three deliverables** on one shared Core capture engine. Start with the track
you're using, then reach for the cross-cutting references.

> **Instrumentation boundary.** Standard EventPipe and ClrMD diagnostics require no target
> code changes or prior instrumentation. MCP-only
> `collect_sample(kind="method-params")` is deliberately different: it is an explicit,
> privileged, security-gated dynamic attach of vendored dotnet-monitor profilers plus a startup hook,
> temporarily instrumenting only the caller's allowlisted methods.

### Cross-cutting

| File | What it covers |
|---|---|
| [`production-safety.md`](./production-safety.md) | Production-readiness go/no-go checklist, canonical generated operation/discriminator/modifier safety matrix, production profiles, EventPipe exposure boundary, and evidence retention/access/disposal expectations |
| [`output-examples.md`](./output-examples.md) | **What each capture actually returns** — real, trimmed output per family (counters, gc, exceptions, threadpool, contention, cpu, allocation), stamped per release |
| [`investigation-playbooks.md`](./investigation-playbooks.md) | Step-by-step recipes for common symptoms (slow, leaking, 5xx, slow HTTP, NativeAOT) |
| [`bad-code-scenarios.md`](./bad-code-scenarios.md) | The anti-patterns in `samples/BadCodeSample/` and the investigation flow each one exercises |
| [`case-studies/`](./case-studies/) | **Narrated end-to-end investigations** — each tells the story of one non-obvious failure from misleading symptom → refuted wrong hypothesis → real cause → fix → verification, with the real captures at every step |
| [`resource-boundedness.md`](./resource-boundedness.md) | Per-collector memory/retention caps for long or high-volume captures — what's bounded, the eviction strategy, and how a cap hit is surfaced in `notes[]` |
| [`hotpaths/`](./hotpaths/README.md) | CPU/allocation profiling of each collector's *own* code (companion to `resource-boundedness.md`, which bounds memory rather than CPU) |
| [`ci-nuget-cache.md`](./ci-nuget-cache.md) | A/B measurement of NuGet cache policy on hosted CI runners (`ci.yml`/`kind-integration.yml`) and the resulting `setup-dotnet` built-in cache decision |
| [`design/`](./design/) | Feature design docs (security/UX/capability-gate tradeoffs) for shipped, higher-risk surfaces — currently method-parameter capture and unified ephemeral-process capture |
| [`research/`](./research/README.md) | Point-in-time spikes and prototypes (feasibility studies, protocol migration assessments, tool-budget measurements) — read the linked issue for current status, since a spike's verdict can be superseded later |

### MCP server (`dotnet-diagnostics-mcp`)

| File | What it covers |
|---|---|
| [`tool-reference.md`](./tool-reference.md) | Every MCP tool: parameters, returns, runtime requirements, examples |
| [`authorization.md`](./authorization.md) | **Bearer scopes** — which scope each tool needs, default policy per transport, token config, and the `confirm=true` gate |
| [`aot-coverage.md`](./aot-coverage.md) | NativeAOT capability matrix and limitations |
| [`consumer-install.md`](./consumer-install.md) | Full install walkthrough (global tool, container, self-contained binary, Linux ptrace) |
| [`client-setup.md`](./client-setup.md) | Connecting to the server from the C# SDK, GUI MCP clients, and `curl` smoke tests |
| [`local-docker-sidecar.md`](./local-docker-sidecar.md) | Reproducing the K8s sidecar topology locally with an anchored Docker PID namespace + shared `/tmp` |
| [`external-investigation-docker.md`](./external-investigation-docker.md) | Kubernetes-style `attach_to_pod`/proxy passthrough for a **Docker** sidecar — a central orchestrator MCP forwards diagnostic calls to an operator-configured external MCP profile, so the client never sees the sidecar's URL or bearer token |
| [`perf-compat-matrix.md`](./perf-compat-matrix.md) | Linux `perf` compatibility matrix for the CPU/off-CPU/native-alloc/native-lock-contention collectors — supported environments, the non-privileged unit coverage, the opt-in live smoke workflow, and the structured perf failure-mode reference |
| [`central-orchestrator-design.md`](./central-orchestrator-design.md) | The central-orchestrator topology (`list_orchestrator`/`attach_to_pod`/`detach_from_pod`) — namespace/workload/pod discovery, ephemeral-container attach, and cross-MCP handoff options |
| [`cross-mcp-byte-fetch-runbook.md`](./cross-mcp-byte-fetch-runbook.md) | Worked example of the `get_bytes` cross-MCP handoff path when sibling MCPs (`dotnet-assembly-mcp`, `dotnet-native-mcp`) can't see the pod-local filesystem and twin sidecars aren't feasible |
| [`handoff-contract.md`](./handoff-contract.md) | The `MethodIdentity` handoff contract between `dotnet-diagnostics-mcp` and the companion `dotnet-assembly-mcp` |
| [`windows-sidecar-service.md`](./windows-sidecar-service.md) | Running the MCP server as a privileged Windows service sidecar (companion to `consumer-install.md`'s dev-workstation Scheduled-Task path) — needed for off-CPU sampling and other elevated captures |
| [`manual-mcp-smoke-test.md`](./manual-mcp-smoke-test.md) | Manual real-client smoke checklist run before cutting a release or after a change touching transport, auth, or protocol negotiation |

### CLI (`dotnet-diagnostics-cli`)

| File | What it covers |
|---|---|
| [`cli-reference.md`](./cli-reference.md) | **Standalone `dotnet-diagnostics-cli`** — install, every command + flags, and the stateful `session` REPL (the human/script counterpart to the MCP server) |

### BenchmarkDotNet diagnoser

| File | What it covers |
|---|---|
| [`../src/DotnetDiagnostics.BenchmarkDotNet/README.md`](../src/DotnetDiagnostics.BenchmarkDotNet/README.md) | The in-process `[DiagnosticKind]` diagnoser — attach Core captures to a `[Benchmark]` |

### Deployment

| Platform | Guide |
|---|---|
| Kubernetes sidecar | [`../deploy/k8s/README.md`](../deploy/k8s/README.md) |
| Helm chart | [`../deploy/helm/README.md`](../deploy/helm/README.md) |
| Azure | [`../deploy/azure/README.md`](../deploy/azure/README.md) |
| AWS | [`../deploy/aws/README.md`](../deploy/aws/README.md) |
| GCP | [`../deploy/gcp/README.md`](../deploy/gcp/README.md) |
