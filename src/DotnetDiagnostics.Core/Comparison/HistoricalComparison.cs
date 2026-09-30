using System.Text.Json;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.UseCases;

namespace DotnetDiagnostics.Core.Comparison;

internal static partial class HistoricalComparison
{
    internal const int MaximumSideBytes = 1024 * 1024;
    private const int MaximumRows = 1000;
    private const int MaximumMetrics = 2000;
    private const string Semantics = "retained/v1";

    internal static HistoricalComparisonResult Build(HistoricalComparisonRequest request,
        CaptureInfo leftCapture, CaptureInfo rightCapture, CaptureArtifactInfo leftArtifact, CaptureArtifactInfo rightArtifact,
        object left, object right, DurableCaptureRecordStreamInfo? leftStream, DurableCaptureRecordStreamInfo? rightStream,
        HistoricalComparisonBudget budget)
    {
        var reasons = new List<HistoricalComparisonReason>
        {
            new("RetainedOnly", "both", "population",
                "Retained observations only; missing rows are not zero. Differences do not establish causation or performance gains."),
        };
        var metrics = new List<HistoricalMetric>();
        var incompatible = leftArtifact.Kind != rightArtifact.Kind;
        if (incompatible) reasons.Add(new("KindMismatch", "both", "kind", "Artifact families differ."));
        else if (left is CpuSampleTraceArtifact lc && right is CpuSampleTraceArtifact rc)
            incompatible = Cpu(lc, rc, metrics, reasons, budget);
        else if (left is HeapSnapshotArtifact lh && right is HeapSnapshotArtifact rh)
            incompatible = Heap(lh, rh, metrics, reasons, budget);
        else if (left is CounterSnapshot ln && right is CounterSnapshot rn)
            Counters(ln, rn, metrics, reasons, budget);
        else throw new CaptureStoreException(CaptureErrorCode.UnsupportedFormat, "UnsupportedComparisonRepresentation.");
        var lq = Quality(leftCapture, leftStream, left);
        var rq = Quality(rightCapture, rightStream, right);
        if (!leftCapture.Quality.IsComplete || !rightCapture.Quality.IsComplete)
            reasons.Add(new("IncompleteOrUnknownCaptureQuality", "both", "quality", "Source/storage completeness is not established on both sides."));
        if (metrics.Any(m => m.UnavailableReason is "UnitMismatch" or "UnitUnknown" or "AggregationMismatch"))
            reasons.Add(new("MetricSemanticsMismatch", "both", "metrics", "Incompatible metric definitions suppress the affected deltas."));
        if (metrics.Count > 0 && metrics.All(m => m.UnavailableReason is "UnitMismatch" or "UnitUnknown" or "AggregationMismatch"))
            incompatible = true;
        return new(HistoricalComparisonResult.SchemaV1,
            Identity(request.Baseline, leftCapture, leftArtifact, left), Identity(request.Candidate, rightCapture, rightArtifact, right),
            new(incompatible ? "incompatible" : "qualified", reasons), new(lq, rq), metrics);
    }

    private static HistoricalCaptureIdentity Identity(HistoricalCaptureReference reference, CaptureInfo capture,
        CaptureArtifactInfo artifact, object value)
    {
        var start = value switch
        {
            CpuSampleTraceArtifact cpu => cpu.StartedAt,
            CounterSnapshot counters => counters.StartedAt,
            HeapSnapshotArtifact heap => heap.CapturedAt,
            _ => artifact.Provenance?.StartedAt,
        };
        TimeSpan? duration = value switch
        {
            CpuSampleTraceArtifact cpu => cpu.Duration,
            CounterSnapshot counters => counters.Duration,
            HeapSnapshotArtifact heap => heap.WalkDuration,
            _ => artifact.Provenance?.Duration,
        };
        return new(reference, artifact.Kind, artifact.Provenance, capture.PortableSource, artifact.SourceArtifactId,
            capture.DerivedFrom, start, duration,
            value is HeapSnapshotArtifact ? "Retained point-in-time rows; duration is walk duration."
                : "Reported capture window; not an observed rate denominator or a workload-equivalence guarantee.")
        {
            CpuEvidence = (value as CpuSampleTraceArtifact)?.Evidence,
            HeapOrigin = (value as HeapSnapshotArtifact)?.Origin,
            HeapRuntime = (value as HeapSnapshotArtifact)?.Runtime,
        };
    }

    private static HistoricalSideQuality Quality(CaptureInfo capture, DurableCaptureRecordStreamInfo? stream, object value)
    {
        var notes = value switch
        {
            CpuSampleTraceArtifact cpu => cpu.Notes.Concat(cpu.Evidence?.Limitations ?? []).ToArray(),
            CounterSnapshot counters => counters.Notes.ToArray(),
            HeapSnapshotArtifact heap => (heap.Warnings ?? []).ToArray(),
            _ => [],
        };
        return new(capture.Quality, stream, notes) { Evidence = (value as HeapSnapshotArtifact)?.Quality };
    }

    internal static void CheckResultBytes(HistoricalComparisonResult result)
    {
        try
        {
            using var buffer = new BoundedSnapshotEncoding(MaximumSideBytes);
            using var writer = new Utf8JsonWriter(buffer);
            JsonSerializer.Serialize(writer, result, HistoricalComparisonJsonContext.Default.HistoricalComparisonResult);
            writer.Flush();
        }
        catch (InvalidDataException)
        {
            HistoricalComparisonBudget.Limit("ComparisonResultBytes");
        }
    }

    private static void Add(List<HistoricalMetric> output, string key, string? unit, string aggregation,
        string normalization, decimal? left, decimal? right, string? unavailable = null)
    {
        if (output.Count == MaximumMetrics) HistoricalComparisonBudget.Limit("ComparisonMetrics");
        decimal? absolute = null, relative = null;
        unavailable ??= left is null || right is null ? "MissingObservation" : null;
        if (unavailable is null)
        {
            try
            {
                absolute = right!.Value - left!.Value;
                if (left == 0) unavailable = "ZeroBaseline";
                else
                {
                    try { relative = absolute / Math.Abs(left.Value); }
                    catch (OverflowException) { unavailable = "RelativeNumericRangeExceeded"; }
                }
            }
            catch (OverflowException) { unavailable = "NumericRangeExceeded"; absolute = null; }
        }
        output.Add(new(key, unit, Semantics, "retained-observations", aggregation, normalization,
            left, right, absolute, relative, unavailable));
    }

    private static string Key(params string?[] values) => JsonSerializer.Serialize(values);
    private static decimal? Number(double value)
    {
        if (!double.IsFinite(value)) return null;
        return decimal.TryParse(value.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var result) &&
            (value == 0 || result != 0) ? result : null;
    }
}
