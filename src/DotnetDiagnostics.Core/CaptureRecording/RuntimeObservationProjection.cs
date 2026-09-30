using System.Globalization;
using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Contention;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Logs;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.ThreadPool;
using F = DotnetDiagnostics.Core.CaptureRecording.CaptureObservationField;

namespace DotnetDiagnostics.Core.CaptureRecording;

/// <summary>Interpreted callback facts; snapshot retention and transport quality remain independent.</summary>
internal static class RuntimeObservationProjection
{
    internal static F Number(string name, double? value) =>
        value is { } number ? F.Double(name, number) : F.Null(name);

    internal static F Integer(string name, long? value) =>
        value is { } number ? F.Int64(name, number) : F.Null(name);

    internal static F Time(string name, DateTimeOffset? value) =>
        F.String(name, value?.ToString("O", CultureInfo.InvariantCulture));

    internal static void Counter(ICaptureObservationSink sink, CounterValue value, DateTimeOffset? at,
        double? relativeMilliseconds, bool first, SensitiveDataRedactor redactor, bool valueAvailable)
        => sink.TryAppend(new("counter.interval", at, null, redactor.Redact(value.Name),
        [
            F.String("provider", redactor.Redact(value.Provider)), F.String("displayName", redactor.Redact(value.DisplayName)),
            F.String("kind", value.Kind.ToString()), F.String("unit", redactor.Redact(value.Unit)),
            Number("value", valueAvailable ? value.Value : null), Number("intervalSeconds", value.IntervalSec),
            Number("ratePerSecond", valueAvailable && CounterValueNormalization.TryGetRate(value, out var rate) ? rate : null),
            Number("displayRateTimeScaleSeconds", value.DisplayRateTimeScale?.TotalSeconds),
            Number("relativeMilliseconds", relativeMilliseconds), F.Bool("firstObservedSample", first),
            F.String("semantics", "Mean is the interval mean; Sum is the interval increment, not a cumulative total."),
            F.String("firstSampleSemantics", "First observed interval may include time before attachment; it is not discarded."),
        ]));

    internal static void Meter(ICaptureObservationSink sink, MeterInstrumentValue value, DateTimeOffset? at,
        double? relativeMilliseconds, SensitiveDataRedactor redactor, int maxTimeSeries,
        bool histogramCountAvailable, bool histogramSumAvailable)
    {
        var fields = new List<F>
        {
            F.String("meter", redactor.Redact(value.Meter)), F.String("kind", value.Kind), F.String("unit", redactor.Redact(value.Unit)),
            Number("lastValue", value.LastValue), Number("rate", value.Rate),
            Number("relativeMilliseconds", relativeMilliseconds),
            Integer("histogramCount", histogramCountAvailable ? value.Histogram?.Count : null),
            Number("histogramSum", histogramSumAvailable ? value.Histogram?.Sum : null),
            Number("p50", value.Histogram?.P50), Number("p95", value.Histogram?.P95),
            Number("p99", value.Histogram?.P99),
            F.String("seriesPolicy", "Provider and collector MaxTimeSeries/MaxHistograms limits apply."),
            F.Int64("maxTimeSeries", maxTimeSeries),
        };
        AddValues(fields, "tag.", value.Tags, value.Tags.Count, redactor);
        sink.TryAppend(new("meter.interval", at, null, redactor.Redact(value.Instrument), fields));
    }

    internal static void Activity(ICaptureObservationSink sink, CapturedActivity value, SensitiveDataRedactor redactor,
        DateTimeOffset? observedStopAt, bool hasStartTime)
    {
        var fields = new List<F>
        {
            F.String("source", redactor.Redact(value.SourceName)), F.String("id", redactor.Redact(value.Id)),
            F.String("parentId", redactor.Redact(value.ParentId)),
            F.String("traceId", value.TraceId), F.String("spanId", value.SpanId), F.String("parentSpanId", value.ParentSpanId),
            Time("startedAt", hasStartTime ? value.StartedAt : null),
            Time("stoppedAt", hasStartTime ? value.StoppedAt : observedStopAt),
            Integer("durationTicks", value.Duration?.Ticks),
            F.String("timing", !hasStartTime ? "start-unavailable; stop-event-clock"
                : value.Duration is null ? "stop-observed-duration-unavailable" : "provider-start-and-duration"),
            F.String("destination", "Not projected here; post-drain authority correlation is snapshot-only."),
        };
        AddValues(fields, "tag.", value.Tags.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)), value.Tags.Count, redactor);
        sink.TryAppend(new("activity.stop", observedStopAt ?? value.StoppedAt, null, redactor.Redact(value.OperationName), fields));
    }

    // Labels may carry arbitrary target values. Cap field cardinality, redact credentials and
    // suppress header values; SQL literals use the existing SQL redaction policy.
    internal static void AddValues(List<F> fields, string prefix, IEnumerable<KeyValuePair<string, string?>>? values, int count,
        SensitiveDataRedactor redactor)
    {
        const int maxFields = 128;
        var retained = 0;
        if (values is not null)
        {
            foreach (var (key, value) in values)
            {
                if (retained == maxFields) break;
                var sensitive = key.Contains("header", StringComparison.OrdinalIgnoreCase)
                    || key.Contains("password", StringComparison.OrdinalIgnoreCase)
                    || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
                    || key.Contains("token", StringComparison.OrdinalIgnoreCase)
                    || key.Contains("credential", StringComparison.OrdinalIgnoreCase)
                    || key.Contains("cookie", StringComparison.OrdinalIgnoreCase)
                    || key.Contains("api_key", StringComparison.OrdinalIgnoreCase)
                    || key.Contains("api.key", StringComparison.OrdinalIgnoreCase)
                    || key.Contains("apikey", StringComparison.OrdinalIgnoreCase)
                    || key.Contains("authorization", StringComparison.OrdinalIgnoreCase);
                var sql = key.Contains("statement", StringComparison.OrdinalIgnoreCase)
                    || key.Contains("query", StringComparison.OrdinalIgnoreCase)
                    || key.Contains("sql", StringComparison.OrdinalIgnoreCase);
                fields.Add(F.String(prefix + redactor.Redact(key), value is null ? null : sensitive ? SensitiveDataRedactor.RedactedPlaceholder
                    : sql ? redactor.RedactSqlText(value) : redactor.Redact(value)));
                retained++;
            }
        }
        fields.Add(F.Int64(prefix.TrimEnd('.') + "OmittedFieldCount", count - retained));
        fields.Add(F.Int64(prefix.TrimEnd('.') + "FieldLimit", maxFields));
    }

    internal static void Exception(ICaptureObservationSink sink, DateTimeOffset at, int threadId,
        string? type, string? message, int hresult, SensitiveDataRedactor redactor)
        => sink.TryAppend(new("exception.thrown", at, threadId, redactor.Redact(type),
        [F.String("exceptionType", redactor.Redact(type)), F.String("message", redactor.Redact(message)),
            F.Int64("hresult", hresult)]));

    internal static void Contention(ICaptureObservationSink sink, ContentionEventSample value,
        bool validDuration, string durationSource)
        => sink.TryAppend(new("contention.wait", value.StoppedAt, value.ContendingThreadId, null,
        [
            Time("startedAt", value.StartedAt), Integer("durationTicks", validDuration ? value.Duration.Ticks : null),
            F.String("durationSource", validDuration ? durationSource : "regressing-time-unavailable"),
            Integer("ownerManagedThreadId", value.OwnerManagedThreadId),
            F.String("lockId", value.LockId == 0 ? null : value.LockId.ToString(CultureInfo.InvariantCulture)),
            F.String("associatedObjectId", value.AssociatedObjectId == 0 ? null : value.AssociatedObjectId.ToString(CultureInfo.InvariantCulture)),
            F.String("callSiteMethod", value.CallSiteMethod == "(unknown)" ? null : value.CallSiteMethod),
            F.String("callSiteModule", value.CallSiteModule == "(unknown)" ? null : value.CallSiteModule),
            F.String("correlation", "matched-start-stop"),
        ]));

    internal static void Collection(ICaptureObservationSink sink, GcEvent value)
        => sink.TryAppend(new("gc.collection", value.Timestamp, null, value.Type,
        [
            Integer("clrInstanceId", value.ClrInstanceId), Integer("collectionCount", value.CollectionCount),
            F.Int64("generation", value.Generation), F.String("reason", value.Reason),
            F.Int64("elapsedTicks", value.PauseDuration.Ticks),
            F.String("correlation", "matched-clr-count-boundaries; elapsed is not stop-the-world pause"),
        ]));

    internal static void Heap(ICaptureObservationSink sink, GcHeapStatsSample value, int version, int clrInstanceId)
        => sink.TryAppend(new("gc.heap", value.Timestamp, null, null,
        [
            F.Int64("gen0SizeBytes", value.Gen0SizeBytes), F.Int64("gen1SizeBytes", value.Gen1SizeBytes),
            F.Int64("gen2SizeBytes", value.Gen2SizeBytes), F.Int64("lohSizeBytes", value.LohSizeBytes),
            Integer("pohSizeBytes", version >= 2 ? value.PohSizeBytes : null), F.Int64("totalHeapSizeBytes", value.TotalHeapSizeBytes),
            Integer("totalPromotedBytes", value.TotalPromotedBytes), Integer("gen2PromotedBytes", value.Gen2PromotedBytes),
            Integer("pohPromotedBytes", version >= 2 ? value.PohPromotedBytes : null), Integer("finalizationPromotedBytes", value.FinalizationPromotedBytes),
            Integer("finalizationPromotedCount", value.FinalizationPromotedCount),
            Integer("pinnedObjectCount", value.PinnedObjectCount), Integer("gcHandleCount", value.GcHandleCount),
            F.String("correlation", "heap-stat-event; no inferred collection association"),
            F.Int64("eventVersion", version), Integer("clrInstanceId", version >= 1 ? clrInstanceId : null),
        ]));

    internal static void ThreadCount(ICaptureObservationSink sink, string kind, EventPipeThreadPoolCollector.CountSample value)
        => sink.TryAppend(new("threadpool.count", value.Timestamp, null, kind,
            [F.Int64("count", value.Count), F.String("provenance", value.Provenance)]));

    internal static void HillClimbing(ICaptureObservationSink sink, ThreadPoolHillClimbingSample value)
        => sink.TryAppend(new("threadpool.adjustment", value.Timestamp, null, value.Reason,
        [
            Integer("oldCount", value.OldCount), Integer("newCount", value.NewCount),
            Number("latestThroughput", value.Throughput),
            F.String("throughputSemantics", "latest observed throughput; may be carried forward"),
            F.String("reasonProvenance", value.ReasonProvenance),
            F.String("oldCountProvenance", value.OldCountProvenance),
            F.String("newCountProvenance", value.NewCountProvenance),
        ]));

    internal static void Log(ICaptureObservationSink sink, EventPipeLogCollector.MutableLogEntry value, SensitiveDataRedactor redactor)
    {
        var fields = new List<F>
        {
            F.String("category", redactor.Redact(value.Category)), F.String("level", value.Level.ToString()),
            F.Int64("eventId", value.EventId), F.String("eventName", redactor.Redact(value.EventName)),
            F.String("message", value.Message), F.String("exceptionType", value.ExceptionType),
            F.String("exceptionMessage", value.ExceptionMessage),
            F.String("trust", "untrusted-target-data"),
        };
        AddValues(fields, "scope.", value.Scopes.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)),
            value.Scopes.Count, redactor);
        sink.TryAppend(new("log.entry", value.Timestamp, null, redactor.Redact(value.EventName), fields));
    }
}
