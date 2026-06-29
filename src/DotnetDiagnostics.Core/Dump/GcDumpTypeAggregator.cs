using System.Globalization;

namespace DotnetDiagnostics.Core.Dump;

/// <summary>
/// Folds the per-object <c>GCBulkNode</c> stream and the <c>GCBulkType</c> name table emitted by an
/// induced GC heap dump into per-type instance counts and byte totals, then projects the canonical
/// <see cref="HeapSnapshotArtifact"/>. Kept transport-agnostic so it can be unit-tested with a
/// synthetic node/type stream (no live process required).
/// </summary>
internal sealed class GcDumpTypeAggregator
{
    private readonly Dictionary<ulong, string> _typeNames = new();
    private readonly Dictionary<ulong, Stat> _stats = new();
    private long _totalBytes;
    private long _nodeCount;

    private sealed class Stat
    {
        public long Count;
        public long Bytes;
    }

    /// <summary>Total bytes accumulated across all observed objects.</summary>
    public long TotalBytes => _totalBytes;

    /// <summary>Number of objects observed.</summary>
    public long NodeCount => _nodeCount;

    /// <summary>Registers a managed type name keyed by its EventPipe type id. Last write wins.</summary>
    public void RegisterType(ulong typeId, string typeName)
    {
        if (!string.IsNullOrEmpty(typeName))
        {
            _typeNames[typeId] = typeName;
        }
    }

    /// <summary>Accumulates one object of <paramref name="typeId"/> with the given size in bytes.</summary>
    public void AddNode(ulong typeId, ulong size)
    {
        if (!_stats.TryGetValue(typeId, out var stat))
        {
            stat = new Stat();
            _stats[typeId] = stat;
        }

        var bytes = unchecked((long)size);
        stat.Count++;
        stat.Bytes += bytes;
        _totalBytes += bytes;
        _nodeCount++;
    }

    /// <summary>Projects the top-N type lists. The instance summary mirrors the bytes summary so
    /// both views are directly comparable with the ClrMD-backed inspectors.</summary>
    public (IReadOnlyList<TypeStat> ByBytes, IReadOnlyList<TypeStat> ByInstances) Project(int snapshotTopTypes)
    {
        var byBytes = _stats
            .OrderByDescending(kv => kv.Value.Bytes)
            .Take(snapshotTopTypes)
            .Select(kv => ToTypeStat(kv.Key, kv.Value))
            .ToArray();

        var byInstances = _stats
            .OrderByDescending(kv => kv.Value.Count)
            .Take(snapshotTopTypes)
            .Select(kv => ToTypeStat(kv.Key, kv.Value))
            .ToArray();

        return (byBytes, byInstances);
    }

    private TypeStat ToTypeStat(ulong typeId, Stat stat)
    {
        var name = _typeNames.TryGetValue(typeId, out var n)
            ? n
            : "0x" + typeId.ToString("X", CultureInfo.InvariantCulture);
        var pct = _totalBytes > 0 ? Math.Round(100.0 * stat.Bytes / _totalBytes, 2) : 0.0;
        return new TypeStat(
            TypeFullName: name,
            ModuleName: null,
            InstanceCount: stat.Count,
            TotalBytes: stat.Bytes,
            TotalBytesPercent: pct);
    }
}
