# ThreadPool sustained backlog versus transient burst

Issue [#1312](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1312)
evaluates whether bounded window-level statistics change a safe ThreadPool
troubleshooting action beyond a single raw counter observation. This is
ScenarioEvaluation evidence only; it does not change Core, CLI, MCP, VS Code, or
BenchmarkDotNet output.

## Verdict

**GO, narrowly scoped.** A bounded series can distinguish "collect waits/stacks
now", "the burst drained", "look elsewhere", and "recapture because quality is
degraded" when the scenario declares its own backlog level and sufficiency
requirements. The action-changing candidates are backlog-window proportion,
longest contiguous duration above the declared level, drained-tail state, and
independent queue/worker movement. Median, p95, and robust slope remain
descriptive or corroborating in this matrix; they did not independently change
an action.

The evidence does not establish starvation unless a retained runtime-observed
adjustment reason independently says `Starvation`, belongs to the same capture,
and falls within the retained window range. The deterministic replay models
that contract synthetically; the live counter path has no runtime reason and
therefore never confirms starvation.

No universal queue or worker threshold is supported.

## Method and bounds

- One non-overlapping bounded time window is the independence unit.
- The repeated-observation harness from #1311 evaluates only registered v1
  methods: `window-median` and `theil-sen-slope`.
- The exact backlog-window count, proportion, and longest contiguous duration
  above the declared level are deterministic scenario aggregates. Wilson
  intervals are not used because the v1 registry does not admit bounded windows
  as independent Wilson trials.
- A queue p95 is accepted only when the collector already supplied one for every
  window. The live EventCounter path supplies one queue value per interval, so
  p95 remains unavailable there rather than being invented.
- Fixtures are capped at 16 cases and 64 windows per case. The underlying
  statistical harness retains its stricter global observation, window, capture,
  repetition, and bootstrap bounds.
- EventPipe loss, bounded-dispatch drops, irregular counter cadence, omitted
  windows, or a truncated representation make the decision evidence unavailable
  and force recapture. Failed evidence is not treated as zero backlog.

## Deterministic matrix

The replay fixture is
`tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/ThreadPoolBacklog/threadpool-backlog-scenarios.v1.json`.

| Case | Queue evidence | Worker evidence | Reason state | Supported action |
|---|---|---|---|---|
| Sustained backlog | Median 22, slope +4 items/s, 6/6 windows above the case level for a contiguous 6 s | Median 6.5, slope +1 thread/s | Synthetic replay of same-capture runtime-observed `Starvation` | Collect waits/stacks now; starvation support comes only from the reason |
| Transient draining burst | Median 15, slope -6 items/s, final two windows below the case level | Workers still rise at +1 thread/s | Synthetic replay of same-capture runtime-observed `Warmup` | Treat the burst as resolved |
| High workers without backlog | Median/slope 0, 0/6 backlog windows | Median 32, slope 0 | Synthetic replay of same-capture runtime-observed `Stabilizing` | Investigate other signals; worker count alone is not backlog |
| Unknown reason | Median 20, slope +4 items/s, 6/6 backlog windows | Median 6.5, slope +1 thread/s | Missing | Collect waits/stacks now, but abstain from a starvation claim |
| Loss/truncation | Numeric shape resembles the sustained case, but 3 observations are lost and retention is truncated | Unavailable for decision | Synthetic runtime reason retained | Recapture; positive-looking retained values do not override degraded quality |
| Healthy control | Median/slope 0, 0/6 backlog windows | Median 4, slope 0 | Missing | Continue observation; no ThreadPool escalation |

The same actions remain when the optional per-window p95 values are removed.
Raising the replay backlog level above every retained value also removes the
sustained action, demonstrating that the level belongs to the scenario protocol
rather than a cross-workload rule.

## Bounded live evidence

`ThreadPoolBacklogLiveEvidenceTests` starts the EventCounter session before
activating the workload, waits through EventPipe startup, retains only
`System.Runtime/threadpool-queue-length` and `threadpool-thread-count`, and
builds at most 64 one-second windows. Fresh BadCodeSample processes exercise:

- an explicitly released blocking workload retained until collector completion;
- a two-second burst with a drained tail;
- a primed 32-worker process after its queue has drained;
- a two-window live retention cap that records omitted counter windows as
  truncation/loss;
- an idle healthy control.

Counter interval metadata defines each live window duration. Timestamp cadence
outside the bounded protocol tolerance is marked truncated and forces recapture
rather than converting delayed or missing ticks into healthy evidence.

The live counter path intentionally retains a missing adjustment reason. A
sustained queue can therefore support collecting waits/stacks while starvation
attribution remains not established. The degraded live run records actual
bounded-window omissions and must abstain.

## Indicator decision

| Indicator | Decision |
|---|---|
| Window median | NO-GO as an action-changing criterion; keep only as descriptive magnitude |
| Collector-provided window p95 | NO-GO as an action-changing or required field; unavailable in the live counter path and did not change the synthetic replay action |
| Proportion of backlog windows | GO; separates persistent occupancy from sparse spikes within the declared protocol |
| Longest contiguous duration above scenario level | GO; prevents sparse or intermittent high windows from triggering immediate escalation |
| Theil-Sen queue slope | NO-GO as an action-changing criterion; retain only as corroborating evidence when the minimum window count is met because a drained burst can correctly have slope zero |
| Worker slope/median | GO only as independent evidence; high/rising workers without queue backlog redirects the investigation |
| Reason state | GO as same-capture retained provenance; it controls causal support, never whether queue growth "proves" starvation |
| Loss/truncation | GO as a mandatory abstention gate |

## Product follow-up boundary

A follow-up may define a minimal Core-owned bounded-window signal plus CLI human
and JSON rendering. MCP must reuse the existing counter evidence path, and VS Code
must continue consuming CLI JSONL. That follow-up must not add a new MCP tool,
automatic capture, starvation verdict, universal threshold, or broad ThreadPool
semantics rewrite.
