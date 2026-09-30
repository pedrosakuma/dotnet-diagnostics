using System.Diagnostics;
using System.Text.Json;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.Comparison;

namespace DotnetDiagnostics.Core.UseCases;

public sealed partial class DurableCaptureUseCases
{
    /// <summary>Compares two locally owned retained snapshots without registering handles or attaching to a target.</summary>
    public async Task<HistoricalComparisonResult> CompareHistoricalAsync(HistoricalComparisonRequest request,
        CaptureAccess access, AuthorizeHistoricalCapture authorize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Baseline);
        ArgumentNullException.ThrowIfNull(request.Candidate);
        ArgumentNullException.ThrowIfNull(authorize);
        CapturePackage.ValidateAccess(access);
        foreach (var reference in new[] { request.Baseline, request.Candidate })
        {
            CapturePackage.ValidateId(reference.CaptureId);
            CapturePackage.ValidateId(reference.ArtifactId);
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var budget = new HistoricalComparisonBudget(linked.Token);
        CaptureReader? left = null, right = null;
        try
        {
            // Both acquisitions are attempted before exposing any side's error or metadata.
            CaptureStoreException? unavailable = null;
            try { left = await _store.OpenBoundedAsync(request.Baseline.CaptureId, access, budget.Check, budget.SqliteProgress, linked.Token).ConfigureAwait(false); }
            catch (CaptureStoreException exception) { unavailable = exception; }
            try { right = await _store.OpenBoundedAsync(request.Candidate.CaptureId, access, budget.Check, budget.SqliteProgress, linked.Token).ConfigureAwait(false); }
            catch (CaptureStoreException exception) { unavailable ??= exception; }
            budget.Check();
            if (unavailable is not null || left is null || right is null)
                throw new CaptureStoreException(CaptureErrorCode.NotFound, "Comparison input is unavailable.");
            var la = left.Info.Artifacts.FirstOrDefault(a => a.ArtifactId == request.Baseline.ArtifactId);
            var ra = right.Info.Artifacts.FirstOrDefault(a => a.ArtifactId == request.Candidate.ArtifactId);
            if (la is null || ra is null)
                throw new CaptureStoreException(CaptureErrorCode.NotFound, "Comparison input is unavailable.");
            await authorize(left.Info, la, "diff", linked.Token).ConfigureAwait(false);
            await authorize(right.Info, ra, "diff", linked.Token).ConfigureAwait(false);
            budget.Check();
            if (la.Kind is not ("cpu-sample" or "heap-snapshot" or "counters") ||
                ra.Kind is not ("cpu-sample" or "heap-snapshot" or "counters"))
                throw new CaptureStoreException(CaptureErrorCode.UnsupportedFormat, "UnsupportedComparisonFamily.");
            var l = Materialize(left, la);
            var r = Materialize(right, ra);
            var result = HistoricalComparison.Build(request, left.Info, right.Info, la, ra,
                l.Snapshot!, r.Snapshot!, l.Stream, r.Stream, budget);
            // Current policy is checked again before either side leaves the service.
            await authorize(left.Info, la, "diff", linked.Token).ConfigureAwait(false);
            await authorize(right.Info, ra, "diff", linked.Token).ConfigureAwait(false);
            HistoricalComparison.CheckResultBytes(result);
            budget.Check();
            return result;

            DecodedCaptureArtifact Materialize(CaptureReader reader, CaptureArtifactInfo artifact)
            {
                budget.Check();
                var raw = reader.ReadSnapshot(artifact.ArtifactId, HistoricalComparison.MaximumSideBytes)
                    ?? throw new CaptureStoreException(CaptureErrorCode.UnsupportedFormat, "RetainedSnapshotUnavailable.");
                DecodedCaptureArtifact decoded;
                try
                {
                    CheckJson(raw.Utf8Json.Span, budget);
                    decoded = DecodeArtifact(reader, artifact.ArtifactId, HistoricalComparison.MaximumSideBytes, raw);
                }
                catch (Exception exception) when (exception is JsonException or CaptureStoreException)
                {
                    budget.Check();
                    throw new CaptureStoreException(exception is CaptureStoreException store ? store.Code : CaptureErrorCode.UnsupportedFormat,
                        "UnsupportedComparisonRepresentation.", exception);
                }
                if (decoded.Snapshot is null || decoded.Composition is not null)
                    throw new CaptureStoreException(CaptureErrorCode.UnsupportedFormat, "RetainedSnapshotUnavailable.");
                budget.Check();
                return decoded;
            }
        }
        finally { right?.Dispose(); left?.Dispose(); }
    }

    private static void CheckJson(ReadOnlySpan<byte> bytes, HistoricalComparisonBudget budget)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = CaptureArtifactCodec.MaximumDepth + 2 });
        while (reader.Read()) budget.Visit();
    }
}

internal sealed class HistoricalComparisonBudget(CancellationToken token)
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private long _examined;
    internal int SqliteProgress()
    {
        _examined += 1000;
        return token.IsCancellationRequested || _watch.Elapsed >= TimeSpan.FromSeconds(10) || _examined > 10_000_000 ? 1 : 0;
    }
    internal void Check()
    {
        token.ThrowIfCancellationRequested();
        if (_examined > 10_000_000) Limit("ComparisonExaminedRows");
        if (_watch.Elapsed >= TimeSpan.FromSeconds(10)) Limit("ComparisonMilliseconds");
    }
    internal void Visit(int count = 1)
    {
        _examined += count;
        if (_examined > 10_000_000) Limit("ComparisonExaminedRows");
        Check();
    }
    internal static void Limit(string name) =>
        throw new CaptureStoreException(CaptureErrorCode.CapacityExceeded, name);
}
