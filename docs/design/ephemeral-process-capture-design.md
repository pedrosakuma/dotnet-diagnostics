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
  cheaper, for callers who don't need pre-attach events and just want the race eliminated) — no
  new launch primitive needed, both already exist in Core and are host-neutral.
- Dispatch order for `coldStart=true`: launch suspended → arm the requested collector's EventPipe
  session on `SuspendedTarget.Client` → `ResumeAsync()` → run the collector's existing
  post-session parsing exactly as today. This mirrors the CLI's own (not yet written) MCP-side
  usage of the same primitive — no new session-arming logic, just a new caller.
- **Lifecycle / cleanup** (the one piece with no existing precedent — MCP tool calls are
  stateless per-request; there is no session object to own cleanup the way
  `SessionRepl`/`session --launch` does): `TerminateAfterCapture` defaults to `true` — the server
  disposes the `SuspendedTarget`/`LaunchedTarget` (which kills the child) once the collection
  window ends, matching "server stays stateless" (`AGENTS.md` non-goals). `TerminateAfterCapture:
  false` leaves the process running and returns its `ProcessId` in the response so a caller can
  drive further one-shot tools against it directly by pid — but the server keeps no handle open
  for it (consistent with statelessness); document that the caller is now responsible for the
  process's lifetime.
- **Security gate**, mirroring `sensitive-parameter-read`'s triple-gate pattern
  (`docs/authorization.md:67`, `docs/design/method-parameter-capture-design.md:39-90`):
  1. existing tool-level primary scope (`eventpipe`, unchanged),
  2. new **modifier** scope `process-launch` (literal-membership only — `*`/root does not
     auto-grant it, same as `sensitive-parameter-read`/`dump-write`),
  3. new server config gate `Diagnostics:AllowProcessLaunch` (env `Diagnostics__AllowProcessLaunch`),
     default `false`.
  Even under `--stdio` (where scopes normally degrade to root/`*`), the modifier-scope check must
  still apply literally — matching the existing modifier-scope pattern — since command execution
  is qualitatively riskier than every other stdio-default-permitted operation.
- **No new tool**, no new top-level MCP surface — `launch` is just another mutually-exclusive
  input shape on the same two tools, same envelope, same handle/drilldown story downstream.

### Open questions to resolve before implementation (flag on #665, do not silently decide in the PR)

1. Partial-failure semantics for Part C (above).
2. Exact error surfaced when `launch` is combined with a non-stdio transport — should this be a
   static `[RequireScope]`-style rejection (visible in `tools/list` metadata) or a runtime check
   like `method-params`' `sensitive-parameter-read` (only visible at call time)? Given transport
   is a server-wide, not per-call, property, a static capability flag advertised in `tools/list`
   (like the orchestrator's config-gated tools) may fit better than a runtime-only check — needs
   its own short design note, mirroring how `discover_azure`/K8s tools are conditionally
   registered.
3. Whether `coldStart=false` (plain attach-after-spawn, no suspend) is worth shipping in v1 at
   all, or whether `coldStart=true` alone already covers the motivating scenario well enough to
   cut scope.
4. Whether `launch` should support the same "child dies when caller disconnects" guarantee
   `session --launch` gives the CLI — MCP has no persistent connection/session concept for
   `Destructive=false, ReadOnly=true` collectors, so `TerminateAfterCapture` is the closest
   equivalent; confirm this is acceptable before implementation.

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
