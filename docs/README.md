# Documentation

> **v0.22.0 — Explicit production safety contract, transport hardening, and approval gates** ([CHANGELOG](../CHANGELOG.md#0220--2026-08-01))
>
> - **Non-loopback cleartext HTTP is refused by default.** Configure direct Kestrel TLS with
>   `MCP_TLS_CERTIFICATE_PEM` + `MCP_TLS_PRIVATE_KEY_PEM`, place the server behind a trusted
>   TLS-terminating proxy via `MCP_TRUSTED_PROXY_CIDRS`, or bind to loopback for local
>   development. See [`client-setup.md` → Transport security](./client-setup.md#transport-security-non-loopback).
> - **High-risk operations pause before side effects** and require the caller to retry with the
>   exact server-returned `safetyApproval.requiredAcknowledgement`. Critical operations prefer
>   MCP elicitation. See [`authorization.md` → per-call confirmation](./authorization.md#per-call-confirmation).
> - **CLI callers** must pass `--acknowledge-risk high|critical`; `--explain-risk` inspects
>   without executing. See [`cli-reference.md` → Risk preflight](./cli-reference.md#risk-preflight).
> - The canonical operation matrix with target impact, data exposure, and evidence lifecycle is
>   [`production-safety.md`](./production-safety.md).

## New user onboarding path

1. **Install and bind to loopback** for initial evaluation:
   [`consumer-install.md` → § 1](./consumer-install.md#1-pick-a-distribution)
2. **Run a first low-risk diagnostic** (`inspect_process(view="list")` — no acknowledgement
   required) to confirm connectivity:
   [`consumer-install.md` → § 4](./consumer-install.md#4-first-diagnostic-and-safety-orientation)
3. **Read the safety model** before collecting traces or dumps — operations are classified
   low / moderate / high / critical and high/critical require deliberate approval:
   [`production-safety.md`](./production-safety.md)
4. **Plan evidence retention and disposal** before the first capture — diagnostic data can
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
| [`production-safety.md`](./production-safety.md) | Canonical generated operation/discriminator/modifier safety matrix, production profiles, EventPipe exposure boundary, and evidence retention/access/disposal expectations |
| [`output-examples.md`](./output-examples.md) | **What each capture actually returns** — real, trimmed output per family (counters, gc, exceptions, threadpool, contention, cpu, allocation), stamped per release |
| [`investigation-playbooks.md`](./investigation-playbooks.md) | Step-by-step recipes for common symptoms (slow, leaking, 5xx, slow HTTP, NativeAOT) |
| [`bad-code-scenarios.md`](./bad-code-scenarios.md) | The anti-patterns in `samples/BadCodeSample/` and the investigation flow each one exercises |
| [`case-studies/`](./case-studies/) | **Narrated end-to-end investigations** — each tells the story of one non-obvious failure from misleading symptom → refuted wrong hypothesis → real cause → fix → verification, with the real captures at every step |

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
