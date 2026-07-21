# Changelog

## [Unreleased]

### Added
- **CPU sample per-phase timing breakdown** (#663) — `collect_sample(kind="cpu")` / CLI
  `collect --kind cpu` now return a `timings` object that breaks elapsed wall-clock time into
  capture, symbolication, SourceLink/source-line resolution, aggregation, and total duration
  buckets (plus session start/drain and optional closed-generic enrichment timings).

## [0.18.0] — 2026-07-20

Highlights: **`dotnet-diagnostics-cli session` REPL ergonomics + a session-integrity fix.**
The stateful `session` REPL gains command history (Up/Down recall) and IntelliSense-style
multi-candidate tab-completion, bringing it to functional parity with one-shot commands for
everyday interactive use. Separately, `session --launch` now locks its target pid for the
whole session lifetime, closing a gap where an operator could accidentally retarget away
from the process the session itself launched (and owns the lifecycle of).

### Added
- **`session` REPL command history and tab-completion** (#657, #658) — the CLI's
  `session` REPL now keeps an in-process command history (Up/Down arrow recall) and
  offers IntelliSense-style multi-candidate tab-completion for command names, `--pid`,
  and other option values, matching one-shot command parity.

### Changed
- **`session --launch` locks its target for the session lifetime** (#659, #660) — once a
  session launches its own target process, `target <pid>`/`target clear` and any explicit
  `--pid`/`-p` override that names a different process are rejected for the rest of that
  session; the launched process's pid is fixed, preventing accidental retargeting away from
  the process the session itself started (and is responsible for cleaning up on exit).
- **Evidence-backed triage (#622)** — `inspect_process(view="triage")`, CLI `inspect --view
  triage`, and sweep triage now separate threshold-backed `observedSignals` from bounded
  `hypotheses` with confidence, supporting/contradicting evidence, and neutral next steps.
  `verdict` / `secondaryVerdicts` remain serialized as deprecated compatibility fields until
  v1.0. Counter-only triage no longer emits `io-bound`; low CPU plus a small queue is
  `inconclusive` unless additional latency evidence supports a waiting/backpressure hypothesis.

## [0.17.0] — 2026-07-07

Highlights: **Two major additive waves.** (1) A new **findings / signal-grouping diagnostic
layer** that turns raw collector output (exceptions, allocations/GC, thread wait-state,
counter trends) into ranked, cross-signal-correlated groupings — a neutral vector layer, not
an opinionated "diagnosis". (2) **Method-parameter live capture**
(`collect_sample(kind="method-params")`) — the first vendored native-binary payload shipped
by this repo (dotnet-monitor's ICorProfiler-based profilers), letting an LLM capture real
parameter values from a live, unmodified .NET process for an explicit, scope-gated,
time-boxed window. Both waves are fully additive: **no new MCP tools** (16-tool cap held,
`method-params` is a new `kind` on the existing `collect_sample` tool), and every gate is
fail-closed by default.

### Added
- **Findings / signal-grouping layer** (meta, multiple PRs) — neutral, ranked groupings
  layered on top of existing collectors, with cross-signal correlation:
  - Findings/diagnosis layer foundation (#515, #521).
  - Reworked into a neutral signal-grouping ("vector") layer, not an opinionated diagnosis
    (#523, #529).
  - Exception signal groupings: by-type and by-throw-site (#524, #530).
  - Allocation and GC signal groupings (#525, #531).
  - Thread wait-state / wait-target signal groupings (#526, #532).
  - Counter-trend signal grouping (#527, #533).
  - Cross-signal correlation across the above groupings (#528, #534).
- **CPU-sample self-time lead** — `collect_sample(kind="cpu")` now leads with self-time
  (exclusive) in its summary and hint, ahead of inclusive time, for faster hotspot triage
  (#513).
- **Method-parameter live capture** — `collect_sample(kind="method-params")` (design: #556;
  feasibility spike: #547; production implementation: #562/#563; win-x64 follow-up:
  #564/#565):
  - Live-captures rendered parameter values for an explicit allowlist of managed methods (1–10
    method filters) by temporarily attaching the vendored dotnet-monitor notify-only +
    mutating profilers plus a startup hook to an already-running **.NET 8+ CoreCLR** process,
    then listening to the `ParameterCapturing` EventPipe provider — no target modification,
    no restart.
  - Ships **linux-x64 and win-x64** native profiler payloads (from the same, SHA-256-verified
    `dotnet-monitor` 10.0.2 NuGet package); NativeAOT, pre-.NET 8, and Hot-Reload-active
    targets get a structured `NotSupported`/`Conflict` response instead of a partial capture.
  - Defense-in-depth for a genuinely sensitive capability: gated behind the primary
    `eventpipe` scope **plus** a new non-bypassable literal modifier scope
    (`sensitive-parameter-read`, wildcard/root tokens do not auto-grant it), a server-wide
    opt-in flag (`Diagnostics:AllowMethodParameterCapture`, default `false`), and a required
    per-call `includeSensitiveValues=true` acknowledgement — all three checked before any
    profiler attach happens.
  - Bounded by design: 30s max duration, 500 max captured events, object graph depth 2,
    4 KiB stored / 256-char inline-preview value caps. Returns a `method-params-capture`
    live handle (10-minute TTL, evicted on process exit) drillable via
    `query_snapshot(view="summary"|"events")`.
  - MCP-server-only in V1 (not the standalone CLI, not the BenchmarkDotNet diagnoser — see
    `docs/design/method-parameter-capture-design.md` for the full threat model and rationale).
- **Orchestrator investigation-handle routing redesign** — `investigationHandleId`/
  `investigationHandleIds` explicit routing arguments across the relevant tools, with the
  MCP session binder demoted to a compatibility fallback; proxy authorization now derives
  from the validated bearer identity instead of the `Mcp-Session-Id` header (#554/#559).

### Docs
- `docs/tool-reference.md` fixed stale "15 tools" count to 16 (#543).
- `tool-reference.md` fully synced with every documented tool + a parity guardrail test
  (#508).
- CLI docs: documented `--max-depth`/`--frames-to-hash`, dropped an unreachable
  `--stack-rank` completion (#509); documented the signal-grouping layer + a gated-capture
  documentation gap (#535).
- Narrated diagnostic case studies (CLI + MCP) added (#511); signal-grouping postscript +
  cross-signal correlation case study added (#536).
- `docs/design/method-parameter-capture-design.md` — approved design doc for the
  method-parameter capture feature (#556/#558).
- `docs/research/method-parameter-capture.md` — original feasibility research (#547/#553).

### Chore
- CLI sampler and container parity follow-ups (#545).
- Dependency bumps: ModelContextProtocol SDK 1.3.0 → 1.4.0 (#544); `actions/checkout`,
  `actions/cache`, dotnet base images, and a grouped actions-minor-patch bump (#431, #432,
  #433, #435, #510); deploy image pins bumped to 0.16.0 (#507).

## [0.16.0] — 2026-07-02

Highlights: **Two parity waves** bringing the standalone CLI and the BenchmarkDotNet
in-process diagnoser up to conceptual parity with the MCP server's diagnostic surface.
Fully additive: **no new MCP tools** (15-tool cap held), **no breaking changes** — every
item extends an existing CLI command/`kind`/`view` or the bench collector surface, plus
guardrail tests that fail the build if the surface and its docs ever drift apart.

### Added
- **CLI ⇄ MCP parity wave** (meta #485) — the Core-only `dotnet-diagnostics` CLI now covers
  every Core-eligible capability the MCP server exposes:
  - `inspect --view triage|runtime-config` — triage autopilot + AppContext-switch runtime
    config over the CLI (#486).
  - `query --view frame-vars --thread-id` — exception throw-site locals/params drilldown on
    a captured thread snapshot (#487).
  - `investigate` (planner autopilot) + `export-summary` (portable CPU-investigation JSON)
    commands (#488).
  - `--native-aot-map` flag on gated `--capture cpu-sample` for NativeAOT symbolization (#489).
- **Bench parity wave** (meta #496) — the `dotnet-diagnostics-benchmarkdotnet` in-process
  diagnoser grew from 13 to 17 capture kinds and gained a discoverable, type-safe API:
  - `kestrel`, `networking`, and `requests` collect kinds (#497).
  - `gcdump` heap-retention kind — EventPipe-based (no ptrace), reusing the Core
    `InspectGcDump` facade whose CoreCLR-only guard degrades NativeAOT to a friendly
    `NotSupported` entry rather than crashing; the "what survives" complement to
    `allocation`'s "what churns" (#498).
  - **Discoverable capture API** — a public `BenchmarkDiagnosticKind` enum + token catalog
    and a `params`-enum overload on `[DiagnosticKind]`, so kinds are picked from a typed
    enum instead of a free-text string (the string overload is retained for back-compat);
    `DurationSeconds` is now a settable named argument (#499).

### Changed
- **Off-CPU sampling in the bench diagnoser** is documented as intentionally out of scope
  (host `perf`/privilege dependency); feasibility via child-launch/container is tracked as a
  future spike (#501).

### Docs / Tests
- `cli-reference.md` synced with the current CLI surface, backed by a `CliDocParityTests`
  guardrail that fails the build if a new command/kind is left undocumented (#490).
- Bench `README` gained an enum-API usage example and a "Not captured (intentionally out of
  scope)" section (`event_source`, `startup`, `crash-guard`, `sweep`, `off_cpu`,
  `native-alloc`), backed by a `BenchDocParityTests` guardrail asserting enum ⇄ collector ⇄
  docs parity (#500).

## [0.15.0] — 2026-07-02

Highlights: **Phase 15 — deep heap/memory diagnostics, distributed correlation, and
investigation UX**. A large additive wave: new heap-retention and native-memory views,
an async wait-chain analyzer, an investigation autopilot, distributed trace stitching
across pods, multi-issuer OIDC, and OpenTelemetry export — plus a documented NativeAOT
gcdump boundary. All additive: **no new MCP tools** (15-tool cap held), **no breaking
changes** — every item extends an existing `kind`/`view`/hint or is a new deploy/security
surface.

### Added
- **Heap growth diff (live)** — `inspect_heap` retention-aware live heap growth diff to
  surface leaking types between two snapshots (#475).
- **gcroot/object views over dumps** — `query_snapshot(view="gcroot"|"object")` answers
  retention questions over dump-origin heap snapshots (#476).
- **`inspect_heap(source="gcdump")`** — production-safe heap snapshot via gcdump
  (CoreCLR), registered in the shared handle store for parameterized drilldown (#453).
- **ALC leak heap drilldown** — surfaces AssemblyLoadContext leaks (#422); timer-leak and
  native-vs-managed memory split views (#421).
- **`collect_sample` native-alloc sampling on Windows (ETW)** — Phase 15 C1 native
  allocation sampling (#480).
- **`query_snapshot(view="wait-chains")`** — ranked async wait-chain analyzer spanning
  sync monitor locks + async continuations + threadpool starvation, built purely from
  already-captured thread-snapshot data (no new tool/collector) (#479).
- **`query_snapshot` frame-vars** — exception throw-site locals/params (#460).
- **`inspect_process(view="requests-now")`** in-flight ASP.NET Core request enumeration,
  plus an EventPipe-only requests-inflight variant (Started-but-never-Stopped, oldest
  first) that needs no ptrace (#477).
- **`collect_events(kind="sweep")`** — parallel initial-triage multi-collector sweep (#459).
- **crash-guard event collector** (#420).
- **`inspect_process(view="runtime-config")`** now surfaces AppContext switches (#456).
- **`get_bytes(kind="trace")`** — export raw `.nettrace`/`.gcdump` artifacts (#454).
- **Investigation autopilot** — `start_investigation` returns an executable next-step
  recommendation (Phase 15 D1) (#478).
- **doctor/preflight** — target-optional environment self-diagnosis command (#439).
- **Artifact lifecycle** — list/delete + TTL reaper for captured artifacts (#461).
- **Distributed trace correlation** — trace-stitching engine (#440) with orchestrator
  fan-out across pods (#442).
- **Multi-pod counter fan-out** — replica-skew comparison across replicas (#458).
- **Cold-start suspended launch port** — attach at launch before startup runs (Phase 15
  A3) (#457).
- **Per-pid attach concurrency throttle** for ptrace-backed tools (#455).
- **Multi-issuer OIDC** — `MCP_OIDC_PROVIDERS_JSON` array of trusted issuers plus
  managed/workload-identity (IRSA / AKS) recipes (#428).
- **OpenTelemetry export** — stream `InvestigationSummary` to OTel (#427).
- **Native MCP elicitation** for dump approval + Tasks on collectors (#429).
- **Bounded threshold-gated capture** — client-owned, bounded auto-capture (#423).
- **NativeAOT `MethodIdentity`** — name-based method identity from ILC `map.xml` on CPU
  samples (#416).
- **Cloud deploy recipes (Wave C)** — ECS/EC2, ACI, and Functions-on-ACA topologies (#411).
- **CLI + MCP ergonomics** — CLI ergonomics improvements (#417) and MCP tool ergonomics
  metadata (#418).

### Changed
- **NativeAOT gcdump documented as unsupported** — requesting a gcdump on a .NET 10
  NativeAOT target **crashes the process** (upstream runtime SIGSEGV, reproduced by the
  official `dotnet-gcdump` tool). `canCollectGcDump` is now CoreCLR-only, with
  defense-in-depth guards and a friendly `NotSupported` result; use `collect_process_dump`
  instead. Added a NativeAOT leak-hunt investigation playbook (#481, #482).
- `GcDumpOptions.Runtime` added as an **init-only property** (ABI-preserving; no positional
  constructor change).

## [0.14.0] — 2026-06-11

Highlights: **Phase 12 Wave A (ergonomics/quick-wins) + Wave B (collectors)**. Five
new application-semantics collectors and CLI/MCP ergonomics improvements, all additive
(no new tools, no breaking changes) — every item extends an existing `kind`/`view`/hint.

### Added
- **Phase 12 Wave B — `collect_events(kind="networking")`**: curated outbound-network view over the
  stable `System.Net.Http` / `System.Net.NameResolution` / `System.Net.Security` / `System.Net.Sockets`
  EventSources — HTTP request/connection counts + latency tails, HttpClient connection-pool
  **time-in-queue** (the #1 outbound-HTTP saturation signal), DNS, TLS handshake, and socket-connect
  stats. Drill in with `query_snapshot(handle, view="byOperation|queue|tls|dns")` (HTTP grouped by host + path).
- **Phase 12 Wave B — `collect_events(kind="startup")`**: cold-start/loader profiler capturing
  assembly + module loader events and `Microsoft-Extensions-DependencyInjection` build activity.
  Drill in with `query_snapshot(handle, view="assemblies|modules|di|timeline")`. Note: attaching to an
  already-running process misses pre-attach cold-start; true cold-start needs EventPipe enabled at launch.
- **Phase 12 Wave B — `collect_events(kind="kestrel")`**: Kestrel request-pipeline collector with
  connection/request/TLS latency, queue-length timeline, and the live `KestrelServerOptions` config.
  Drill in with `query_snapshot(handle, view="byOperation|queues|tls|config")`.
- **Phase 12 Wave B — CLI shell completion**: `dotnet-diagnostics completion bash|zsh|pwsh` emits
  shell-completion scripts for sub-commands, kinds, and options.
- **Phase 12 Wave A — Smart Auto-Hints** — `collect_events(kind="counters")` now surfaces
  automatic `NextActionHints` based on counter thresholds, reducing triage steps from 6+ to 2-3:
  - `cpu-usage > 70%` → `collect_sample` (CPU hotspot)
  - `threadpool-queue-length > 50` → hints for `kind="threadpool"` (starvation likely)
  - `time-in-gc > 15%` → hints for `kind="gc"` + `inspect_heap` (GC pressure)
  - `alloc-rate > 50 MB/s` + any Gen2 GC activity → hints for `kind="allocation"` (allocation hotspot)
  - `monitor-lock-contention-count > 10` → hints for `kind="contention"` (lock storms)
  - Low CPU (< 30%) + queue buildup (> 10) → `collect_thread_snapshot` + `kind="activities"` (I/O bound)
  - Headlines now include `time-in-gc` and `alloc-rate` counters (was 12, now 14).
- **Phase 12 Wave A — Triage View** — `inspect_process(view="triage")` is a single
  call that collects counters (5s), classifies the workload, and returns actionable hints:
  - Verdicts: `cpu-bound`, `gc-pressure`, `threadpool-starvation`, `lock-contention`, `io-bound`, `healthy`
  - Severity: `Critical`, `Degraded`, `Healthy`
  - Evidence: key counters that drove the classification
  - SecondaryVerdicts: additional issues detected (e.g., gc-pressure + contention)
  - The LLM just follows the first hint — no interpretation needed.
- **Phase 12 Wave A** — `query_snapshot(view="gc-events")` adds a per-generation GCHeapStats +
  pinned-object trend view; CLI ergonomics (progress spinner, one-shot `query` redirect, artifact-path
  disclosure); trimmed unified-tool `[Description]` bloat for token economy.
- `inspect_process(view="runtime-config")` now reports best-effort GC / ThreadPool startup settings, tiered-compilation env overrides, filtered runtime environment variables, and a forward-compatible `appContextSwitches` field. **Security boundary:** `envVars[]` is strictly filtered to `DOTNET_`, `COMPlus_`, `ASPNETCORE_`, and `DOTNET_SYSTEM_` prefixes so secrets like `*_TOKEN` / `*_KEY` outside those prefixes are never exposed.
- `collect_events(kind="contention")` adds a curated CLR lock-contention view over `Microsoft-Windows-DotNETRuntime` with wait-duration percentiles, `query_snapshot(handle, view="summary|byCallSite|byOwner")` drilldown, and a new `/lock-storm?seconds=N&blockers=M` `BadCodeSample` fixture for reproducing monitor storms.
- `query_snapshot(handle, view="async-stalls")` now classifies async-looking thread stacks into `SyncOverAsync`, `ChannelAwait`, `TcsPending`, `SemaphoreAwait`, `Delay`, and `Unknown`, and `samples/BadCodeSample` exposes `/async-stall?bucket=tcs|channel|sync-over-async|semaphore&seconds=N` for live repros.

## [0.5.0] — 2026-05-26

Highlights: Phase 10 application-semantics gaps. Eight new curated views
let an LLM diagnose log storms, JIT cold-start, in-flight HTTP hangs,
ThreadPool starvation, and EF Core / SqlClient N+1 bursts; plus Meter
API support in counters, FD/socket inspection, and a sample diff view.
No breaking changes.

### Added
- `query_snapshot(handle, view="gchandles")` now aggregates the GCHandle table from `inspect_heap` snapshots, grouping public `GCHandleType`-compatible buckets (`Pinned`, `Normal`, `Weak`, `WeakTrackResurrection`, `Dependent`, `AsyncPinned`) with top target types and notes for ClrMD-internal handle kinds.
- `collect_events(kind="counters")` now subscribes to `System.Diagnostics.Metrics`
  meters via the new `meters` / `maxInstrumentTimeSeries` parameters, surfaces
  Meter time series and histogram percentiles in `CounterSnapshot`, and carries
  cap/error notes when Meter cardinality is truncated.
- `inspect_process(view="resources")` now reports FD / handle / socket state: Linux snapshots classify `/proc/<pid>/fd`, aggregate TCP states from `/proc/<pid>/net/tcp{,6}`, parse `Max open files` from `/proc/<pid>/limits`, and can sample a short trend window; Windows returns `GetProcessHandleCount` with a clear partial-support note. `inspect_process(view="capabilities")` now surfaces `CanReadProcFs` / `CanReadHandleCount` so agents can see whether the sidecar can collect those signals before asking.
- `samples/BadCodeSample` gained `/fd-leak` and `/socket-leak` fixtures, plus live/integration coverage and docs for the new unmanaged-resource investigation path.
- `query_snapshot(view="diff")` can now diff `cpu-sample`, `heap-snapshot`, and `allocation-sample` handles against a `baselineHandle`, including per-second normalization for allocation windows.
- `collect_events(kind="logs")` adds a curated `ILogger` view over the `Microsoft-Extensions-Logging` EventSource with per-level counts, per-category rollups, redacted scopes, bounded recent entries, and `query_snapshot(handle, view="summary|byCategory|byLevel|recent|errors")` drilldown.
- `collect_events(kind="jit")` adds a tiered-compilation / ReadyToRun view over `Microsoft-Windows-DotNETRuntime`, reconstructing inclusive JIT time, Tier0 vs Tier1 distribution, R2R hit vs miss-then-jit, ReJIT / OSR counts, and `query_snapshot(handle, view="summary|topMethods|tierDistribution|reJIT")` drilldown.
- `inspect_process(view="requests-now")` now opens a short ASP.NET Core request window, keeps `HttpRequestIn` spans that started without stopping, and enriches each in-flight request with the current thread id plus top stack frames.
- `samples/BadCodeSample` now exposes `/log-spam?count=N&level=warning|error|...` and `/jit-pressure?count=N` so live tests and playbooks can reproduce logging storms and post-deploy cold-start JIT pressure.
- `samples/BadCodeSample` now exposes `/slow-hang?seconds=N` so live tests and playbooks can reproduce a hanging endpoint for `inspect_process(view="requests-now")`.
- `collect_events(kind="threadpool")` adds a deep ThreadPool starvation view over the runtime `ThreadingKeyword`: worker + IOCP timelines, hill-climbing transitions/reasons, best-effort effective min/max settings, and `query_snapshot(handle, view="summary|timeline|hillClimbing|workItemOrigins")` drilldown.
- `samples/BadCodeSample` now exposes `/log-spam?count=N&level=warning|error|...` and `/threadpool-starve?blockers=N` so live tests and playbooks can reproduce warning/error storms and ThreadPool starvation.
- `collect_events(kind="db")` adds a curated EF Core / SqlClient DB view with sanitized command aggregation (`count`, `totalMs`, `maxMs`, `p95Ms`), N+1 detection, SqlClient pool counters, and `query_snapshot(handle, view="summary|byCommand|n+1|connectionPool")` drilldown.
- `samples/BadCodeSample` now exposes `/log-spam?count=N&level=warning|error|...` and `/db-n+1?count=N` so live tests and playbooks can reproduce warning/error storms and N+1 query bursts.

### Fixed
- `deploy/Dockerfile`: removed dev-only `"Urls": "http://127.0.0.1:8787"` from
  shipped `appsettings.json` so `ASPNETCORE_URLS=http://0.0.0.0:8080` (set in
  the image) is no longer overridden. The `docs/local-docker-sidecar.md`
  quickstart now works out-of-the-box without `-e Urls=http://0.0.0.0:8080`.
  Local dev launch is unaffected — `launchSettings.json` profiles still set
  `applicationUrl` explicitly.
- `inspect_process(view=list)` and the ClrMD `PermissionDenied` envelope no
  longer emit `nextTool="get_diagnostic_capabilities"` (removed in the
  tool-surface consolidation); they now correctly point at `inspect_process(view="capabilities")`.

## [0.4.0] — 2026-05-25

Highlights: tool surface consolidation (24 legacy tools → 15
unified discriminator tools, breaking), central K8s orchestrator
(`attach_to_pod` + server-side proxy), Azure discovery
(`discover_azure` for App Service / Container Apps / AKS), AWS ECS &
GCP Cloud Run sidecar recipes, comprehensive security hardening
(OIDC/JWT, per-tool RBAC scopes, supply-chain), and SLSA build
provenance attestations on every release artifact.

GitHub's auto-generated release notes list every PR; the entries below
group the work by theme.

### Breaking
- **Tool surface consolidation (#213)** — Removed 24 deprecated MCP tools that were superseded by 7 unified discriminator tools. The 15-tool consolidated surface is now the only entry point.
  - Removed: `list_dotnet_processes`, `get_process_info`, `get_diagnostic_capabilities`, `get_container_signals`, `get_memory_trend`, `snapshot_counters`, `collect_cpu_sample`, `collect_allocation_sample`, `get_call_tree`, `collect_off_cpu_sample`, `query_off_cpu_snapshot`, `query_collection`, `collect_exceptions`, `collect_gc_events`, `collect_activities`, `collect_event_source`, `inspect_dump`, `inspect_live_heap`, `query_heap_snapshot`, `query_thread_snapshot`, `list_pods`, `list_active_investigations`, `get_module_bytes`, `get_dump_bytes`.
  - Use the corresponding unified tool with the appropriate `kind`/`view`/`source` discriminator (see `docs/tool-reference.md`).
- **Async collection Stage B (#211)** — Removed `runAsJob` flag and retired `get_collection_status` / `cancel_collection` in favor of MCP-native progress + cancellation (#222).
- **Container image (#111)** — `perf` now ships by default; the GHCR tag suffix is inverted to `-lean` for the perf-less variant.

### Added — unified tool surface
- `inspect_process(view=...)` bootstrap consolidation (#209/#218).
- `collect_events(kind=...)` unified EventPipe collectors (#208/#215).
- `collect_sample(kind=cpu|off_cpu|allocation)` unified sample collectors (#210/#221).
- `query_snapshot(handle, view, ...)` unified drilldown verbs (#207/#223).
- `inspect_heap(source=live|dump)` merged heap inspectors (#206/#219).
- `list_orchestrator(kind=pods|investigations)` (#212/#217).
- `get_bytes(kind=module|dump)` merged byte-fetch tools (#205/#216).
- MCP-native progress + cancellation for long-running collectors (#222).
- Shared compatibility scaffolding (#204/#214) and `PodLocalToolSurfaces` single source of truth for tool registration (#220).

### Added — Central K8s orchestrator (#20)
- `list_pods` + Kubernetes client scaffolding (P3a, #143).
- `attach_to_pod` + investigation handles (P3b-1, #146).
- Port-forward proxy at `/proxy/{handle}` (P3b-2, #150).
- Investigation session binding (P3b-3a, #152).
- Server-side proxy intercept for bound MCP sessions (#154).
- `detach_from_pod` + `list_active_investigations` + TTL reaper (P4, #155).
- Orchestrator deployment assets (#156) and kind integration test for attach + proxy round-trip (#157).
- Observability — metrics, audit, OpenTelemetry (#198/#201).
- Session-aware target resolution foundation (#142).

### Added — Cloud discovery & deployment
- **Azure discovery (parent #230)**: `discover_azure` tool contract (#232/#236), ARM client foundation (#231/#235), App Service + Container Apps backends (#233/#237), AKS cluster listing + process-local kubeconfig handle subsystem (#234/#238).
- **AWS** ECS/Fargate sidecar recipe (#22 Phase 1, #141).
- **GCP** Cloud Run sidecar recipe (#22, #161).
- Cross-MCP byte fetch tools (#195).

### Added — Security hardening
- OIDC / JWT auth on MCP HTTP transport (#196/#200).
- B5 per-tool authorization scopes: `BearerTokenRegistry` + scoped middleware (#182/#188), `[RequireScope]` on every `[McpServerTool]` (#183/#190), Helm chart scoped-token wiring (#186/#191), per-call `confirm=true` for `collect_process_dump` (#187/#192), subsumption of orchestrator + diagnostics admin flags into scopes (#193/#185/#194).
- B4 gating: heap secret leakage, event source allowlist, symbol-server SSRF (#165/#179).
- Sandboxed dump and JIT-bytes output paths (#163/#171).
- Hardened default Helm/RBAC/TLS posture (#162/#172).
- Cross-MCP handoff path hints treated as untrusted (#168/#169).
- Supply-chain hardening (#167/#170) and SLSA build provenance attestations on every release artifact (#149/#159).
- Hardened investigation proxy (#164/#180).

### Added — Diagnostics surface
- `collect_activities` tool (#113/#129).
- Thread snapshot deadlock view (#115/#131), threadpool view (#118/#136), async view (#117), unique thread snapshot groups (#130).
- Heap object drilldowns for snapshot queries (#133).
- Heap async view (#117).
- Opt-in closed-generic enrichment in `collect_cpu_sample` (#86/#127).
- Project `MethodIdentity` into allocation call trees (#100/#126).
- Uniform external symbol resolution (#112/#124).
- Accept `SeSystemProfilePrivilege` for off-CPU sampling on Windows (#89/#59/#128).
- Uniform `depth` parameter + managed↔kernel off-CPU stack merge (#41 slice 2c, #82).
- Extended kernel capability matrix (#41/#132).
- Experimental MCP Tasks support for long-running collects (#135).

### Fixed
- NativeAOT Linux `collect_thread_snapshot` (carried from v0.3.1 — eu-stack partial-success handling).
- Process discovery filters stale diagnostic sockets and Linux TID collisions (#110).
- `PerfScriptParser` PID filter cross-OS (#122).
- `collect_cpu_sample` `runAsJob` depth propagation (#121/#123).
- Windows integration test hangs (#120/#125).
- Kind-integration cluster-wide RBAC (#178).
- CI flake mitigations: serialize test assemblies + collect crash dumps (#148), tolerate documented ubuntu host-crash flake via retry wrapper (#189), conditional quarantine for CpuSampler closed-generic flakes (#145/#147/#160), drive allocation pressure in Kind_Allocation compat test (#225), serialize legacy admin-bypass latch tests (#202/#228).

### Docs
- Central K8s topology design (#15/#137), cloud integrations design (#16/#138), central MCP orchestrator design (Phase 1 spike, #139), AWS ECS + GCP Cloud Run recipes design (#140).
- `gh`/`git` shell-escape pitfalls (#119).
- AOT coverage matrix rewrite with OS columns (#97/#109).
- Agent workflow conventions in `AGENTS.md` (#229).

### CI / Chore
- Bumped GitHub Actions to Node 24-based majors (#151/#158).
