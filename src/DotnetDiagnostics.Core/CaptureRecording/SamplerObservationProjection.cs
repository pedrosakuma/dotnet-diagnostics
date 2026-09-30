using System.Text;
using System.Text.Json;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.Memory;
using DotnetDiagnostics.Core.MethodParameters;
using DotnetDiagnostics.Core.OffCpu;

namespace DotnetDiagnostics.Core.CaptureRecording;

/// <summary>Copies interpreted samples only; no trace/native object escapes into the sink.</summary>
/// <remarks>
/// Stack JSON is leaf-first, capped at 128 frames and 64 KiB, with explicit truncation.
/// Sample weights are not actual allocation counts; native uprobes establish call frequency,
/// not byte volume or proven lock wait time. Unknown clock alignment/loss stays unknown.
/// The adapter, not a callback, owns queue admission, rejection accounting and persistence.
/// </remarks>
internal static class SamplerObservationProjection
{
    internal const int MaximumFrames = 128;
    internal const int MaximumStructuredBytes = 64 * 1024;

    internal readonly record struct Frame(string Module, string Method, MethodIdentity? Identity = null);

    internal static void Sample(
        ICaptureObservationSink sink, string category, string sourceClock, double? sourceSeconds,
        long? threadId, IEnumerable<Frame> leafToRoot, long weight = 1, string weightUnit = "samples",
        long? samplePeriod = null, IReadOnlyList<CaptureObservationField>? additional = null)
        => sink.TryAppend(BuildSample(category, sourceClock, sourceSeconds, threadId, leafToRoot,
            weight, weightUnit, samplePeriod, additional));

    internal static CaptureObservation BuildSample(
        string category, string sourceClock, double? sourceSeconds,
        long? threadId, IEnumerable<Frame> leafToRoot, long weight = 1, string weightUnit = "samples",
        long? samplePeriod = null, IReadOnlyList<CaptureObservationField>? additional = null)
    {
        var fields = new List<CaptureObservationField>
        {
            CaptureObservationField.Bool("sourceOccurrence", true),
            CaptureObservationField.String("provenance", "interpreted-sample"),
            CaptureObservationField.String("sourceClock", sourceClock),
            sourceSeconds is { } seconds ? CaptureObservationField.Double("sourceSeconds", seconds) : CaptureObservationField.Null("sourceSeconds"),
            CaptureObservationField.Int64("weight", weight),
            CaptureObservationField.String("weightUnit", weightUnit),
            samplePeriod is { } period ? CaptureObservationField.Int64("samplePeriod", period) : CaptureObservationField.Null("samplePeriod"),
            CaptureObservationField.String("stackOrder", "leaf-to-root"),
        };
        string? name = null;
        var count = 0;
        var truncated = false;
        using var buffer = new BoundedSnapshotEncoding(MaximumStructuredBytes);
        try
        {
            using var writer = new Utf8JsonWriter(buffer);
            writer.WriteStartArray();
            foreach (var frame in leafToRoot)
            {
                if (count == 0)
                {
                    name = frame.Method;
                    fields.Add(CaptureObservationField.String("module", frame.Module));
                    fields.Add(CaptureObservationField.String("method", frame.Method));
                    if (frame.Identity is { } identity)
                    {
                        fields.Add(CaptureObservationField.String("moduleVersionId", identity.ModuleVersionId?.ToString("D")));
                        fields.Add(identity.MetadataToken is { } token
                            ? CaptureObservationField.Int64("metadataToken", token) : CaptureObservationField.Null("metadataToken"));
                        fields.Add(CaptureObservationField.String("type", identity.TypeFullName));
                    }
                }
                if (count == MaximumFrames) { truncated = true; break; }
                writer.WriteStartObject();
                writer.WriteString("module", frame.Module);
                writer.WriteString("method", frame.Method);
                writer.WritePropertyName("identity");
                JsonSerializer.Serialize(writer, frame.Identity, CaptureSnapshotEncodingContext.Default.MethodIdentity);
                writer.WriteEndObject();
                count++;
            }
            writer.WriteEndArray();
            writer.Flush();
            fields.Add(CaptureObservationField.String("stack", Encoding.UTF8.GetString(buffer.WrittenSpan)));
        }
        catch (InvalidDataException)
        {
            truncated = true;
            fields.Add(CaptureObservationField.Null("stack"));
        }
        fields.Add(CaptureObservationField.Bool("stackTruncated", truncated));
        if (category.StartsWith("sample.native-", StringComparison.Ordinal))
        {
            fields.Add(CaptureObservationField.String("evidence", category.Contains("lock-contention", StringComparison.Ordinal)
                ? "sampled-mutex-call-not-proven-contention"
                : category.Contains("etw-virtualalloc", StringComparison.Ordinal)
                    ? "virtualalloc-commit-event-not-malloc-volume" : "sampled-allocation-call-not-byte-volume"));
            fields.Add(CaptureObservationField.Null("allocatedBytes"));
            fields.Add(CaptureObservationField.Null("waitMicroseconds"));
        }
        if (additional is not null) fields.AddRange(additional);
        return new CaptureObservation(category, null, threadId, name, fields);
    }

    internal static void OffCpu(ICaptureObservationSink sink, OffCpuSpan span)
        => Sample(sink, "sample.off-cpu", span.OutTimestampSeconds.HasValue ? span.SourceClock : "unavailable",
            span.OutTimestampSeconds, span.Tid,
            span.BlockingStack.Select(f => new Frame(f.Module, f.Method, f.Identity)),
            span.DurationMicros, "off-cpu-microseconds", additional:
            [
                CaptureObservationField.String("threadName", span.Comm),
                CaptureObservationField.String("previousState", span.PrevState),
                CaptureObservationField.String("syscall", span.Syscall),
                CaptureObservationField.Bool("censoredLowerBound", span.IsCensored),
                CaptureObservationField.String("nativeContentionSpanClassification",
                    DotnetDiagnostics.Core.NativeLockContention.NativeLockContentionUx.ClassifyOffCpuSpan(span).ToString()),
            ]);

    internal static void Invocation(ICaptureObservationSink sink, int threadId, MethodParameterInvocation invocation)
    {
        // Called only for admitted, allowlisted invocations, after RenderParameter redaction.
        // TimestampUtc is the existing observer receipt time, not a profiler event clock.
        var fields = new List<CaptureObservationField>
        {
            CaptureObservationField.Bool("sourceOccurrence", true),
            CaptureObservationField.String("provenance", "accepted-redacted-invocation"),
            CaptureObservationField.String("sourceClock", "observer-receipt-utc"),
            CaptureObservationField.Int64("sequence", invocation.Sequence),
            CaptureObservationField.String("module", invocation.Method.ModuleName),
            CaptureObservationField.String("moduleVersionId", invocation.Method.ModuleVersionId),
            CaptureObservationField.Int64("metadataToken", invocation.Method.MetadataToken),
            CaptureObservationField.String("type", invocation.Method.TypeName),
            CaptureObservationField.String("method", invocation.Method.MethodName),
        };
        using var buffer = new BoundedSnapshotEncoding(MaximumStructuredBytes);
        try
        {
            using var writer = new Utf8JsonWriter(buffer);
            JsonSerializer.Serialize(writer, invocation, CaptureSnapshotEncodingContext.Default.MethodParameterInvocation);
            writer.Flush();
            fields.Add(CaptureObservationField.String("invocation", Encoding.UTF8.GetString(buffer.WrittenSpan)));
            fields.Add(CaptureObservationField.Bool("structuredOmitted", false));
        }
        catch (InvalidDataException)
        {
            fields.Add(CaptureObservationField.Bool("structuredOmitted", true));
        }
        sink.TryAppend(new CaptureObservation("sample.method-params", invocation.TimestampUtc, threadId, invocation.Method.MethodName, fields));
    }
}
