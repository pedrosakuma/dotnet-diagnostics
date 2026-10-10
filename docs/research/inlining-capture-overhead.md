# `captureInlining` overhead and buffer-loss measurement

Issue: [#1076](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1076) (open question: *overhead of
`JitTracing` on large apps, and whether the 256 MB circular buffer needs an inlining-specific cap*).

**Verdict: advisory, one machine, one small app.** On the measured workload the opt-in JitTracing keyword
(`0x1000`, Verbose) cost about +8 % target CPU and about +6 % request latency, produced a ~4.7× larger
`.nettrace`, and lost **zero** events. This does **not** show that a buffer cap is unnecessary for large
applications; that was not tested.

## What changed alongside the experiment

- `EventPipeCpuSampler` now adds a `notes[]` entry when `captureInlining` is on and the trace reports lost
  events (`traceLog.EventsLost > 0`). It names `EventPipeCpuSampler.CircularBufferMB` (256 MB) and the count.
  Captures without loss add nothing. No serialized property was added to any tool summary record, so the
  `ToolCatalogBudgetTests` catalog size is unchanged.
- Live test `CpuSampler_CaptureInlining_AnnotatesSampledInlineeWithItsInliner` asserts that a *sampled*
  `JitInlineFixture.Mix` row carries `inlinedInto` naming `InlineProbeType.InlineProbeCaller`, with
  `inlinedIntoAmbiguous` unset. It fails (does not skip) when no inlining events arrive or the row is never
  sampled before a 90 s deadline, and polls repeated 4 s captures instead of sleeping.

### Fixture finding: why the inlinee needs a loop

The sampler stops a thread only at a GC safepoint. A tiny loop-free leaf method (the original
`AddOne`, or a first `Mix` without a loop) never receives a leaf sample even when it runs millions of times:
the thread is observed in its caller after the return. `Mix` therefore contains a short loop (a back-edge
safepoint). The JIT judges that inline *unprofitable* (`unprofitable inline` in the decision's reason), so `Mix`
carries `AggressiveInlining`; the decision is still a real, traced inlining success. The standalone body is
kept hot by `SpinUnoptimized`, a `MinOpts` caller that invokes `Mix` through a delegate (no inliner can fold it).
This matches the safepoint-bias mechanism inferred in [`sampler-timer-aliasing.md`](./sampler-timer-aliasing.md).
A non-aggressive, loop-free inlinee that is also sampled was **not achievable** deterministically.

## Experiment

**Setup (this machine only):** WSL2 Linux 6.18, 16 logical CPUs (shared with other agents' processes, so
background load was not controlled), .NET SDK 10.0.401 / runtime 10.0.x, `CoreClrSample` published Release,
commit based on `1a0242e`.

**Protocol.** For each round a *fresh* target process was started and warmed with 30 requests, then a single
sequential client looped `GET /jit-inline-probe?spin=0` (`spin` present opts into the `Mix` call; each request defines a fresh collectible
`DynamicMethod` assembly, JITs an optimized caller and takes a traced inlining decision) while
`EventPipeCpuSampler.SampleAsync(duration: 10 s, exportTrace: true)` ran. Rounds alternated
off→on / on→off. Metrics: target CPU time over the window (`Process.TotalProcessorTime` delta), client request
p50/p99, exported `.nettrace` size, whether the new loss note appeared, `InliningProfile.TotalDecisions` /
`UnattributedDecisions`, and total CPU samples. The driver was a ~45-line throw-away console app against
`DotnetDiagnostics.Core`; it is deliberately not in the repo.

**Results** (14 scheduled rounds per mode; per-run medians, min–max in brackets):

| Metric | `captureInlining` off (n=13) | `captureInlining` on (n=11) |
|---|---|---|
| Target CPU over 10 s window | 16.64 s (16.45–17.01) | 17.92 s (17.76–19.51) |
| CPU per request | 2.74 ms (2.66–2.82) | 2.89 ms (2.75–3.28) |
| Requests completed in window | 6132 (5895–6196) | 6195 (5617–6538) |
| Request p50 | 1.42 ms (1.39–1.48) | 1.51 ms (1.45–1.62) |
| Request p99 | 6.65 ms (6.44–6.99) | 7.09 ms (6.55–8.29) |
| `.nettrace` size | 10.6 MB (10.3–10.7) | 49.4 MB (47.8–49.9) |
| Lost events (note emitted) | 0 of 13 | 0 of 11 |
| Inlining decisions per capture | – | 46,067 (44,773–46,693) |
| Unattributed decisions | – | 0 (0–2) |
| CPU samples | 71,426 | 71,694 |

Reading: median CPU +7.7 %, p50 +6 %, p99 +6.6 % (the p99 ranges overlap), trace ≈ 4.7×. Those percentages
are descriptive for this workload; with n≈11–13 and a noisy shared host they are not a confidence interval.
The sampled-CPU-sample count is unchanged, so the keyword did not visibly distort sampling here.

**Target crashes (follow-up, issue #1154).** 4 of 28 capture runs (1 off, 3 on) ended with the *target* exiting
with code 139 (SIGSEGV) and the capture failing with `FormatException: Read past end of stream` on the truncated
trace. The original attribution to the load pattern alone was **not** confirmed: a controlled follow-up
(WSL2, 16 shared CPUs, published `CoreClrSample`, same `/jit-inline-probe?spin=0` full-rate loop, 24 fresh
processes per arm) saw **0/24 crashes with no capture**, 8/24 with a default CPU capture and 4/24 with
`captureInlining` (3 SIGSEGV, 1 SIGABRT `The RX block to map as RW was not found`). `captureInlining` therefore
did not raise the rate over a default CPU capture (8/24 vs 4/24). The same pattern reproduced without ASP.NET
(a console app creating `RunAndCollect` dynamic assemblies in a loop: 0/24 without capture, 8/24 with a CPU
capture). In the `dotnet-trace` arm the target died when the 10 s window ended (the `Stop` command failed), which points at session stop/rundown under ongoing churn. Raw-provider bisection
with `dotnet-trace` (16 runs per arm) crashed only with the SampleProfiler provider plus rundown (3/16); rundown off
or runtime-only providers gave 0/16. The crash site is the same `libcoreclr` frames on a native thread with no managed stack in all 7 SIGSEGV dumps examined (symbols unavailable, so the function is unidentified); this points at a runtime-side defect rather than the sampler or sample, but the root cause is not proven. Not tested: Windows, other runtime patch versions, non-WSL hosts. The live test keeps the 50 ms
inter-request delay and did not crash in the runs observed. These runs are excluded from the table above.

## Limits — what was not measured

- **Windows, macOS and ETW paths:** untested. This is the Linux EventPipe backend only.
- **Large applications / large module graphs:** untested; this app has a small steady set of compiled methods.
  The decision *rate* here is dominated by the per-request dynamic-method JIT; a real app with more jitted
  methods at startup (cold start, tiering churn) can emit far more events in a shorter time.
- **Buffer loss was never provoked.** Zero loss was observed at ~4.6 k decisions/s into a 256 MB buffer. The
  loss path is covered only by the unit test on the note helper, not by an induced overflow, so whether an
  inlining-specific cap or sampling is needed for big apps remains **open**; the new note makes loss visible
  when it happens.
- Other .NET runtime versions, the sample at other request rates, concurrent clients, and server GC vs
  workstation GC were not varied. CPU time includes the in-process EventPipe/sampler work for both modes, so
  the table is an on-vs-off difference, not an absolute cost of tracing.
