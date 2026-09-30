using System.Globalization;
using DotnetDiagnostics.Core.CaptureRecording;

namespace DotnetDiagnostics.Core.CpuSampling;

/// <summary>
/// Normalizes repeated stacks and invariant interpretation metadata during sequential offline
/// CPU replay. Definitions and references belong to the same invocation sink/artifact.
/// </summary>
internal sealed class CpuReplayStackObservationWriter
{
    internal const string DefinitionCategory = "definition.cpu-stack.v1";
    internal const string SampleCategory = "sample.cpu.eventpipe.stack-ref.v1";
    internal const int MaximumStackDefinitions = 4096;
    internal const long MaximumStackCacheBytes = 8 * 1024 * 1024;

    private readonly IReplayCaptureObservationSink _sink;
    private readonly int _maxDefinitions;
    private readonly long _maxCacheBytes;
    private readonly Dictionary<string, string> _definitions = new(StringComparer.Ordinal);
    private readonly string _prefix = "cpu-stack-v1:" + Guid.NewGuid().ToString("N") + ":";
    private long _nextDefinition;
    private long _cacheFallbacks;
    private long _definitionRejections;
    private long _truncatedFallbacks;

    internal CpuReplayStackObservationWriter(IReplayCaptureObservationSink sink,
        int maxDefinitions = MaximumStackDefinitions, long maxCacheBytes = MaximumStackCacheBytes)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentOutOfRangeException.ThrowIfNegative(maxDefinitions);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxDefinitions, MaximumStackDefinitions);
        ArgumentOutOfRangeException.ThrowIfNegative(maxCacheBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxCacheBytes, MaximumStackCacheBytes);
        _sink = sink;
        _maxDefinitions = maxDefinitions;
        _maxCacheBytes = maxCacheBytes;
    }

    internal int DefinitionCount => _definitions.Count;
    internal long CacheBytes { get; private set; }

    internal async ValueTask<bool> AppendAsync(
        int threadId, double relativeMilliseconds, IReadOnlyList<(string Key, string Module, string Display)> frames,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var inline = SamplerObservationProjection.BuildSample("sample.cpu.eventpipe", "trace-relative-seconds",
            relativeMilliseconds / 1000, threadId,
            frames.Select(f => new SamplerObservationProjection.Frame(f.Module, f.Display)),
            additional: [CaptureObservationField.String("evidence", "sample-profiler-thread-sample-not-proven-on-cpu")]);
        var stack = inline.Fields.First(f => f.Name == "stack").Text;
        if (stack is null || inline.Fields.First(f => f.Name == "stackTruncated").Boolean)
        {
            _truncatedFallbacks++;
            return await AppendInlineAsync(inline, "stack-encoding-truncated", cancellationToken).ConfigureAwait(false);
        }

        if (!_definitions.TryGetValue(stack, out var reference))
        {
            reference = _prefix + (++_nextDefinition).ToString(CultureInfo.InvariantCulture);
            // A conservative UTF-16 key/reference plus entry allowance, separate from the
            // unchanged store logical-lifetime budget. Never evict and later reuse an ID.
            var cacheBytes = 256L + 2L * stack.Length + 2L * reference.Length;
            if (_definitions.Count >= _maxDefinitions || cacheBytes > _maxCacheBytes - CacheBytes)
            {
                _cacheFallbacks++;
                return await AppendInlineAsync(inline, "stack-cache-saturated", cancellationToken).ConfigureAwait(false);
            }

            var fields = inline.Fields.Where(f => f.Name is not ("sourceSeconds" or "weight" or "sourceOccurrence" or "provenance")).ToList();
            fields.Add(CaptureObservationField.Bool("sourceOccurrence", false));
            fields.Add(CaptureObservationField.String("provenance", "interpreted-stack-definition"));
            var definition = new CaptureObservation(DefinitionCategory, null, null, reference, fields);
            if (!await _sink.AppendReplayAsync(definition, cancellationToken).ConfigureAwait(false))
            {
                _definitionRejections++;
                // This is a distinct source observation, not a retry of the rejected definition.
                // Never emit a reference when its definition was not admitted.
                return await AppendInlineAsync(inline, "stack-definition-rejected", cancellationToken).ConfigureAwait(false);
            }
            _definitions.Add(stack, reference);
            CacheBytes += cacheBytes;
        }

        // Name is the indexed definition key. Thread, relative time and weight remain per
        // occurrence; method/module/identity, stack order, clock and evidence live in the definition.
        return await _sink.AppendReplayAsync(new CaptureObservation(SampleCategory, null, threadId, reference,
        [
            CaptureObservationField.Double("sourceSeconds", relativeMilliseconds / 1000),
            CaptureObservationField.Int64("weight", 1),
            CaptureObservationField.Bool("sourceOccurrence", true),
        ]), cancellationToken).ConfigureAwait(false);
    }

    internal IReadOnlyList<string> GetNotes()
    {
        var notes = new List<string>(3);
        if (_cacheFallbacks != 0)
            notes.Add($"CPU replay stack dictionary reached MaximumStackDefinitions={_maxDefinitions} or MaximumStackCacheBytes={_maxCacheBytes}; {_cacheFallbacks} sample(s) used explicit inline-stack fallback. Existing definitions remain referenceable; store budgets are unchanged.");
        if (_definitionRejections != 0)
            notes.Add($"CPU replay rejected {_definitionRejections} stack definition offer(s); their source samples used explicit inline-stack fallback, never dangling references. Admission rejections remain in capture quality.");
        if (_truncatedFallbacks != 0)
            notes.Add($"CPU replay used inline-stack fallback for {_truncatedFallbacks} truncated stack encoding(s); MaximumFrames={SamplerObservationProjection.MaximumFrames} and MaximumStructuredBytes={SamplerObservationProjection.MaximumStructuredBytes} remain unchanged.");
        return notes;
    }

    private ValueTask<bool> AppendInlineAsync(CaptureObservation inline, string reason, CancellationToken cancellationToken)
        => _sink.AppendReplayAsync(inline with
        {
            Fields = [.. inline.Fields, CaptureObservationField.String("stackReferenceFallback", reason)],
        }, cancellationToken);
}
