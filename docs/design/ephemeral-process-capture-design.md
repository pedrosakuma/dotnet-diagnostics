# Unified Ephemeral-Process Capture — Design

**Issue**: #665 · **Date**: 2026-07-21
**Status**: Design only — no production code, no shipping tool-contract changes in
`docs/tool-reference.md` yet. Companion issue #662 (self-contained handle eviction) is a
separate, independent fix and is not re-litigated here.

## Executive summary

#665 is not one feature — the discussion already found the three original "alternatives" are
complementary parts of one workflow: launch → discover → collect → collect-again against a
process that might not survive long enough for the naive sequence. None of the three parts
should become a **new MCP tool** (`AGENTS.md` "One MCP tool per concept", 16-tool cap). All three
extend existing tools/parameters:

| Part | Extends | New surface |
|---|---|---|
| A. Launch-and-suspend-then-arm | `collect_sample`, `collect_events` | new `launch` parameter object; new gated capability, **stdio/local-dev only** |
| B. Command-line filter on discovery | `inspect_process(view="list")` | new `commandLineContains` string parameter |
| C. Combined multi-kind collection | `collect_sample`, `collect_events` | new `alsoCollect` parameter (fan-out, not a merged EventPipe session) |

Part B is low-risk and almost free (the data — `DotnetProcess.CommandLine` — already exists).
Part C is medium-risk but decomposes into an easy version (independent concurrent sessions) that
should ship first, deferring true single-session provider-union as a possible future
optimization. Part A is the highest-risk, genuinely new capability shape (the MCP server
executing an arbitrary command) and needs its own scope, config gate, and an explicit statement
of where it does and does not apply (it does **not** apply to the K8s sidecar topology).

## Part B — command-line filter on process discovery (ship first, lowest risk)

### Current state

`IProcessDiscovery.ListProcesses()` already returns `DotnetProcess.CommandLine`
(`src/DotnetDiagnostics.Core/ProcessDiscovery/DotnetProcess.cs:8`,
`LocalProcessDiscovery.cs:170,210`). `inspect_process(view="list")` bypasses the resolver
entirely and just projects the raw list
(`src/DotnetDiagnostics.Mcp/Tools/InspectProcessTool.cs:228-234`,
`DiagnosticToolProcessInspection.cs:23` → `ProcessInspectionUseCases.ListProcesses`). There is no
filtering today — a caller racing to find `testhost.exe` among several candidates gets the full
unfiltered list and must disambiguate client-side under time pressure.

### Proposed change

Add one new optional parameter to `inspect_process`:

```
[Description("view='list' only. Case-insensitive substring filter against DotnetProcess.CommandLine. " +
    "Use to disambiguate among several candidates spawned by a wrapper the caller doesn't control " +
    "(e.g. 'testhost.exe' + a test-assembly name under 'dotnet test'). Ignored by every other view.")]
string? commandLineContains = null
```

- Applied in `ProcessInspectionUseCases.ListProcesses` (or a thin wrapper) via
  `string.Contains(..., StringComparison.OrdinalIgnoreCase)` — no new collector, no new
  dependency.
- Empty result set keeps today's "no attachable .NET processes" summary shape but should note the
  filter was applied (`$"No .NET process found matching commandLineContains='{filter}'."`) so the
  caller can tell "nothing running" apart from "filter typo'd".
- No security implications — this only narrows an already-visible list; no new scope.
- No change to `DotnetProcess`'s other consumers (`view=info`/`capabilities`/etc. never touch this
  parameter).

### Suggested tests

- Filter matches a subset → only matching processes returned, count/preview text reflects the
  filtered count.
- Filter matches nothing → empty list, summary explicitly names the filter.
- Filter omitted → byte-for-byte identical to today's `view=list` output (regression guard, keeps
  `InspectProcessCompatibilityTests`-style envelope equality intact).

## Part C — combined multi-kind collection (ship second)

### Reframing: fan-out, not session-merge

The issue text says "CPU sample + counters in a single request/response" to cut round-trips
against a volatile process. The tempting design is to union provider/keyword lists into **one**
`EventPipeSession` and split the resulting trace into two projections. That is unnecessary and
higher-risk: every existing per-kind collector already opens its **own** independent
`DiagnosticsClient` EventPipe session
(`EventPipeCpuSampler.cs:182-194`, `EventPipeAllocationSampler.cs:95-106`,
`EventPipeCounterCollector.cs:59-106`), and `Microsoft.Diagnostics.NETCore.Client` supports
multiple concurrent sessions against the same target PID. So "combined collection" can be:

> **Run the existing, unmodified per-kind collectors concurrently (`Task.WhenAll`) for the same
> `durationSeconds` window against the same resolved process, inside one tool call, and return one
> envelope carrying every requested kind's already-existing payload shape.**

This eliminates the process-exit race (the entire point of #665) without touching a single
existing collector's internals, without inventing a merged trace format, and without a new
provider-reconciliation engine. True session-merging (one EventPipe session, N providers) is left
as a possible future optimization if profiling shows the fan-out approach's per-session overhead
matters in practice — not needed for the volatility problem this issue targets.

### Proposed shape

Add one new parameter to both `collect_sample` and `collect_events`:

```
[Description("Optional additional kinds to collect concurrently in this same call, against the " +
    "same resolved process, for the same duration window — avoids a second round-trip against a " +
    "process that may not survive it. Each entry names a kind understood by collect_sample or " +
    "collect_events (cross-tool: e.g. collect_sample(kind='cpu', alsoCollect=[{tool:'collect_events', kind:'counters'}]) " +
    "runs a CPU sample and a counters snapshot side by side). Every secondary kind uses this call's " +
    "shared processId/durationSeconds and its own kind-specific defaults for everything else; it " +
    "cannot override per-kind options individually in v1.")]
IReadOnlyList<AlsoCollectSpec>? alsoCollect = null
```

```csharp
public sealed record AlsoCollectSpec(string Tool, string Kind);
```

- `Tool` is `"collect_sample"` or `"collect_events"` (the only two families relevant to #665);
  `Kind` is one of that tool's existing `AllowedKinds`.
- Dispatch: after the primary kind's existing branch resolves `processId` (so `alsoCollect` reuses
  the *same* resolved PID, not a second auto-resolve that could race/pick differently), fan out
  `Task.WhenAll` over the primary collector call plus one call per `alsoCollect` entry, each
  reusing that tool's existing use-case method unchanged.
- Response envelope: the primary kind's field is populated exactly as today (byte-compatible);
  add one new field, `AlsoCollected: IReadOnlyList<NamedCollectResult>?`, each entry carrying
  `{ tool, kind, result: <that tool's existing envelope shape>, handle, handleExpiresAt }` — so
  every secondary result still gets its own `IDiagnosticHandleStore` handle and remains
  drill-down-able via the existing `query_snapshot` for its kind, unchanged.
- **v1 scope guard**: reject (`DiscriminatorDispatch`-style validation failure, not a partial
  200) combinations that don't make sense — `method-params` in `alsoCollect` (security-gated,
  should stay an explicit single-purpose call), duplicate `{tool, kind}` pairs, and an
  `alsoCollect` list longer than a small cap (e.g. 3) to bound total EventPipe session count
  against one target.
- **Authorization for every secondary kind, not just the outer call** (caught in review — this is
  a correctness requirement, not a nice-to-have). The `[RequireScope]`/`[RequireAnyScope]`
  attributes on `CollectSampleTool.CollectSample`/`CollectEventsTool.CollectEvents` are enforced
  once, on the *outer* tool invocation, by the MCP authorization filter
  (`src/DotnetDiagnostics.Mcp/Security/ToolScopeAuthorizationFilter.cs`). Fanning out to a
  secondary tool's use-case method directly (bypassing its own `[McpServerTool]` entry point)
  does **not** re-run that tool's scope check. Concretely, without an explicit fix, a caller
  authorized only for `read-counters` could call
  `collect_events(kind="counters", alsoCollect=[{tool:"collect_sample",kind:"cpu"}])` and get a
  `collect_sample`-class result despite never presenting the `eventpipe` scope
  `collect_sample` normally requires. Before starting *any* session, the dispatcher must
  pre-authorize every `{tool, kind}` pair in `alsoCollect` — including kind-specific modifier
  scopes and config gates where they exist — against the caller's principal, exactly as if each
  were called directly, and fail the whole request (no session opened) on the first
  unauthorized entry.
- **Partial failure semantics** (needs explicit design, flag as an open question in the tracking
  issue before implementation): if the primary kind succeeds but one `alsoCollect` entry fails
  (e.g. the process exited between session starts), does the whole call fail, or does it return
  the primary result plus a per-entry error in `AlsoCollected`? Recommend the latter — a partial
  win is strictly better than today's "second call fails outright" for the volatile-process case
  this issue exists to fix — but this must be confirmed against the `DiagnosticResult<T>`
  envelope conventions before coding.

### Suggested tests

- `collect_sample(kind="cpu", alsoCollect=[{tool:"collect_events",kind:"counters"}])` against a
  live sample process returns both payloads populated, each with its own valid handle.
- `alsoCollect` omitted → byte-for-byte identical to today's single-kind response (regression
  guard).
- `alsoCollect` containing `method-params` → rejected with a clear validation error, no session
  opened.
- Secondary entry's target exits mid-collection → primary result still returned; secondary entry
  reports its own error (pending the partial-failure decision above).

## Part A — launch-and-suspend-then-arm at MCP level (ship last, highest risk)

### Why this is fundamentally different from B and C

B and C only touch **already-running, already-visible** processes — read-only composition of
existing collectors. Part A asks the MCP server to **spawn a new OS process from a command line
supplied by the MCP client**. That is a new capability shape, not a parameter tweak, and needs to
be designed with the same rigor as `docs/design/method-parameter-capture-design.md` gave
`sensitive-parameter-read` (triple gate: primary scope + modifier scope + server config flag).

### Scope of applicability: stdio/local-dev only, not the K8s sidecar

The existing primitive (`SuspendedColdStartLauncher`,
`src/DotnetDiagnostics.Core/Launch/SuspendedColdStartLauncher.cs:1-22`) is explicitly documented
as **CLI-only by design**: *"the MCP server attaches to already-running pids and cannot influence
the target's launch environment."* That constraint is not incidental — in the K8s sidecar
topology (`deploy/k8s/sample-sidecar.yaml`) the MCP server's container shares only the **PID
namespace** with the app container, not its filesystem, working directory, or environment. A
process the sidecar spawns would run with the *sidecar's* filesystem and environment, not the
app's — almost never what the caller wants, and silently misleading if it "works" by accident
(e.g. both images happen to share a base layer).

So Part A should be gated to run only when the MCP server is effectively the process owner and
shares the target's execution environment — concretely: **`--stdio` mode**
(`docs/authorization.md:8-9` already documents stdio as "the MCP client is the process owner; no
bearer ever crosses a network"), i.e. local dev, the same shape `session --launch` already serves
for the standalone CLI. For non-stdio (HTTP/sidecar) transports, the tool should reject the
`launch` parameter outright with an explicit "launch-and-suspend is only supported over --stdio;
the K8s sidecar shares only the target's PID namespace, not its filesystem/environment" error —
do not attempt it there.

**Correction from review**: stdio cannot simply layer a literal modifier-scope check on top of
its existing principal the way the rest of this section originally assumed. `--stdio` always
registers a single synthetic `StdioRootPrincipalAccessor` carrying only the `root` pseudo-scope
(`src/DotnetDiagnostics.Mcp/Program.cs:219-224`), and `HasExplicitScope` — the check every
modifier scope (`sensitive-parameter-read`, `sensitive-heap-read`, …) uses — **deliberately does
not honour `root`**
(`src/DotnetDiagnostics.Mcp/Security/BearerPrincipal.cs:69-78`). Requiring a literal
`process-launch` modifier scope exactly like `sensitive-parameter-read` would therefore make
`launch` **unreachable over its only intended transport** — every stdio call would fail the
modifier check it can never satisfy. (The doc's original citation of `dump-write` as a precedent
for this pattern was also wrong: `dump-write` is a **primary** scope, granted by root like any
other primary scope, not a literal-only modifier — `docs/authorization.md:46`.)

The gate for `launch` therefore cannot reuse the modifier-scope mechanism verbatim. Two options,
to be settled in the dedicated design note referenced in open question 2 below, not silently
picked here:

- **(a)** Treat `Diagnostics:AllowProcessLaunch=true` as sufficient authorization on `--stdio`
  (mirroring how stdio already collapses every primary-scope check to "the local client owns the
  process"), with no separate scope check at all under stdio — the config flag alone is the gate,
  and the *transport* restriction (stdio-only) is what keeps this from being reachable over a
  network. This is simplest but means an operator who flips the flag on a shared stdio-adjacent
  host (unusual, but not impossible) gets no finer-grained control.
- **(b)** Introduce a **new stdio principal variant** (or a config-driven scope override for the
  existing one) that can be denied `process-launch` even though it holds `root` for every primary
  scope — i.e. make `process-launch` a primary scope that participates in root's union
  (`RootScope` today unions every primary scope but explicitly excludes modifier scopes,
  `BearerPrincipal.cs:69-82`) plus a way to *carve it back out* for stdio specifically. More
  consistent with the rest of the scope model but needs new plumbing that doesn't exist today.

Recommend (a) for v1 given stdio is single-tenant/local-dev by construction, revisit only if field
usage shows a need for finer control.

### Proposed shape

Add one new parameter to `collect_sample` and `collect_events` (mutually exclusive with
`processId` and with `alsoCollect`'s auto-resolve — v1 keeps `launch` single-kind only, no
combining with Part C in the first cut):

```csharp
public sealed record LaunchSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null,
    bool ColdStart = true,
    double ConnectTimeoutSeconds = 10,
    bool TerminateAfterCapture = true);
```

```
[Description("Spawn the target instead of attaching to an existing pid, then arm this collector " +
    "before any managed code runs (coldStart=true, default) or immediately after ordinary attach " +
    "(coldStart=false), so a process that would otherwise exit before a second round-trip is fully " +
    "covered for its whole lifetime. Mutually exclusive with processId. --stdio transport only — " +
    "rejected over HTTP/sidecar because the server does not share the target's filesystem/environment " +
    "there. Requires the 'process-launch' scope and Diagnostics:AllowProcessLaunch=true.")]
LaunchSpec? launch = null
```

- Reuses `SuspendedColdStartLauncher.LaunchSuspendedAsync` (`coldStart=true`, matching #446's
  proven pre-attach-event coverage) or plain `ChildProcessLauncher.Launch` (`coldStart=false`,
  cheaper, for callers who don't need pre-attach events and just want the race eliminated) for the
  child-process/reverse-connect plumbing itself — that part needs no new primitive.
- **Correction from review — not every kind can arm on a `SuspendedTarget` today.** The claim that
  every collector can arm against `SuspendedTarget.Client` with no new session-arming logic is
  false as the collectors are shaped today. Only `EventPipeStartupCollector` currently accepts a
  `SuspendedTarget` (`src/DotnetDiagnostics.Core/Startup/EventPipeStartupCollector.cs:75`,
  `IStartupCollector.cs:22`) — it was built for exactly this cold-start shape. `cpu`/`allocation`
  each construct their own `EventPipeCpuSampler`/`EventPipeAllocationSampler` session against a
  plain `DiagnosticsClient(pid)` (`EventPipeCpuSampler.cs:57-76`,
  `EventPipeAllocationSampler.cs:93-106`), and `EventPipeCounterCollector` does the same
  (`EventPipeCounterCollector.cs:104-106`); `off_cpu`/`native-alloc` are OS-native
  perf/ETW-backed, not EventPipe, and have no notion of a diagnostic-port connector at all.
  **v1 must restrict `launch` to the kinds that actually have (or get) a `SuspendedTarget`-capable
  arming path** — starting with `collect_events(kind="startup")`, which already has one — and
  either extend `cpu`/`allocation`/`counters` to accept an already-connected `DiagnosticsClient`
  (a real, if modest, refactor of each collector's entry point) or explicitly exclude them from
  `launch` until that refactor lands. This must be scoped as its own follow-up design item, not
  assumed away.
- **Lifecycle / cleanup** (the one piece with no existing precedent — MCP tool calls are
  stateless per-request; there is no session object to own cleanup the way
  `SessionRepl`/`session --launch` does): `TerminateAfterCapture` defaults to `true` — the server
  disposes the `SuspendedTarget`/`LaunchedTarget` (which kills the child) once the collection
  window ends, matching "server stays stateless" (`AGENTS.md` non-goals).
  **Correction from review**: `TerminateAfterCapture: false` cannot simply "leave the process
  running" the way this originally read — disposal is the *only* teardown operation either
  wrapper exposes (`LaunchedTarget` kills the child on dispose;
  `SuspendedTarget.DisposeAsync` additionally tears down the connector + deletes the reverse-connect
  socket, `src/DotnetDiagnostics.Core/Launch/SuspendedTarget.cs:53-65`). Neither offers a "detach
  without killing" operation today. Shipping `TerminateAfterCapture: false` in v1 would require a
  new release/detach method on both wrappers that stops tracking the child without sending it a
  signal — real new code, not a parameter default. **Recommend cutting `TerminateAfterCapture`
  from v1 entirely** (always terminate after capture, matching the volatile/short-lived-process
  scenario this issue is about) and revisiting "leave it running" as a separate follow-up only if
  a concrete use case needs it.
- **Correction from review — stdout must never be inherited.** Because `launch` is stdio-only,
  and `--stdio` reserves the process's own stdout exclusively for JSON-RPC framing
  (`src/DotnetDiagnostics.Mcp/Program.cs` stdio transport wiring), the child **must never** be
  spawned with `consoleSink: null` (which makes `ChildProcessLauncher.Launch` let it inherit the
  parent's console, `src/DotnetDiagnostics.Core/Launch/ChildProcessLauncher.cs`). Any stray write
  to stdout by the launched app (its own logging, an uncaught exception, etc.) would interleave
  with and corrupt the MCP JSON-RPC stream, breaking the entire session, not just this call. The
  `launch` code path must always pass a non-null sink (redirect to server-side logs, a bounded
  in-memory buffer surfaced in the response, or `TextWriter.Null`) — never inherit console.
- **Security gate**, loosely mirroring `sensitive-parameter-read`'s multi-gate shape
  (`docs/authorization.md:67`, `docs/design/method-parameter-capture-design.md:39-90`) but adapted
  per the stdio-reachability correction above:
  1. existing tool-level primary scope (`eventpipe`, unchanged),
  2. server config gate `Diagnostics:AllowProcessLaunch` (env `Diagnostics__AllowProcessLaunch`),
     default `false` — this is the actual gate for stdio callers (see the stdio-reachability
     discussion above; a literal `process-launch` modifier scope, unlike `sensitive-parameter-read`,
     cannot be the *sole* stdio-facing gate because stdio's root-only principal can never satisfy a
     literal modifier check),
  3. transport check rejecting `launch` outright on any non-stdio transport.
  Whether a `process-launch` scope is introduced at all — and if so, how it interacts with stdio
  — is deliberately left to open question 2 below rather than decided here.
- **No new tool**, no new top-level MCP surface — `launch` is just another mutually-exclusive
  input shape on the same two tools, same envelope, same handle/drilldown story downstream (for
  the subset of kinds that end up supporting it — see the arming-path correction above).

### Open questions to resolve before implementation (flag on #665, do not silently decide in the PR)

1. Partial-failure semantics for Part C (above) — and the pre-authorization design for every
   `alsoCollect` entry (also above; this one is a correctness requirement, not just a UX choice,
   so it must be resolved, not deferred, before Part C's implementation PR).
2. Exact error surfaced when `launch` is combined with a non-stdio transport — should this be a
   static `[RequireScope]`-style rejection (visible in `tools/list` metadata) or a runtime check
   like `method-params`' `sensitive-parameter-read` (only visible at call time)? Given transport
   is a server-wide, not per-call, property, a static capability flag advertised in `tools/list`
   (like the orchestrator's config-gated tools) may fit better than a runtime-only check — needs
   its own short design note, mirroring how `discover_azure`/K8s tools are conditionally
   registered. This note must also settle whether a `process-launch` scope exists at all and, if
   so, how it composes with stdio's root-only principal (see the stdio-reachability correction
   above) — do not carry the `sensitive-parameter-read` triple-gate pattern over unmodified.
3. Whether `coldStart=false` (plain attach-after-spawn, no suspend) is worth shipping in v1 at
   all, or whether `coldStart=true` alone already covers the motivating scenario well enough to
   cut scope.
4. Which kinds actually get a `launch`-capable arming path in v1 (see the arming-path correction
   above) — at minimum requires either extending `cpu`/`allocation`/`counters` to accept an
   already-connected `DiagnosticsClient`, or shipping `launch` only for `collect_events(kind=
   "startup")` (the one kind already wired for `SuspendedTarget`) plus whichever others get that
   refactor. Do not assume the full kind list works "for free".
5. `TerminateAfterCapture` is cut from v1 per the lifecycle correction above; if a future need for
   "leave it running" surfaces, it requires a genuine detach-without-kill API on
   `LaunchedTarget`/`SuspendedTarget` that does not exist today — track that as separate follow-up
   work, not a parameter default.

## Suggested implementation sequencing

Consistent with this repo's "decompose-then-parallelise" convention (`AGENTS.md` "Agent workflow
conventions"), the three parts are independent trails (different files, different test surfaces,
no shared schema) and should ship as separate PRs, in this order:

1. **Part B** — `inspect_process(view="list", commandLineContains=...)`. Small, no security
   review needed beyond the standard code-review pass.
2. **Part C** — `alsoCollect` fan-out on `collect_sample`/`collect_events`. Needs the
   partial-failure decision above resolved first; otherwise self-contained.
3. **Part A** — `launch` on `collect_sample`/`collect_events`. Needs the two open questions above
   resolved, plus the same security-review rigor as the method-parameter-capture design (new
   scope, new config flag, explicit stdio-only gate) before any implementation PR opens.

Each PR should update `docs/tool-reference.md` (parity-tested by
`ToolReferenceDocParityTests`) and — for Part A only — `docs/authorization.md` (new
`process-launch` modifier scope) and `docs/cli-reference.md` if the CLI grows an equivalent
one-shot flag.
