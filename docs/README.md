# Documentation

> **Breaking change — `collect_process_dump` now requires `confirm=true` (issue #187).**
> Existing callers that omit `confirm` will receive a structured
> `{ "kind": "confirmation_required", ... }` envelope describing the dump that would
> have been written (`targetPid`, `dumpType`, `outputDirectory`) and **no file is
> written to disk**. Pass `confirm=true` (in addition to holding the existing
> `dump-write` + `ptrace` scopes) to perform the dump. The other ptrace-stack tools
> (`capture_method_bytes`, `inspect_heap(source="live")`, `collect_thread_snapshot`) are
> deliberately unchanged. See [`authorization.md` → per-call confirmation](./authorization.md#per-call-confirmation)
> and [`tool-reference.md` → `collect_process_dump`](./tool-reference.md#collect_process_dump).

The repo ships **three deliverables** on one shared Core capture engine. Start with the track
you're using, then reach for the cross-cutting references.

### Cross-cutting

| File | What it covers |
|---|---|
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
| [`local-docker-sidecar.md`](./local-docker-sidecar.md) | Reproducing the K8s sidecar topology locally with plain Docker (`--pid=container:` + shared `/tmp`) |

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
