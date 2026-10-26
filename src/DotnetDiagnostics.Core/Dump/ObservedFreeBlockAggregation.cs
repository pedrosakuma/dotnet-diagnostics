namespace DotnetDiagnostics.Core.Dump;

/// <summary>
/// Retains the largest directly observed material free records with O(log K) insertion work and
/// O(K) storage. The CLR's aggregate free-object count remains a separate compatibility field.
/// </summary>
internal sealed class ObservedFreeBlockAggregation
{
    internal const int TopRangeLimit = 8;
    internal const long MaterialBlockMinimumBytes = 4 * 1024;

    private const string ConclusionLimitation =
        "Observed free blocks are ClrMD obj.IsFree records; they do not establish allocation success, object liveness, retained size, or pinned-object concentration.";
    private const string IndeterminateLimitation =
        "ClrMD exposes no formal per-segment walk-completion signal, so absence of additional free blocks is indeterminate.";

    private readonly ObservedFreeBlockRange[] _heap;
    private int _count;

    internal ObservedFreeBlockAggregation(int capacity = TopRangeLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _heap = new ObservedFreeBlockRange[capacity];
    }

    internal int Capacity => _heap.Length;
    internal int RetainedCount => _count;
    internal long ObservedCount { get; private set; }
    internal long OmittedCount => ObservedCount - _count;
    internal long LargestObservedBytes { get; private set; }

    internal void Observe(ulong start, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (length < MaterialBlockMinimumBytes)
        {
            return;
        }

        var range = new ObservedFreeBlockRange(
            start,
            checked(start + (ulong)length),
            length);
        ObservedCount++;
        LargestObservedBytes = Math.Max(LargestObservedBytes, length);

        if (_heap.Length == 0)
        {
            return;
        }

        if (_count < _heap.Length)
        {
            _heap[_count] = range;
            SiftUp(_count);
            _count++;
            return;
        }

        if (!IsBetter(range, _heap[0]))
        {
            return;
        }

        _heap[0] = range;
        SiftDown(0);
    }

    internal IReadOnlyList<ObservedFreeBlockRange> Snapshot()
    {
        var result = new ObservedFreeBlockRange[_count];
        Array.Copy(_heap, result, _count);
        Array.Sort(result, CompareBestFirst);
        return result;
    }

    internal SegmentStat ApplyTo(
        SegmentStat segment,
        long unresolvedObjectCount,
        bool walkCompletionEstablished = false)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentOutOfRangeException.ThrowIfNegative(unresolvedObjectCount);

        var state = unresolvedObjectCount > 0
            ? ObservedFreeBlockEvidenceState.Partial
            : walkCompletionEstablished
                ? ObservedFreeBlockEvidenceState.Complete
                : ObservedFreeBlockEvidenceState.Indeterminate;
        IReadOnlyList<string> limitations = state == ObservedFreeBlockEvidenceState.Partial
            ?
            [
                $"The segment walk skipped {unresolvedObjectCount:N0} unresolved object(s); observed free-block evidence is partial.",
                ConclusionLimitation,
            ]
            :
            [
                state == ObservedFreeBlockEvidenceState.Indeterminate
                    ? IndeterminateLimitation
                    : "The producer explicitly established completion for this segment projection.",
                ConclusionLimitation,
            ];

        return segment with
        {
            LargestObservedMaterialFreeBlockBytes = LargestObservedBytes,
            ObservedMaterialFreeBlockCount = ObservedCount,
            RetainedMaterialFreeBlockCount = RetainedCount,
            OmittedMaterialFreeBlockCount = OmittedCount,
            MaterialFreeBlockRetentionLimit = Capacity,
            MaterialFreeBlockMinimumBytes = MaterialBlockMinimumBytes,
            TopObservedMaterialFreeBlocks = Snapshot(),
            FreeBlockEvidenceState = state,
            FreeBlockEvidenceLimitations = limitations,
        };
    }

    private void SiftUp(int index)
    {
        while (index > 0)
        {
            var parent = (index - 1) / 2;
            if (!IsWorse(_heap[index], _heap[parent]))
            {
                return;
            }

            (_heap[parent], _heap[index]) = (_heap[index], _heap[parent]);
            index = parent;
        }
    }

    private void SiftDown(int index)
    {
        while (true)
        {
            var left = checked(index * 2 + 1);
            if (left >= _count)
            {
                return;
            }

            var right = left + 1;
            var worse = right < _count && IsWorse(_heap[right], _heap[left]) ? right : left;
            if (!IsWorse(_heap[worse], _heap[index]))
            {
                return;
            }

            (_heap[index], _heap[worse]) = (_heap[worse], _heap[index]);
            index = worse;
        }
    }

    private static int CompareBestFirst(ObservedFreeBlockRange left, ObservedFreeBlockRange right)
    {
        var byLength = right.Length.CompareTo(left.Length);
        return byLength != 0 ? byLength : left.Start.CompareTo(right.Start);
    }

    private static bool IsBetter(ObservedFreeBlockRange candidate, ObservedFreeBlockRange existing) =>
        candidate.Length > existing.Length
        || (candidate.Length == existing.Length && candidate.Start < existing.Start);

    private static bool IsWorse(ObservedFreeBlockRange candidate, ObservedFreeBlockRange existing) =>
        candidate.Length < existing.Length
        || (candidate.Length == existing.Length && candidate.Start > existing.Start);
}
