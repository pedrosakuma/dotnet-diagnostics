# dotnet-diagnostics-core

The transport-agnostic **.NET diagnostics engine** behind the
[`dotnet-diagnostics-mcp`](https://github.com/pedrosakuma/dotnet-diagnostics) MCP server and
the `dotnet-diagnostics-cli`. It attaches to a live .NET process over the runtime diagnostic IPC
socket and turns raw EventPipe / ClrMD / TraceEvent streams into structured results. Those normal
paths require no target code changes or prior instrumentation. The explicit exception is
`MethodParameterCaptureUseCases`: it performs a privileged dynamic attach of vendored
dotnet-monitor profilers plus a startup hook and temporarily ReJIT-instruments an allowlist of
methods; hosts must expose that sensitive capability only behind an explicit authorization policy.

This package exists so other hosts can call the same engine **in-process**, without shelling out to
a tool. The first such consumer is the BenchmarkDotNet diagnoser
(`dotnet-diagnostics-benchmarkdotnet`).

```bash
dotnet add package dotnet-diagnostics-core
```

> **Consumer target frameworks:** `net8.0`, `net9.0`, `net10.0`. **Platform:** the engine attaches over the diagnostic IPC socket;
> live ClrMD memory readers (heap/thread snapshots, method bytes, module bytes) additionally need
> `CAP_SYS_PTRACE` on Linux and the same UID as the target. Process dumps write through diagnostic
> IPC and do not require that kernel capability. See the repo docs for the deployment matrix.

## Supported public surface (Pattern B — curated facade)

> ⚠️ **Pre-1.0 / unstable.** While this package is versioned `0.x` it carries **no SemVer API
> stability guarantee**. Only the entry points listed below are *intended* for external use; every
> other public type is an implementation detail that will be internalized incrementally and may
> change or disappear without a major-version bump. Depend on the facade, not on the plumbing.

The supported entry points are the static **use-case** classes; each method returns a
`DiagnosticResult<T>` envelope (success payload, `DiagnosticError`, and `NextActionHint`s):

| Use-case class | What it does |
| --- | --- |
| `ProcessInspectionUseCases` | Discover .NET processes; process / capability / container / runtime-config / triage views. |
| `EventCollectionUseCases` | EventPipe collection: counters, exceptions, GC, GC DATAS, logs, JIT, threadpool, contention, db, activities, event-source, event catalog. |
| `HeapInspectionUseCases` | Live or dump heap walk + drilldown handles. |
| `ProcessDumpUseCases` | Write a process dump (Mini / Triage / WithHeap / Full). |
| `ByteMaterializationUseCases` | Stream module (PE/PDB) or dump bytes. |
| `MethodParameterCaptureUseCases` | Explicit dynamic-profiler capture of allowlisted method parameters on supported CoreCLR targets. |
| `IDiagnosticSession` / `ICounterSessionFactory` | Attach typed callbacks, start, cancel, and stop a bounded live EventCounter session; terminal status reports EventPipe loss and dropped observations. |
| `ComposedDiagnosticSession` | Combine live sessions with finite captures behind one bounded, typed callback dispatcher. |

Supporting types that are part of the facade because the use-cases return or accept them:

- `DiagnosticResult` / `DiagnosticResult<T>`, `DiagnosticError`, `NextActionHint` — the result envelope.
- The per-collector snapshot/result records returned by the use-cases (e.g. `CounterSnapshot`,
  `GcSummary`, `ContentionSnapshot`, …).

Live counter sessions are separate from finite `EventCollectionUseCases` snapshots. Attach one or
more typed handlers before starting; the Core dispatcher invokes matching callbacks serially in
registration order. Event sequence numbers are assigned before bounded queue insertion, so gaps
reveal drops; the queue (maximum capacity 16,384) drops new observations when full and reports the
total in `DiagnosticSessionCompletion`. Session options validate positive process IDs and intervals,
cap provider lists at 64 names of at most 256 characters each, and reject queue capacities outside
the documented bound before opening EventPipe. The live
`CounterSession.DroppedObservations` property exposes the running count. Use `StopAsync`,
`DisposeAsync`, or the cancellation token passed to `StartAsync` to stop and drain the session.

```csharp
await using var session = counterSessions.CreateSession(processId);
using var subscription = session.Attach<CounterObservation>((observation, cancellationToken) =>
{
    Console.WriteLine($"{observation.Sequence}: {observation.Counter.Name}={observation.Counter.Value}");
    return ValueTask.CompletedTask;
});

await session.StartAsync(cancellationToken);
var completion = await session.Completion;
```

`IDiagnosticSession` is the common lifecycle and typed event attachment contract. Event-specific
session factories can publish additional `DiagnosticSessionEvent` record types without changing
existing finite collector APIs. Live counters and the `IStreaming*Collector` interfaces for
exceptions, activities, monitor contention, GC, logs, and JIT publish typed observations incrementally;
other capture families are not yet streamed. Stopping cancels capture
producers; callbacks continue draining the bounded queue. The callback cancellation token is
canceled if handler delivery exceeds the shutdown wait budget.

Use `ComposedDiagnosticSession` when one consumer needs multiple capture families in one lifecycle.
Add live sessions with `AddSession`; add existing finite collector/use-case calls with
`AddCapture`. Each finite operation publishes exactly one
`DiagnosticSessionCaptureResult<TCapture>` event containing its statically typed result. For
windowed collectors that expose observations, use `AddStreamingCapture`: it publishes each typed
observation as a `DiagnosticSessionObservation<TObservation>` and then the same typed terminal
result. Point-in-time operations remain a single result event. Capture operations run concurrently,
and all events share the composed session's bounded queue and monotonic sequence. Collectors with
multiple observation types can use `AddEventCapture` and publish each type as its own
`DiagnosticSessionEvent`.

For example, attach a `CounterObservation` handler and a
`DiagnosticSessionObservation<ManagedExceptionEvent>` handler, add the live counter session with
`AddSession`, and add `IStreamingExceptionCollector.CollectStreamingAsync` with `AddStreamingCapture`. Both
capture families then share one start/stop lifecycle and callback queue; the exception snapshot is
also delivered as a `DiagnosticSessionCaptureResult<ExceptionSnapshot>`.

### Example

```csharp
using DotnetDiagnostics.Core.UseCases;

// Snapshot EventCounters for ~5s from a target PID.
DiagnosticResult<CounterSnapshot> result =
    await EventCollectionUseCases.SnapshotCounters(processId: pid, durationSeconds: 5);

if (result.IsSuccess)
{
    foreach (var hint in result.Hints)
        Console.WriteLine(hint.Message);
}
```

## Not in scope for this package

- The MCP tool surface, HTTP transport and bearer auth live in `dotnet-diagnostics-mcp`.
- The one-shot / REPL command line lives in `dotnet-diagnostics-cli`.

## License

MIT — see the repository.
