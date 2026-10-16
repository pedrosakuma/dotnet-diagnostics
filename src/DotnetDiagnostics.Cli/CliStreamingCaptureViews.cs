using System.Globalization;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Contention;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Db;
using DotnetDiagnostics.Core.Evidence;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Kestrel;
using DotnetDiagnostics.Core.Networking;
using DotnetDiagnostics.Core.OffCpu;
using DotnetDiagnostics.Core.Requests;
using DotnetDiagnostics.Core.ThreadPool;
using DotnetDiagnostics.Core.UseCases;

namespace DotnetDiagnostics.Cli;

internal sealed record CaptureViewMetric(string Label, object? Value, string? Note = null);
internal sealed record CaptureViewTable(string Id, string Title, IReadOnlyList<string> Columns,
    IReadOnlyList<object?[]> Rows, int TotalRows, int OmittedRows, string EmptyText);
internal sealed record CaptureViewProvenance(string Origin, bool Durable, int ProcessId,
    DateTimeOffset? StartedAt, double? ReportedDurationSeconds, int RequestedDurationSeconds,
    string? Runtime, string? RuntimeVersion);
internal sealed record CaptureViewConclusions(string RetainedPositive, string AbsenceOrExhaustive, string HealthyControl);
internal sealed record CaptureViewEvidence(string Source, IReadOnlyList<EvidenceLimitation> Limitations,
    CaptureViewConclusions? Conclusions = null, string? Schema = null);
internal sealed record CaptureView(string Kind, string Title, string Summary, CaptureViewProvenance Provenance,
    IReadOnlyList<CaptureViewMetric> Metrics, IReadOnlyList<CaptureViewTable> Tables, IReadOnlyList<string> Notes,
    CaptureViewEvidence Evidence)
{
    public string? Handle { get; init; }
}

internal sealed class CaptureViewBuilder
{
    private readonly List<CaptureViewMetric> _metrics = [];
    private readonly List<CaptureViewTable> _tables = [];
    private readonly List<string> _notes = [];
    private const int MaxTextLength = 2048;
    private const int MaxNotes = 50;
    private int _truncatedText;
    private int _omittedNotes;

    public void Metric(string label, object? value, string? note = null)
        => _metrics.Add(new(label, Cell(value), note));

    public void Note(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return;
        if (_notes.Count < MaxNotes) _notes.Add((string)Cell(note)!);
        else _omittedNotes++;
    }

    public void Notes(IEnumerable<string>? notes)
    {
        if (notes is not null)
            foreach (var note in notes) Note(note);
    }

    public void Table<T>(string id, string title, string[] columns, IReadOnlyList<T> items, int top,
        Func<T, object?[]> project, string empty = "No matching observations were retained.")
    {
        var rows = items.Take(top).Select(item => project(item).Select(Cell).ToArray()).ToArray();
        var omitted = items.Count - rows.Length;
        _tables.Add(new(id, title, columns, rows, items.Count, omitted, empty));
        if (omitted > 0) Note($"{title}: {omitted} retained row(s) omitted from the view at topN={top}.");
    }

    public CaptureView Build(string kind, string summary, CliOptions options,
        DateTimeOffset? start, TimeSpan? duration, object? evidence)
    {
        if (_truncatedText > 0) Note($"MaxTextLength={MaxTextLength}: {_truncatedText} text value(s) truncated.");
        if (_omittedNotes > 0)
            _notes[^1] = $"MaxNotes={MaxNotes}: {_omittedNotes + 1} additional note(s) omitted. MaxTextLength={MaxTextLength}: {_truncatedText} text value(s) truncated.";
        return new(kind, kind, summary,
            new("live-stream", false, options.Pid!.Value, start, duration?.TotalSeconds,
                options.DurationSeconds!.Value, null, null),
            _metrics, _tables, _notes, NormalizeEvidence(evidence));
    }

    private static CaptureViewEvidence NormalizeEvidence(object? evidence) => evidence switch
    {
        EvidenceQuality quality => new("collector", quality.Limitations,
            new(quality.Conclusions.RetainedExplicitPositiveEvidence.ToString(),
                quality.Conclusions.AbsenceOrExhaustiveCounts.ToString(),
                quality.Conclusions.RegressionOrHealthyControl.ToString()), quality.Schema),
        NetworkingCaptureQuality quality => new("networking",
            [new(EvidenceLimitationCategory.CaptureWindow, "capture", quality.EventsLost,
                $"Completion={quality.Completion}; transport events lost={quality.EventsLost?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}; parse errors={quality.ParseErrors}. Stream read duration is not proof of target coverage.")]),
        _ => new("not-reported", []),
    };

    private object? Cell(object? value)
    {
        if (value is string text && text.Length > MaxTextLength)
        {
            _truncatedText++;
            return text[..MaxTextLength] + "...";
        }
        return value switch
        {
            DateTimeOffset time => time.ToString("O", CultureInfo.InvariantCulture),
            double number when !double.IsFinite(number) => null,
            float number when !float.IsFinite(number) => null,
            _ => value,
        };
    }
}

internal static class CliStreamingCaptureViews
{
    internal static readonly IReadOnlySet<string> Kinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "allocation", "off_cpu", "contention", "threadpool", "gc", "gc-activities",
        "requests", "kestrel", "db", "networking",
    };

    internal static CaptureView Project(CliCommandResult result, CliOptions options)
    {
        var b = new CaptureViewBuilder();
        var top = options.Top!.Value;
        DateTimeOffset? start = null;
        TimeSpan? duration = null;
        object? evidence = null;
        string summary;
        switch (result.Envelope)
        {
            case DiagnosticResult<AllocationSample> { Data: { } s } envelope:
                summary = envelope.Summary;
                start = s.StartedAt; duration = s.Duration;
                b.Metric("Sampled events", s.TotalEvents);
                b.Metric("Sampled bytes", s.TotalBytes);
                b.Table("types", "Types by sampled bytes", ["Type", "Sampled bytes", "Sampled events", "Heap"], s.TopByBytes, top,
                    t => [t.TypeName, t.TotalBytes, t.EventCount, t.DominantKind.ToString()]);
                b.Table("sites", "Allocation sites", ["Method", "Module", "Sampled bytes", "Sampled events"], s.TopBySite, top,
                    t => [t.Frame.Method, t.Frame.Module, t.TotalBytes, t.EventCount]);
                b.Note("GCAllocationTick is sampled; bytes and event counts are not exact allocation totals. No samples do not prove no allocation.");
                b.Note("Profiling can be expensive. Limits: 64 MiB raw trace, 100,000 ticks, 4,096 types, 16,384 distinct frames/tree nodes and 128 frames per stack. Limit hits and lost-event traces fail explicitly; no partial profile is returned. Trace conversion has additional memory/disk cost; these limits are not a hard process-RSS budget.");
                break;
            case DiagnosticResult<OffCpuSnapshot> { Data: { } s } envelope:
                summary = envelope.Summary;
                start = s.StartedAt; duration = s.Duration;
                b.Metric("Off-CPU time (ms)", s.TotalOffCpuMicros / 1000.0);
                b.Metric("Threads", s.DistinctThreads);
                b.Metric("Scheduler switches", s.SchedSwitches);
                b.Metric("Censored spans", s.CensoredSpans);
                b.Metric("Censored lower bound (ms)", s.CensoredOffCpuMicros / 1000.0);
                b.Table("stacks", "Blocking stacks", ["Leaf", "Off-CPU (ms)", "Occurrences", "State", "Stack (first 8 frames)"], s.TopBlockingStacks, top,
                    h => [h.LeafFrame, h.OffCpuMicros / 1000.0, h.OccurrenceCount, h.DominantState,
                        string.Join(" <- ", h.Stack.Take(8).Select(f => $"{f.Module}!{f.Method}"))]);
                b.Notes(s.Notes);
                b.Note("Stack previews show at most 8 frames. Censored time is a lower bound, not a complete blocking interval.");
                break;
            case DiagnosticResult<ContentionSnapshot> { Data: { } s } envelope:
                summary = envelope.Summary;
                start = s.StartedAt; duration = s.Duration;
                b.Metric("Observed contention events", s.TotalEvents);
                b.Metric("Distinct monitors", s.DistinctMonitors);
                b.Metric("Total contention (ms)", s.TotalContentionDuration.TotalMilliseconds);
                b.Metric("P95 contention (ms)", s.TotalEvents > 0 ? s.P95ContentionDuration.TotalMilliseconds : null);
                b.Table("sites", "Retained contention sites", ["Module", "Method", "Duration (ms)", "Thread"], s.Events, top,
                    e => [e.CallSiteModule, e.CallSiteMethod, e.Duration.TotalMilliseconds, e.ContendingThreadId]);
                b.Notes(s.Notes);
                b.Note("Site rows cover retained events only; aggregate totals cover observed events. Empty data do not prove no unmanaged contention.");
                break;
            case DiagnosticResult<ThreadPoolEventSnapshot> { Data: { } s } envelope:
                summary = envelope.Summary;
                start = s.StartedAt; duration = s.Duration;
                var reasons = ThreadPoolEvidence.GetSummary(s);
                b.Metric("Latest observed workers", s.WorkerThreadTimeline.MaxBy(t => t.Timestamp)?.Count);
                b.Metric("Enqueue events", s.TotalEnqueueEvents);
                b.Metric("Dequeue events", s.TotalDequeueEvents);
                b.Metric("Confirmed starvation adjustments", reasons?.ConfirmedStarvationAdjustments);
                b.Metric("Confirmed cooperative-blocking adjustments", reasons?.ConfirmedCooperativeBlockingAdjustments);
                b.Table("adjustments", "Hill-climbing adjustments", ["Time", "Reason", "Old count", "New count", "Reason provenance"],
                    s.HillClimbing, top, h => [h.Timestamp, h.Reason, h.OldCount, h.NewCount, h.ReasonProvenance]);
                b.Table("origins", "Work item origins", ["Method", "Count"], s.WorkItemOrigins, top, o => [o.Method, o.Count]);
                b.Notes(s.Notes);
                b.Note("Missing worker/reason observations are unavailable, not evidence of a healthy pool.");
                evidence = ThreadPoolEvidence.GetQuality(s);
                break;
            case DiagnosticResult<GcSummary> { Data: { } s } envelope:
                summary = envelope.Summary;
                start = s.StartedAt; duration = s.Duration;
                b.Metric("Observed collections", s.TotalCollections);
                b.Metric("Collection elapsed (ms), not pause time", s.TotalPauseTime.TotalMilliseconds);
                b.Metric("Suspension status", s.PauseMeasurementStatus);
                b.Metric("Authoritative suspension total (ms)", s.Suspension is { IsAuthoritative: true } suspension
                    ? suspension.TotalSuspensionTime?.TotalMilliseconds : null);
                b.Table("generations", "Collections by generation", ["Generation", "Count"], s.Generations, top, g => [g.Generation, g.Count]);
                b.Table("events", "Retained collections", ["Time", "Generation", "Reason", "Type", "Collection elapsed (ms)"],
                    s.Events, top, e => [e.Timestamp, e.Generation, e.Reason, e.Type, e.PauseDuration.TotalMilliseconds]);
                b.Note(s.MeasurementSummary);
                if (s.DroppedEvents > 0) b.Note($"maxEvents=200: {s.DroppedEvents} collection detail row(s) dropped; aggregates continue after detail caps.");
                if (s.DroppedHeapStats > 0) b.Note($"maxEvents=200: {s.DroppedHeapStats} heap-stat row(s) dropped.");
                evidence = s.GetQuality();
                break;
            case DiagnosticResult<InFlightRequestSnapshot> { Data: { } s } envelope:
                summary = envelope.Summary;
                start = s.StartedAt; duration = s.Duration;
                b.Metric("Started", s.RequestsStarted);
                b.Metric("Completed", s.RequestsCompleted);
                b.Metric("In flight at end", s.InFlightCount);
                b.Metric("Long-running", s.LongRunningCount);
                b.Metric("Long-running threshold (ms)", s.LongRunningThresholdMs);
                b.Table("requests", "In-flight requests", ["Method", "Path", "Elapsed (ms)", "Long-running", "Started", "Trace ID"],
                    s.Requests, top, r => [r.Method, r.Path, r.ElapsedMs, r.IsLongRunning, r.StartedAt, r.TraceId]);
                b.Notes(s.Notes);
                b.Note("Paths and trace IDs can contain sensitive application data. In-flight rows describe the end of this capture only.");
                break;
            case DiagnosticResult<KestrelSnapshot> { Data: { } s } envelope:
                summary = envelope.Summary;
                start = s.StartedAt; duration = s.Duration;
                b.Metric("Requests started", s.RequestsStarted);
                b.Metric("Requests stopped", s.RequestsStopped);
                b.Metric("Connections rejected", s.ConnectionsRejected);
                b.Metric("Request P95 (ms)", s.ByOperation.Any(o => o.Count > 0) ? s.RequestP95.TotalMilliseconds : null);
                b.Table("operations", "Request operations", ["Method", "Path", "HTTP", "Count", "Total (ms)", "P95 (ms)"],
                    s.ByOperation, top, o => [o.Method, o.Path, o.HttpVersion, o.Count, o.TotalDuration.TotalMilliseconds, o.P95Duration.TotalMilliseconds]);
                b.Table("counters", "Kestrel counters", ["Counter", "Value", "Unit"], s.Counters, top, c => [c.DisplayName, c.Value, c.Unit]);
                b.Notes(s.Notes);
                b.Note("Request latency requires correlated start/stop pairs; stop events alone do not establish latency. Request paths may contain sensitive data.");
                break;
            case DiagnosticResult<DbSnapshot> { Data: { } s } envelope:
                summary = envelope.Summary;
                start = s.StartedAt; duration = s.Duration;
                b.Metric("Observed commands", s.TotalCommands);
                b.Metric("N+1 candidates", s.NPlusOne.Count);
                b.Table("commands", "Database commands", ["Sanitized command", "Count", "Total (ms)", "P95 (ms)", "Max (ms)"],
                    s.ByCommand, top, c => [c.CommandTextSanitized, c.Count, c.TotalMs, c.P95Ms, c.MaxMs]);
                b.Table("n-plus-one", "N+1 candidates", ["Sanitized command", "Repeats"], s.NPlusOne, top, n => [n.CommandTextSanitized, n.Count]);
                b.Table("pools", "Connection pools", ["Provider", "Latest open", "Max open", "Latest pooled", "Pool exhausted"],
                    s.ConnectionPool, top, p => [p.Provider, p.LatestOpenConnections, p.MaxOpenConnections, p.LatestPooledConnections, p.PoolExhaustedCount]);
                b.Notes(s.Notes);
                foreach (var pool in s.ConnectionPool) b.Notes(pool.Notes);
                b.Note("Sanitized command text can still contain sensitive identifiers; no connection strings are included.");
                break;
            case DiagnosticResult<NetworkingSnapshot> { Data: { } s } envelope:
                summary = envelope.Summary;
                start = s.StartedAt; duration = s.Duration;
                b.Metric("HTTP requests started", s.HttpRequestsStarted);
                b.Metric("HTTP requests failed", s.HttpRequestsFailed);
                b.Metric("DNS lookups started", s.DnsLookupsStarted);
                b.Metric("Socket connects started", s.SocketConnectsStarted);
                b.Metric("Capture completion", s.CaptureQuality?.Completion);
                b.Metric("Transport events lost", s.CaptureQuality?.EventsLost);
                b.Table("operations", "HTTP operations", ["Host", "Path", "Count", "P95 (ms)", "Latency availability"], s.ByOperation, top,
                    o => [o.Host, o.Path, o.Count, o.LatencyAvailability == "measured" ? o.P95Duration.TotalMilliseconds : null, o.LatencyAvailability]);
                b.Table("counters", "Networking counters", ["Provider", "Counter", "Value", "Unit"],
                    s.Counters, top, c => [c.Provider, c.DisplayName, c.Value, c.Unit]);
                b.Notes(s.Notes);
                evidence = s.CaptureQuality;
                b.Note("Missing latency is unavailable, not zero. Hosts and paths can contain sensitive application data.");
                break;
            case DiagnosticResult<GcActivitiesCapture> { Data: { } s } envelope:
                summary = envelope.Summary;
                start = s.Gc.ObservedStart;
                if (s.Gc.ObservedStart is { } from && s.Gc.ObservedEnd is { } to) duration = to - from;
                b.Metric("Capture status", s.Status);
                b.Metric("GC side", s.Gc.Status);
                b.Metric("Activities side", s.Activities.Status);
                b.Metric("Startup skew (ms)", s.StartupSkewMs);
                b.Metric("Overlay measurement", s.Overlay?.MeasurementStatus);
                b.Metric("Impacted activities", s.Overlay?.ImpactedCount);
                b.Table("impact", "Activities overlapping GC", ["Source", "Operation", "Duration (ms)", "GC overlap (ms)", "GC share (%)", "Lower bound"],
                    s.Overlay?.ImpactedActivities ?? [], top, a => [a.SourceName, a.OperationName, a.DurationMs, a.GcPauseMs, a.GcPausePercent, a.GcPauseIsLowerBound]);
                b.Notes(s.Notes);
                b.Note(s.OverlayUnavailableReason);
                b.Note(s.Gc.UnavailableReason);
                b.Note(s.Activities.UnavailableReason);
                if (s.Overlay is { } overlay)
                {
                    b.Metric("Correlation truncated", overlay.CorrelationTruncated);
                    b.Metric("Correlation values lower bounds", overlay.CorrelationValuesAreLowerBounds);
                    b.Note($"Overlay omitted activities={overlay.OutputOmittedActivities}; dropped GC events={overlay.DroppedGcEvents}.");
                }
                if (s.Gc.Capture is { } gc) evidence = gc.GetQuality();
                b.Note("Correlation is not causation. A missing overlay is unavailable, not an absence of GC impact.");
                break;
            default:
                throw new InvalidOperationException("The collector returned no supported structured capture payload.");
        }
        return b.Build(options.Kind!, summary, options, start, duration, evidence) with
        {
            Handle = result.Envelope is DiagnosticResult<AllocationSample> allocation ? allocation.Handle : null,
        };
    }
}
