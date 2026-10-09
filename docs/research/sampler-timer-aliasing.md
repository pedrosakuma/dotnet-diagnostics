# Sampled-profile skew: timer aliasing or short-region undersampling? (issue #1078)

**Verdict (advisory, no product change):** the reported *direction-holding skew between two
equal dominant paths* was **not reproduced**. A different, real effect **was** measured: a
minority path made of very short calls is heavily undersampled, and the undersampling depends
on the length of each call, not on total work. Treat sampled `collect_sample(kind="cpu")`
counts as reliable for *which* path dominates and unreliable for *what a minor, short-call
path costs*.

## What the sampler actually configures (proven by reading the code)

`EventPipeCpuSampler.CollectTraceAsync` enables exactly two providers:
`Microsoft-DotNETCore-SampleProfiler` at `Informational` with **default keywords and no
arguments**, plus `Microsoft-Windows-DotNETRuntime` (`Default` keywords) for rundown/symbols.
There is no rate, jitter, or alignment parameter in the request, so the interval is entirely
the runtime's. The tool text's "~1 kHz" is the runtime's nominal 1 ms interval, not the
delivered rate.

## Experiment

`samples/SamplingAliasProbe` (not part of the solution): one thread loops over `PathA`,
`PathB` (byte-identical work) and `PathM` (~1% of wall time). `Stopwatch` ticks per path are
the wall-clock truth. `run-matrix.py` launches the probe, captures with
`dotnet-diagnostics collect --kind cpu --duration 12 --top 100`, and records inclusive samples
per path. The loop *chunk* (length of each A/B call) is varied from 0.25 ms to 20 ms while
the per-path total work stays constant (~9.4 s each). 7 chunk sizes × 4 captures = 28
captures (raw data: `samples/SamplingAliasProbe/results-1078.jsonl`). Linux, .NET 10,
single busy thread.

## Results (measured)

**Effective rate.** 633–736 samples/s on a busy thread (≈1.4–1.6 ms effective period), in line
with the ~690/s in the issue and well under 1 kHz. Individual captures ranged ~400–700/s.

**A vs B skew** (`(B−A)/(A+B)`, sampled), per chunk size, 4 captures each:

| chunk (ms) | mean skew | sd across captures | binomial SE (n≈6000) |
|---|---|---|---|
| 0.25 | +0.36% | 1.16 | 1.14 |
| 0.5 | −0.44% | 1.30 | 1.09 |
| 1 | −0.03% | 0.88 | 1.07 |
| 2 | +0.11% | 0.33 | 1.13 |
| 5 | +0.06% | 0.70 | 1.13 |
| 10 | −0.15% | 0.55 | 1.10 |
| 20 | +0.35% | 0.62 | 1.05 |

Pooled over 28 captures: mean +0.04%, sd 0.80%, t = 0.24. Signs are mixed, and the spread is
at or below binomial noise. **No significant, direction-holding bias and no dependence on loop
period.** The −5…−8% (and sign flip) from the issue did not reproduce.

**Minority path (PathM, ≈1.0% of wall time by Stopwatch).** Samples observed vs expected from
wall-clock share, summed over the 4 captures per chunk size:

| chunk (ms) | PathM call length | observed | expected | observed/expected |
|---|---|---|---|---|
| 0.25 | ~2.5 µs | 7 | 161 | 4% |
| 0.5 | ~5 µs | 12 | 157 | 8% |
| 1 | ~10 µs | 8 | 153 | 5% |
| 2 | ~20 µs | 4 | 175 | 2% |
| 5 | ~51 µs | 32 | 144 | 22% |
| 10 | ~102 µs | 102 | 171 | 60% |
| 20 | ~204 µs | 125 | 145 | 86% |

Overall 290 observed vs ~1105 expected (26%). The shortfall is large, in one direction, and
shrinks monotonically as each call gets longer while total work is unchanged. Individual
captures of the short-call configurations frequently contain zero PathM samples, matching the
issue's 69/0/0/1 observation.

Also measured: ~5.5% of all samples land outside the three path frames (in `Stopwatch` /
`Thread.PollGC` at path boundaries), i.e. samples are disproportionately attributed to
call/return boundaries rather than inside the tight loop body.

## Proven vs inferred

- **Proven (measured here):** effective rate ≈ 630–740/s; A/B skew is within noise at every
  loop period; minority short-call paths are undersampled by an order of magnitude and the
  deficit depends on call duration.
- **Inferred, not proven:** the mechanism. The pattern (loss concentrated in calls of a few to
  tens of µs, extra samples at call/return boundaries) fits *safepoint/suspension bias* — the
  runtime stops the thread at a GC-safe point or return hijack, so samples cluster at
  boundaries — rather than classic timer aliasing, which would predict skew between the equal
  paths that moves with the period. Confirming it needs runtime-side evidence (sample
  timestamps vs. suspension points), not done here.
- **Not tested:** a deliberately jittered sampler (the provider exposes no jitter control), an
  OS-backed `perf` comparison (`--cpu-backend os`), multi-threaded targets, Windows.

## Why no product change

No cheap, safe mitigation exists in this repo: the provider accepts no rate/jitter argument,
and the confirmed effect is runtime-side. A per-result "confidence" note would need a
statistical model of path length that the sampler does not have. Guidance for readers instead:

- Use sampled counts to rank dominant paths; do not quote a ratio involving a path with few
  samples (or one made of sub-100 µs calls) as measured cost.
- Cross-check a suspected minor path with `--cpu-backend os` (perf/ETW) or a Stopwatch /
  `collect_events` measurement.

Reproduce: `dotnet build samples/SamplingAliasProbe -c Release`, build the CLI, then
`python3 samples/SamplingAliasProbe/run-matrix.py 0.25,1,5,20 4` from the repo root.
