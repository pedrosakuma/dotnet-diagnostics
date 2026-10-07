using System.Collections.Immutable;

namespace DotnetDiagnostics.Core.Dump;

/// <summary>
/// COM RCW/CCW leak-detection view (issue #1118). Aggregates ClrMD's
/// <c>ComCallableWrapper</c>/<c>RuntimeCallableWrapper</c> data observed during the heap walk,
/// plus the runtime's own pending-cleanup queue backlog (<c>EnumerateRcwCleanupData</c> /
/// <c>EnumerateSyncBlockCleanupData</c>), so the model can spot "someone didn't call
/// Marshal.ReleaseComObject / Dispose" (disconnected-but-still-referenced RCWs) and "native COM
/// caller never released this managed object" (CCWs whose ref-count never reaches zero) leak
/// smells from a single dump-capable pass.
/// </summary>
/// <param name="CcwCount">Total ComCallableWrapper instances observed (managed objects exposed to native COM callers).</param>
/// <param name="CcwTotalRefCount">Sum of <c>ComCallableWrapper.RefCount</c> across every CCW observed.</param>
/// <param name="TopCcwTypes">CCW-hosting managed types ranked by aggregate ref-count.</param>
/// <param name="RcwCount">Total RuntimeCallableWrapper instances observed (native COM objects exposed to managed callers).</param>
/// <param name="RcwTotalRefCount">Sum of <c>RuntimeCallableWrapper.RefCount</c> across every RCW observed.</param>
/// <param name="RcwDisconnectedCount">RCWs where <c>IsDisconnected</c> is true but the wrapper is still reachable from the managed heap — the leak smell: the native side thinks it's gone, nothing freed the managed side.</param>
/// <param name="RcwWinRtObjectCount">RCWs whose <c>WinRTObject</c> handle is non-zero — i.e. backed by a WinRT object, not plain COM (see remarks on <see cref="RcwTypeStat.WinRtObjectCount"/>).</param>
/// <param name="TopRcwTypes">RCW-hosting managed types ranked by aggregate ref-count.</param>
/// <param name="RcwCleanupBacklogCount">Total entries currently queued in the runtime's RCW cleanup list (<c>ClrRuntime.EnumerateRcwCleanupData</c>) — a backlog that isn't draining capture-to-capture signals RCWs are being created faster than cleaned up.</param>
/// <param name="RcwCleanupBacklogSample">A bounded sample of the RCW cleanup backlog (first <see cref="ComWrapperAggregation.MaxCleanupBacklogSample"/> entries); the count above is exact even when this sample is truncated.</param>
/// <param name="SyncBlockCleanupBacklogCount">Total entries queued in the runtime's sync-block cleanup list (<c>ClrRuntime.EnumerateSyncBlockCleanupData</c>) — covers RCW/CCW/class-factory cleanup pending via the SyncBlock table.</param>
/// <param name="Notes">Human-readable call-outs (e.g. disconnected-RCW leak smell detected, caps hit).</param>
public sealed record ComWrappersView(
    int CcwCount,
    long CcwTotalRefCount,
    ImmutableArray<CcwTypeStat> TopCcwTypes,
    int RcwCount,
    long RcwTotalRefCount,
    int RcwDisconnectedCount,
    int RcwWinRtObjectCount,
    ImmutableArray<RcwTypeStat> TopRcwTypes,
    int RcwCleanupBacklogCount,
    ImmutableArray<ComCleanupBacklogEntry> RcwCleanupBacklogSample,
    int SyncBlockCleanupBacklogCount,
    ImmutableArray<string> Notes);

/// <summary>ComCallableWrapper instances grouped by the managed type they wrap.</summary>
public sealed record CcwTypeStat(
    string TypeFullName,
    int Count,
    long TotalRefCount,
    long MaxRefCount,
    long TotalInterfaceCount,
    TypeIdentity? Identity);

/// <summary>
/// RuntimeCallableWrapper instances grouped by the managed type they're exposed as.
/// </summary>
/// <remarks>
/// <see cref="WinRtObjectCount"/> counts RCWs whose ClrMD <c>WinRTObject</c> property is non-zero.
/// That property is a <c>ulong</c> handle to the internal WinRT object associated with the RCW (if
/// one exists) — not a boolean flag, despite the "WinRT-specific wrapper flag" name suggested by
/// some initial research. Confirmed via reflection against the installed ClrMD 4.1.745802 and the
/// upstream ClrMD source (<c>RuntimeCallableWrapper.cs</c>: "Gets the internal WinRT object
/// associated with this RCW (if one exists)"). Zero means "not a WinRT object" (plain COM RCW);
/// non-zero is the internal object's address, which this view intentionally does not surface
/// per-instance (it's an internal pointer, not actionable on its own) — only the derived boolean
/// "is this a WinRT RCW" distinction is exposed.
/// </remarks>
public sealed record RcwTypeStat(
    string TypeFullName,
    int Count,
    long TotalRefCount,
    long MaxRefCount,
    int DisconnectedCount,
    int WinRtObjectCount,
    TypeIdentity? Identity);

/// <summary>One entry from the runtime's RCW pending-cleanup queue (<c>ClrRcwCleanupData</c>).</summary>
public sealed record ComCleanupBacklogEntry(
    ulong RcwAddress,
    ulong ContextAddress,
    ulong ThreadAddress,
    bool IsFreeThreaded);

/// <summary>
/// Builds a <see cref="ComWrappersView"/> incrementally from per-object CCW/RCW samples observed
/// during the single-pass heap walk (<see cref="ClrMdHeapWalker"/>), plus the runtime-level cleanup
/// backlog counted separately (not per-object — see <c>ClrMdDumpInspector.WalkComWrapperCleanupBacklog</c>).
/// Mirrors the test-double-friendly shape of <see cref="GcHandleAggregation"/>: callers feed plain
/// sample records rather than live <c>ClrObject</c>/<c>ComCallableWrapper</c>/<c>RuntimeCallableWrapper</c>
/// instances, so the aggregation/bucketing logic is unit-testable without a real COM-interop process.
/// </summary>
internal static class ComWrapperAggregation
{
    private const string UnknownTypeName = "<collected-or-unresolved>";

    /// <summary>Individual RCW cleanup-backlog entries retained inline; the exact total count is never truncated.</summary>
    internal const int MaxCleanupBacklogSample = 50;

    /// <summary>Default number of ranked CCW/RCW type rows retained per kind.</summary>
    internal const int DefaultTopTypesPerKind = 20;

    internal readonly record struct CcwSample(
        string? TypeFullName,
        int RefCount,
        int InterfaceCount,
        TypeIdentity? Identity);

    internal readonly record struct RcwSample(
        string? TypeFullName,
        int RefCount,
        bool IsDisconnected,
        bool IsWinRtObject,
        TypeIdentity? Identity);

    internal static ComWrappersView Aggregate(
        IEnumerable<CcwSample> ccwSamples,
        IEnumerable<RcwSample> rcwSamples,
        int topTypesPerKind = DefaultTopTypesPerKind)
    {
        ArgumentNullException.ThrowIfNull(ccwSamples);
        ArgumentNullException.ThrowIfNull(rcwSamples);

        var builder = new Builder(topTypesPerKind);
        foreach (var sample in ccwSamples)
        {
            builder.AddCcw(sample);
        }

        foreach (var sample in rcwSamples)
        {
            builder.AddRcw(sample);
        }

        return builder.BuildView();
    }

    internal sealed class Builder
    {
        private readonly int _topTypesPerKind;
        private readonly Dictionary<ComWrapperTypeKey, RawCcwTypeStat> _ccwTypes = new();
        private readonly Dictionary<ComWrapperTypeKey, RawRcwTypeStat> _rcwTypes = new();
        private readonly List<ComCleanupBacklogEntry> _rcwCleanupSample = new();
        private string? _detectionUnavailableNote;

        public Builder(int topTypesPerKind = DefaultTopTypesPerKind)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topTypesPerKind);
            _topTypesPerKind = topTypesPerKind;
        }

        public int CcwCount { get; private set; }
        public long CcwTotalRefCount { get; private set; }
        public int RcwCount { get; private set; }
        public long RcwTotalRefCount { get; private set; }
        public int RcwDisconnectedCount { get; private set; }
        public int RcwWinRtObjectCount { get; private set; }
        public int RcwCleanupBacklogCount { get; private set; }
        public int SyncBlockCleanupBacklogCount { get; private set; }

        public void AddCcw(CcwSample sample)
        {
            CcwCount++;
            CcwTotalRefCount += sample.RefCount;

            var typeFullName = string.IsNullOrWhiteSpace(sample.TypeFullName) ? UnknownTypeName : sample.TypeFullName;
            var key = BuildKey(typeFullName, sample.Identity);
            if (!_ccwTypes.TryGetValue(key, out var stat))
            {
                stat = new RawCcwTypeStat(typeFullName, sample.Identity);
                _ccwTypes[key] = stat;
            }

            stat.Count++;
            stat.TotalRefCount += sample.RefCount;
            stat.MaxRefCount = Math.Max(stat.MaxRefCount, sample.RefCount);
            stat.TotalInterfaceCount += sample.InterfaceCount;
        }

        public void AddRcw(RcwSample sample)
        {
            RcwCount++;
            RcwTotalRefCount += sample.RefCount;
            if (sample.IsDisconnected) RcwDisconnectedCount++;
            if (sample.IsWinRtObject) RcwWinRtObjectCount++;

            var typeFullName = string.IsNullOrWhiteSpace(sample.TypeFullName) ? UnknownTypeName : sample.TypeFullName;
            var key = BuildKey(typeFullName, sample.Identity);
            if (!_rcwTypes.TryGetValue(key, out var stat))
            {
                stat = new RawRcwTypeStat(typeFullName, sample.Identity);
                _rcwTypes[key] = stat;
            }

            stat.Count++;
            stat.TotalRefCount += sample.RefCount;
            stat.MaxRefCount = Math.Max(stat.MaxRefCount, sample.RefCount);
            if (sample.IsDisconnected) stat.DisconnectedCount++;
            if (sample.IsWinRtObject) stat.WinRtObjectCount++;
        }

        /// <summary>
        /// Records one entry from <c>ClrRuntime.EnumerateRcwCleanupData()</c>. Call once per entry;
        /// the exact count is tracked unconditionally while only the first
        /// <see cref="MaxCleanupBacklogSample"/> entries are retained for inline display.
        /// </summary>
        public void AddRcwCleanupEntry(ComCleanupBacklogEntry entry)
        {
            RcwCleanupBacklogCount++;
            if (_rcwCleanupSample.Count < MaxCleanupBacklogSample)
            {
                _rcwCleanupSample.Add(entry);
            }
        }

        /// <summary>Records one entry from <c>ClrRuntime.EnumerateSyncBlockCleanupData()</c> (count only).</summary>
        public void AddSyncBlockCleanupEntry()
        {
            SyncBlockCleanupBacklogCount++;
        }

        /// <summary>
        /// Called at most once (the caller stops invoking per-object detection afterwards) when
        /// resolving a CCW/RCW for one heap object throws — most likely the cached ClrMD
        /// <c>SyncBlockContainer</c> lookup itself failed (e.g. a corrupted dump), which would
        /// otherwise retry the same failing, expensive enumeration for every remaining object.
        /// Counts/types aggregated before the failure remain exact lower bounds.
        /// </summary>
        public void MarkDetectionUnavailable(string reason)
        {
            _detectionUnavailableNote ??=
                $"COM wrapper detection stopped partway through the heap walk ({reason}); " +
                "counts above only cover objects visited before the failure and are a lower bound.";
        }

        public ComWrappersView BuildView()
        {
            var topCcw = _ccwTypes.Values
                .OrderByDescending(static s => s.TotalRefCount)
                .ThenByDescending(static s => s.Count)
                .ThenBy(static s => s.TypeFullName, StringComparer.Ordinal)
                .Take(_topTypesPerKind)
                .Select(static s => new CcwTypeStat(s.TypeFullName, s.Count, s.TotalRefCount, s.MaxRefCount, s.TotalInterfaceCount, s.Identity))
                .ToImmutableArray();

            var topRcw = _rcwTypes.Values
                .OrderByDescending(static s => s.TotalRefCount)
                .ThenByDescending(static s => s.Count)
                .ThenBy(static s => s.TypeFullName, StringComparer.Ordinal)
                .Take(_topTypesPerKind)
                .Select(static s => new RcwTypeStat(s.TypeFullName, s.Count, s.TotalRefCount, s.MaxRefCount, s.DisconnectedCount, s.WinRtObjectCount, s.Identity))
                .ToImmutableArray();

            var notes = new List<string>();
            if (_detectionUnavailableNote is not null)
            {
                notes.Add(_detectionUnavailableNote);
            }

            if (RcwDisconnectedCount > 0)
            {
                notes.Add(
                    $"{RcwDisconnectedCount:N0} RCW(s) are disconnected from their underlying COM object " +
                    "but still referenced from the managed heap — a likely leak candidate " +
                    "(missing Marshal.ReleaseComObject / Dispose on the RCW-wrapping managed object).");
            }

            if (RcwCleanupBacklogCount > _rcwCleanupSample.Count)
            {
                notes.Add(
                    $"RCW cleanup-backlog sample is truncated to the first {MaxCleanupBacklogSample} of " +
                    $"{RcwCleanupBacklogCount:N0} total pending entries; the count above is exact.");
            }

            return new ComWrappersView(
                CcwCount,
                CcwTotalRefCount,
                topCcw,
                RcwCount,
                RcwTotalRefCount,
                RcwDisconnectedCount,
                RcwWinRtObjectCount,
                topRcw,
                RcwCleanupBacklogCount,
                _rcwCleanupSample.ToImmutableArray(),
                SyncBlockCleanupBacklogCount,
                notes.ToImmutableArray());
        }

        private static ComWrapperTypeKey BuildKey(string typeFullName, TypeIdentity? identity) => new(
            typeFullName,
            identity?.ModuleVersionId,
            identity?.MetadataToken,
            identity?.ModuleName,
            identity?.ModulePath);
    }

    private readonly record struct ComWrapperTypeKey(
        string TypeFullName,
        Guid? ModuleVersionId,
        int? MetadataToken,
        string? ModuleName,
        string? ModulePath);

    private sealed class RawCcwTypeStat
    {
        public RawCcwTypeStat(string typeFullName, TypeIdentity? identity)
        {
            TypeFullName = typeFullName;
            Identity = identity;
        }

        public string TypeFullName { get; }
        public TypeIdentity? Identity { get; }
        public int Count;
        public long TotalRefCount;
        public long MaxRefCount;
        public long TotalInterfaceCount;
    }

    private sealed class RawRcwTypeStat
    {
        public RawRcwTypeStat(string typeFullName, TypeIdentity? identity)
        {
            TypeFullName = typeFullName;
            Identity = identity;
        }

        public string TypeFullName { get; }
        public TypeIdentity? Identity { get; }
        public int Count;
        public long TotalRefCount;
        public long MaxRefCount;
        public int DisconnectedCount;
        public int WinRtObjectCount;
    }
}
