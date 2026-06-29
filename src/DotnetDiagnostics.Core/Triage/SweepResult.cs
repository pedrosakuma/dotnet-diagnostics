using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.Exceptions;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.ThreadPool;

namespace DotnetDiagnostics.Core.Triage;

/// <summary>
/// Consolidated initial-triage envelope (issue #447 / Wave B1) produced by
/// <c>collect_events(kind="sweep")</c>. Fans out the five EventPipe-safe collectors
/// (counters + gc + exceptions + threadpool + resource) concurrently in a single round-trip,
/// classifies the result, and returns each sub-snapshot plus its drill-down handle so the LLM
/// can pivot without re-paying the collection cost. Cuts a cold triage from five sequential
/// calls (~25–40s) to one window.
/// </summary>
/// <param name="DurationSeconds">The collection window each collector observed.</param>
/// <param name="Triage">Server-side classification (verdict, severity, evidence) derived from the counter snapshot.</param>
/// <param name="Counters">EventCounter snapshot (CPU, GC, threadpool, allocation rate), or null when collection failed.</param>
/// <param name="Gc">GC start/stop summary, or null when collection failed.</param>
/// <param name="Exceptions">Managed exception stream summary, or null when collection failed.</param>
/// <param name="ThreadPool">ThreadPool worker/IOCP starvation view, or null when collection failed.</param>
/// <param name="Resource">OS-level FD/handle/socket snapshot, or null when collection failed.</param>
/// <param name="Handles">Per-collector drill-down handles (counters/gc/exceptions/threadpool) keyed by kind; query_snapshot uses these to follow up without re-collecting.</param>
/// <param name="Failures">Per-collector failure notes — empty when every collector succeeded.</param>
public sealed record SweepResult(
    int DurationSeconds,
    TriageResult Triage,
    CounterSnapshot? Counters,
    GcSummary? Gc,
    ExceptionSnapshot? Exceptions,
    ThreadPoolEventSnapshot? ThreadPool,
    ProcessResources? Resource,
    IReadOnlyDictionary<string, string?> Handles,
    IReadOnlyList<string> Failures);
