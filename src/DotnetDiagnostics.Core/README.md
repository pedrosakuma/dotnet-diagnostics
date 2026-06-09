# dotnet-diagnostics-core

The transport-agnostic **.NET diagnostics engine** behind the
[`dotnet-diagnostics-mcp`](https://github.com/pedrosakuma/dotnet-diagnostics-mcp) MCP server and
the `dotnet-diagnostics-cli`. It attaches to a live .NET process over the runtime diagnostic IPC
socket — **no modification to the target app** — and turns the raw EventPipe / ClrMD / TraceEvent
streams into structured results.

This package exists so other hosts can call the same engine **in-process**, without shelling out to
a tool. The first such consumer is the BenchmarkDotNet diagnoser
(`dotnet-diagnostics-benchmarkdotnet`).

```bash
dotnet add package dotnet-diagnostics-core
```

> **Target framework:** `net10.0`. **Platform:** the engine attaches over the diagnostic IPC socket;
> ClrMD-backed operations (heap/thread snapshots, dumps) additionally need `CAP_SYS_PTRACE` on Linux
> and the same UID as the target. See the repo docs for the deployment matrix.

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

Supporting types that are part of the facade because the use-cases return or accept them:

- `DiagnosticResult` / `DiagnosticResult<T>`, `DiagnosticError`, `NextActionHint` — the result envelope.
- The per-collector snapshot/result records returned by the use-cases (e.g. `CounterSnapshot`,
  `GcSummary`, `ContentionSnapshot`, …).

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
