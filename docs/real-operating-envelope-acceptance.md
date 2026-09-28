# Real operating-envelope acceptance harness

This is the opt-in live acceptance harness for issue #1041. It measures existing
CoreClrSample workloads against the current ephemeral and SQLite-backed capture
paths; it adds no product tool, collector, retention policy, or performance
target. **No live matrix results are claimed here.**

## Run only by explicit opt-in

Build the repository with its pinned SDK and a working local .NET diagnostic
socket. Choose a persistent output directory with enough free space, then run
only the acceptance test:

```bash
DOTNET_DBG_MCP_OPERATING_ENVELOPE=1 \
DOTNET_DBG_MCP_OPERATING_ENVELOPE_OUTPUT=/var/tmp/dotnet-diagnostics-operating-envelope \
dotnet test tests/DotnetDiagnostics.Core.Tests/DotnetDiagnostics.Core.Tests.csproj \
  -c Release --filter FullyQualifiedName~OperatingEnvelopeAcceptanceTests
```

Without `DOTNET_DBG_MCP_OPERATING_ENVELOPE=1`, xUnit marks the live test skipped;
ordinary CI and the deterministic protocol tests do not start CoreClrSample
processes for this matrix. The output variable is required when opting in. The
harness creates a unique run subdirectory and uses create-only report files; it
does not delete old evidence or reuse a run directory.

The default finite plan has seven populations, three matched pairs per
population, and two trials per pair (42 trials total). Each trial gets a fresh
CoreClrSample process and a new store root. The two-target population starts
two fresh sample processes and captures them concurrently under one fresh store
root. Pair order alternates `ephemeral,durable`, `durable,ephemeral`,
`ephemeral,durable`. For each target, warmup is at most 2 seconds and the
measured capture/request window is 10 seconds. Requests are scheduled at
250-ms intervals, at most 40 per target per window (80 in the two-target
population); missed slots are counted as not offered, not burst-caught-up.
The cell deadline is 90 seconds, the whole-run deadline is two hours, and the
run-output budget is 8 GiB with conservative pair headroom. These are harness
stop bounds, not product limits or service SLOs.

The workloads reuse existing routes:

| Population | Existing workload and capture |
|---|---|
| Idle counters | `/weatherforecast` warmup, then `System.Runtime` counters only |
| CPU + counters | `/cpu-burn?ms=100`, CPU sample and counters concurrently |
| Allocation + counters | `/render?count=64`, allocation sample and counters concurrently |
| Activity + counters | `/activity?delayMs=10`, ActivitySource capture and counters concurrently |
| Queue + thread | `/threadpool/queue?...`, ThreadPool EventPipe capture and a live thread snapshot |
| Mixed collectors | `/cpu-burn`, `/render`, `/activity?collectGc=true`, and `/parse`; existing `sweep` collectors share one capture/store |
| Two targets, shared disk | Two `/cpu-burn` targets captured concurrently to one store root |

The queue/thread population uses the existing live ClrMD thread-snapshot path.
On Linux that may require a sidecar-scoped `CAP_SYS_PTRACE` and matching target
UID; do not weaken host-wide ptrace policy. If that prerequisite is unavailable,
the trial is recorded as failed and the pair is invalid.

## Evidence and accounting

Each run contains an immutable `plan.json` and `run-manifest.json`; each trial
and matched pair has its own JSON result, and each pair has a CSV. A completed
run additionally has `results.csv`, a `results-manifest.json` listing
SHA-256 hashes for the plan, run manifest, per-trial/per-pair reports, pair
CSVs, and result CSV, plus a sidecar hash for the results manifest itself.
Durable package members are independently hashed before and after read-only
querying. A hash mismatch, missing package, or incomplete write remains an
explicit failure; reports are never rewritten to hide it.

Results retain:

- Warmup and measured HTTP request planned/offered/admitted/rejected/unknown/
  not-offered counts, bounded response-header latency samples and percentiles,
  measured-window throughput, and per-target request accounting.
- Target CPU-time deltas and sampled peak working set, diagnostic-process
  CPU/RSS/allocation and GC deltas, and target GC/runtime counters where the
  existing counter capture exposes them. Resource samples are periodic and
  are not hard peak-memory measurements.
- Collector operation elapsed time, a separate drain duration only where the
  collector API exposes one, durable encode/seal time, read-only query open,
  query and close time, queried record counts, package bytes, and package-file
  hashes. Most EventPipe collectors expose a combined operation/stop/drain
  duration; the harness records that combined duration rather than inventing a
  drain split. Evidence hashing and read-only queries run after the target/
  diagnostic resource sampling window and have their own timings.
- Durable `CaptureQuality` offered, accepted, persisted, rejected by stage,
  pending, source-rejected when available, and unknown-tail populations.
  Ephemeral collectors do not expose equivalent producer/persistence counters:
  those fields remain null/unknown (ephemeral persistence is zero), not inferred
  as lossless. `sourceRejected=null` is not zero.
- Collector notes for observed loss, retention/cap hits, failures, cancellation,
  query verification, and target/resource/cleanup outcomes. Failed or unrun
  trials are retained with explicit stop outcomes; there are no retries or
  replacement runs.
- Package hashing reads bounded chunks and checks the absolute cell and run
  deadlines between chunks. Read-only queries check both deadlines before and
  after each synchronous page/snapshot operation. The store reader exposes
  synchronous page APIs without cancellation; a call already executing cannot
  be preempted, so it runs inside an owned trial task that is awaited only to
  the deadline. Deadline checks surround each call; if it remains blocked
  through the bounded settlement window, the run is quarantined.
- Cleanup cancels outstanding work, initiates termination of every target, and
  waits for target termination before it starts the final settlement of owned
  capture/request work and the resource sampler. Target termination and owned
  task settlement share one absolute bounded cleanup deadline. Request
  accounting is snapshotted only after that post-termination settlement
  succeeds. If settlement or cleanup fails, the run writes a `quarantine.json`
  marker identifying the invalid trial and its stop outcome, plus its SHA-256
  sidecar; it publishes no report for that pair and does not write `results.csv`
  or a final results manifest. Treat every artifact under that run directory
  as quarantined; do not interpret partial package files as sealed evidence.

`Pair IsValid` means both mode trials completed the requested collection with
the same declared configuration. It does not mean zero loss, a passing
throughput target, or a product-level capacity guarantee. Collector overload,
loss, and cap outcomes remain measurements and are not filtered out. A failed
collector, incomplete store, changed query hashes, cleanup failure, deadline,
or output-budget stop makes the pair invalid or leaves remaining trials
explicitly not run.

## Interpretation boundary

Use all three repetitions, preserve invalid and stopped outcomes, and report
each denominator and unknown population. The HTTP results describe this fixed,
low-rate request schedule; they do not calibrate general application demand.
The harness records target impact and storage/query costs on the declared host,
but does not infer universal rates, losslessness, or a production SLO. It does
not change collector quotas or persistence durability settings. These artifacts
are prospective acceptance inputs for review under #1041/#1054, not measured
evidence until the gated matrix is deliberately run and independently assessed.
