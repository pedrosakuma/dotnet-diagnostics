# Unified Ephemeral-Process Capture — Design

**Issue**: #665 · **Date**: 2026-07-21
**Status**: Design only — no production code, no shipping tool-contract changes in
`docs/tool-reference.md` yet. Companion issue #662 (self-contained handle eviction) is a
separate, independent fix and is not re-litigated here.

## Executive summary

#665 is not one feature — the discussion already found the three original "alternatives" are
complementary parts of one workflow: launch → discover → collect → collect-again against a
process that might not survive long enough for the naive sequence. Parts A and B extend existing
tools/parameters, keeping faith with `AGENTS.md`'s "One MCP tool per concept" / 16-tool-cap
guidance. Part C is a deliberate, acknowledged exception: after comparing three shapes (a bolt-on
parameter, an in-tool `kind="batch"` value, and a new dedicated tool — see Part C's "Rejected
alternatives"), the maintainer chose a **new dedicated tool**, explicitly accepting the
tool-budget cost because the alternatives strained the existing per-kind envelope contract more
than a 17th tool costs.

| Part | Extends | New surface |
|---|---|---|
| A. Launch-and-suspend-then-arm | `collect_sample`, `collect_events` | new `launch` parameter object; new gated capability, **stdio/local-dev only** |
| B. Command-line filter on discovery | `inspect_process(view="list")` | new `commandLineContains` string parameter |
| C. Combined multi-kind collection | new tool | `collect_batch` — fans out to the existing per-kind collectors concurrently (not a merged EventPipe session) |

Part B is low-risk and almost free (the data — `DotnetProcess.CommandLine` — already exists).
Part C is medium-risk (new tool, new envelope) but internally low-risk (fans out to unmodified
existing collectors; no merged trace format). Part A is the highest-risk, genuinely new
capability shape (the MCP server executing an arbitrary command) and needs its own config gate
and an explicit statement of where it does and does not apply (it does **not** apply to the K8s
sidecar topology).

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

## Part C — combined multi-kind collection: a dedicated `collect_batch` tool

**Revision note**: an earlier draft of this section proposed an `alsoCollect` parameter bolted
onto `collect_sample`/`collect_events`, and a sibling option considered a `kind="batch"` value
inside those same tools. After comparing three shapes (bolt-on parameter / in-tool `batch` kind /
new dedicated tool), the maintainer chose the **new dedicated tool**, explicitly accepting the
16-tool-cap cost. The two rejected shapes are kept below the recommended design for the record —
see "Rejected alternatives".

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
> shared window against the same resolved process, inside one new tool call, and return one
> envelope carrying every requested kind's already-existing payload shape.**

This eliminates the process-exit race (the entire point of #665) without touching a single
existing collector's internals, without inventing a merged trace format, and without a new
provider-reconciliation engine. True session-merging (one EventPipe session, N providers) is left
as a possible future optimization if profiling shows the fan-out approach's per-session overhead
matters in practice — not needed for the volatility problem this issue targets.

This holds regardless of which of the three shapes owns the fan-out — it is a property of *how*
the collection happens, not of which tool/parameter triggers it.

### Why call-tree-style drilldowns are unaffected by any of the three shapes

Verified directly in code: `CpuSampleQueryDispatcher` (`call-tree`, `top-methods`, `by-module`,
`by-namespace`, `hot-path`, `caller-callee`) renders every one of those views purely from the
already-collected `CpuSampleTraceArtifact` stored in the handle at `collect_sample(kind="cpu")`
time (`src/DotnetDiagnostics.Core/CpuSampling/CpuSampleQueryDispatcher.cs:1-50` — "renders … directly
from the already-collected trace"). None of them re-open the process or depend on any
`collect_events` data, matching #662's premise that these artifacts are self-contained. So batching
kinds together only has to solve the *upfront* race (getting every requested collector armed
before the volatile process exits); it never has to reconcile or merge data across kinds
afterward — each kind's own handle/artifact stays exactly as independent, and exactly as
drill-down-able via `query_snapshot`, as if it had been collected by a lone `collect_sample`/
`collect_events` call.

### Proposed tool: `collect_batch`

New `[McpServerToolType]` tool, naming consistent with the existing `collect_*` family
(`collect_sample`, `collect_events`, `collect_process_dump`, `collect_thread_snapshot`).

```csharp
[McpServerTool(
    Name = "collect_batch",
    Title = "Run several bounded-time collectors in one call against one process",
    Destructive = false,
    ReadOnly = true,
    Idempotent = false,
    UseStructuredContent = true)]
[Description(
    "Runs several collect_sample/collect_events kinds concurrently, against the same resolved " +
    "process, for the same shared duration window, inside a single call — eliminates the " +
    "process-exit race of issuing them as separate calls (short-lived test hosts, CLI batch " +
    "jobs). Each requested entry's response Data has exactly the same shape it would have if " +
    "called directly via collect_sample/collect_events (see docs/tool-reference.md for each " +
    "kind's payload) and gets its own independent query_snapshot-compatible handle.")]
public static async Task<DiagnosticResult<CollectBatchReport>> CollectBatch(
    /* the same collector dependencies CollectSampleTool/CollectEventsTool take */
    IProcessContextResolver resolver,
    IPrincipalAccessor principalAccessor,
    IDiagnosticHandleStore handles,
    /* ... */
    [Description("Operating system process id of the target .NET process. Resolved once and shared " +
        "by every requested entry (auto-selects the lone visible .NET process when omitted).")]
    int? processId = null,
    [Description("Shared duration of the collection window in seconds for every requested entry. " +
        "Must be >= 1. Defaults to 10. Individual entries cannot override this in v1 — call the " +
        "specific tool directly if one kind genuinely needs a different window.")]
    int durationSeconds = 10,
    [Description("Which collectors to run, each naming an existing collect_sample/collect_events " +
        "kind. Between 1 and 4 entries. Duplicate {tool, kind} pairs and kind='method-params' are " +
        "rejected (security-sensitive; stays a single-purpose collect_sample call).")]
    IReadOnlyList<CollectBatchRequest> requests,
    CancellationToken cancellationToken = default)
```

```csharp
/// <param name="Tool">"collect_sample" or "collect_events".</param>
/// <param name="Kind">One of that tool's existing AllowedKinds (validated by reusing
/// CollectSampleTool.AllowedKinds / CollectEventsTool.AllowedKinds directly — no separate list to
/// drift out of sync).</param>
public sealed record CollectBatchRequest(string Tool, string Kind);

/// <param name="ProcessId">The single resolved pid every entry ran against.</param>
/// <param name="DurationSeconds">The shared window every entry used.</param>
/// <param name="Results">One entry per requested {tool, kind}, in request order.</param>
public sealed record CollectBatchReport(
    int ProcessId,
    int DurationSeconds,
    IReadOnlyList<CollectBatchEntryResult> Results);

/// <param name="Tool">Echoes the request's Tool.</param>
/// <param name="Kind">Echoes the request's Kind.</param>
/// <param name="Summary">That entry's own DiagnosticResult&lt;T&gt;.Summary.</param>
/// <param name="Data">That entry's own DiagnosticResult&lt;T&gt;.Data, serialized generically —
/// heterogeneous per-kind payload types (CpuSample, CounterSnapshot, ...) can't share one static
/// C# type, so this is JsonElement rather than a typed field. Precedent for a JsonElement escape
/// hatch already exists in this codebase (DiagnosticToolBaselineComparison.cs uses it for
/// arbitrary metric JSON). Every kind's shape is still fully documented — at
/// docs/tool-reference.md for that kind — it just isn't statically declared on this shared
/// envelope.</param>
/// <param name="Handle">That entry's own IDiagnosticHandleStore handle, if any — pass this to
/// query_snapshot exactly as if the entry's kind had been collected by a standalone call.</param>
/// <param name="HandleExpiresAt">Mirrors DiagnosticResult&lt;T&gt;.HandleExpiresAt for this entry.</param>
/// <param name="Error">Populated instead of Data/Handle when this one entry failed; other entries
/// are unaffected (see partial-failure semantics below).</param>
public sealed record CollectBatchEntryResult(
    string Tool,
    string Kind,
    string Summary,
    System.Text.Json.JsonElement? Data,
    string? Handle,
    DateTimeOffset? HandleExpiresAt,
    DiagnosticError? Error);
```

### Dispatch

1. Validate the request shape first, with no session opened yet: 1–4 entries, no duplicate
   `{tool, kind}` pairs, no `kind="method-params"`, every `{tool, kind}` pair actually exists in
   that tool's own `AllowedKinds` (reuse `DiscriminatorDispatch`'s existing validation helper
   per-entry rather than inventing a second one).
2. **Pre-authorize every entry before starting anything** (this was the correctness gap the
   review caught in the rejected `alsoCollect` draft, and it applies identically here — a new
   tool does not get to skip it). `collect_batch` itself carries the union of every kind's
   possible primary scope via `[RequireAnyScope("read-counters", "eventpipe")]`, exactly like
   `CollectEventsTool` already does
   (`src/DotnetDiagnostics.Mcp/Tools/CollectEventsTool.cs:47-51,84`) — and the dispatcher must
   then loop over every requested entry and re-check that entry's specific required scope via
   `ToolDispatchGuards.RequireScope`, the same primitive `CollectEventsTool` already uses per kind
   at its own dispatch point
   (`src/DotnetDiagnostics.Mcp/Tools/CollectEventsTool.cs:238`). Fail the whole call (no session
   opened for any entry) on the first unauthorized entry — no partial start.
3. Resolve `processId` once (auto-resolve if omitted), shared by every entry — not re-resolved
   per entry, so every entry targets the exact same pid even if the visible-process set changes
   mid-call.
4. `Task.WhenAll` over one call per entry into that entry's *existing*, unmodified
   `CollectSampleTool`/`CollectEventsTool` use-case method (bypassing only the outer
   `[McpServerTool]`/authorization-filter entry point, whose job step 2 already did once for the
   whole batch) — no changes to any individual collector's internals.
5. Project each entry's `DiagnosticResult<T>` into `CollectBatchEntryResult` (`Data` serialized to
   `JsonElement`, everything else copied through).

### Partial-failure semantics (decided here, since this is a new envelope with no legacy
compatibility to preserve — unlike the rejected `alsoCollect` draft, there is no ambiguity to defer)

A `collect_batch` call **never fails outright just because one entry's target exited mid-window**
— that would defeat the entire purpose of the feature (robustness for volatile processes). The
top-level `DiagnosticResult<CollectBatchReport>.Error` stays `null` and `Results` is always
returned once dispatch begins; each entry independently carries its own `Error` when it failed.
The top-level call only fails outright (no `Results` at all) for the pre-flight validation/auth
failures in steps 1–2 above, or if `processId` resolution itself fails (nothing to run any entry
against).

### v1 scope cuts (explicit, to keep the first PR small)

- **No per-entry option overrides** (`topN`, `symbolPath`, `resolveSourceLines`, `depth`,
  provider lists, …) — every entry runs with that kind's own defaults. A caller who needs
  fine-grained per-kind tuning still calls `collect_sample`/`collect_events` directly; they only
  reach for `collect_batch` when the race, not the tuning, is the problem. Revisit if usage shows
  this is too restrictive — the natural extension is a per-entry `options: JsonElement?` escape
  hatch mirrored back into that kind's real typed parameters server-side, but that is real new
  work, not assumed here.
- **No `launch` integration in v1** (Part A). Composing `collect_batch` with launch-and-suspend is
  an obvious future step (arm every requested kind's session before resuming a suspended target,
  covering the full lifetime *and* eliminating the round-trip race in the same call) but multiplies
  Part A's own open questions (which kinds even support arming on a `SuspendedTarget` today — see
  Part A below) across every batch entry. Ship `collect_batch` against already-running processes
  first; revisit launch-composition once Part A's arming-path question is separately resolved.
- **Cap of 4 entries** to bound total concurrent EventPipe/ETW sessions opened against one target
  process in a single call (resource-boundedness discipline, `docs/resource-boundedness.md`).

### Suggested tests

- `collect_batch(requests=[{tool:"collect_sample",kind:"cpu"},{tool:"collect_events",kind:"counters"}])`
  against a live sample process returns both entries populated, each with its own valid,
  independently-`query_snapshot`-able handle where applicable.
- A caller authorized only for `read-counters` requesting a `collect_sample` kind (which needs
  `eventpipe`) is rejected before any session opens; no partial start.
- `requests` containing `kind="method-params"`, a duplicate `{tool,kind}` pair, an unknown kind,
  or more than 4 entries → rejected with a clear validation error, no session opened.
- One entry's target exits mid-window → the surviving entries' results are still returned; the
  exited entry's `Error` is populated, `DiagnosticResult.Error` at the top level stays `null`.
- Each entry's `Data` JSON matches (module a wrapping `JsonElement`) exactly what calling that
  kind directly via `collect_sample`/`collect_events` would have produced — a
  compatibility-style test analogous to `InspectProcessCompatibilityTests`.

### Rejected alternatives (kept for the record)

**`alsoCollect` bolt-on parameter** on `collect_sample`/`collect_events`. Pros: no new tool, no
16-tool-cap cost. Cons: adds a parameter (plus a new response field) to two tools whose parameter
lists are already large (`CollectSampleTool.CollectSample` alone has ~20 parameters); the
cross-tool case still needs a `{tool, kind}` pair inside a tool that isn't conceptually "about"
spec objects; and the "one kind, one populated field" shape both tools' envelopes follow today
has no natural slot for a second, list-shaped result sitting next to it.

**`kind="batch"` value inside the existing tools.** Pros: reuses the `AllowedKinds`/
`DiscriminatorDispatch` machinery verbatim — "batch" is just another kind. Cons, on closer look:
it does **not** actually avoid the structural problem above — `kind="batch"` still needs
`{tool, kind}`-qualified entries to reach across to the other tool's kinds (the whole reason C
existed was `cpu` in `collect_sample` + `counters` in `collect_events`), so it would need to be
implemented **twice** (once in each tool) to be callable from either entry point, and its response
shape ("a list of sub-results in request order") is a wholly different contract from every other
kind's "exactly one populated field," which is precisely the invariant `InspectProcessReport` and
the sampler/events envelopes were built to preserve. A dedicated tool gets to define its own
contract from scratch instead of straining an existing one to fit a shape it wasn't designed for.

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
`processId`; not composed with `collect_batch` in v1 — see Part C's v1 scope cuts):

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

1. `collect_batch`'s partial-failure semantics and per-entry pre-authorization are decided in
   Part C above (not left open) — no further tracking needed for those two.
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
2. **Part C** — new `collect_batch` tool. Self-contained (its own envelope, no legacy
   compatibility surface to preserve), but bumps the tool count to 17 — update the "16-tool cap"
   framing in `AGENTS.md`'s "One MCP tool per concept" section as part of this PR, since the
   maintainer has explicitly accepted that cost for this feature (this is the one deliberate,
   acknowledged exception, not silent drift).
3. **Part A** — `launch` on `collect_sample`/`collect_events`. Needs the open questions above
   resolved, plus the same security-review rigor as the method-parameter-capture design (new
   config flag, explicit stdio-only gate, and the stdio-reachability question) before any
   implementation PR opens.

Each PR should update `docs/tool-reference.md` (parity-tested by
`ToolReferenceDocParityTests`) and — for Part A only — `docs/authorization.md` (new
`process-launch` modifier scope) and `docs/cli-reference.md` if the CLI grows an equivalent
one-shot flag.
