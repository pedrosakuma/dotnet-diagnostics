# Live counter streaming — overhead and latency measurement

Closes the outstanding #1090 acceptance criterion: *"Streaming overhead and
update latency are measured before expanding to other panels."* This is a
one-time manual measurement, not an automated benchmark; re-run it if the
`CounterSession` / `CliStreamingProtocol` hot path changes materially.

## Method

`dotnet-diagnostics-cli stream --protocol jsonl` driven directly over stdio
(bypassing the VS Code extension) against the published `CoreClrSample`
webapi, on Linux (this environment), default providers
(`System.Runtime` + ASP.NET), `intervalSeconds=1`. CPU/RSS sampled from
`/proc/<cli-pid>/stat` and `/proc/<cli-pid>/status` at ~2s cadence; interval
cadence measured by filtering observation frames to a single named counter
(`cpu-usage`) so batched same-tick frames from other counters in the same
provider don't distort the measurement.

## Results

**Startup latency**
- `hello` → `start` → `started` ack: ~0.15s.
- `start` sent → first `observation` frame received: ~1.28s (matches the
  documented ~500ms-1s EventPipe session startup plus the first 1s counter
  interval boundary).

**Steady-state interval cadence** (single counter, requested 1s interval):
- Observed deltas: 0.953s, 0.999s, 0.997s, 1.000s, 1.001s.
- Average 0.990s, max jitter 0.047s — effectively matches the requested
  interval; no meaningful drift observed.

**CPU overhead (CLI process)**
- First ~5s (JIT warmup + session start): transient spike to ~11-14%.
- Steady state after warmup: ~0.5-1.7% of one core, sampled over 15 windows
  across 30s, ~34 counters/interval (~50 observation frames/s at peak
  multi-provider ticks).

**Memory (CLI process RSS)**
- Grows from ~64MB (process start) to ~75MB in the first 2s (startup), then
  ~80-83MB over the following 30s — roughly 100-150KB/s residual growth
  correlated with observation volume, consistent with managed-heap growth
  between gen0 collections rather than runaway retention. Not confirmed
  bounded over multi-hour sessions; worth re-checking if a future panel
  keeps a session open for very long unattended periods.

**Shutdown latency**
- `stop` sent → `terminal` frame received: ~0.12-0.14s in both runs — well
  within the existing 5s `ShutdownWaitBudget`/`TerminalWriteBudget` budgets,
  consistent with the cancellation-latency assertion added to
  `LiveCounterSession_StreamsSequencedUpdates_AndStopsCleanly` in #1096.

## Conclusion

Overhead is low and bounded in steady state; startup/shutdown latencies are
small and well inside existing budget constants. No changes needed before
expanding to additional live/point-in-time signal kinds (see #1090 follow-up
issues). The only open watch-item is long-session RSS growth, which should be
re-measured once a longer-lived panel (hours, not tens of seconds) is built.
