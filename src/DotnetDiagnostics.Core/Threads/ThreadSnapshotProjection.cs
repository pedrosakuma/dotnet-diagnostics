namespace DotnetDiagnostics.Core.Threads;

/// <summary>
/// Builds bounded, decision-oriented thread projections while leaving the complete snapshot
/// unchanged behind its handle.
/// </summary>
public static class ThreadSnapshotProjection
{
    public const int SummaryThreadLimit = 6;
    public const int SummaryFrameLimit = 6;
    public const int DetailThreadLimit = 8;
    public const int DetailFrameLimit = 7;
    public const int QueryThreadLimit = 8;
    public const int QueryFrameLimit = 8;
    public const int DetailLockLimit = 12;
    public const int LockWaiterIdLimit = 8;

    public static BoundedProjectionPage<ManagedThread> ProjectThreads(
        ThreadSnapshotArtifact snapshot,
        int requestedCount,
        int hardThreadLimit,
        int frameLimit,
        bool blockedOnly = false,
        int offset = 0)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var blockedCandidateCount = blockedOnly
            ? snapshot.Threads.Count(static thread => thread.IsLikelyBlocked || thread.IsLockWaiter)
            : snapshot.Threads.Count;
        var usedFallback = blockedOnly && blockedCandidateCount == 0;
        var totalItems = usedFallback || !blockedOnly
            ? snapshot.Threads.Count
            : blockedCandidateCount;
        var limit = Math.Min(requestedCount, hardThreadLimit);
        var items = SelectThreadWindow(
            snapshot.Threads,
            blockedOnly && !usedFallback,
            offset,
            limit,
            frameLimit);
        int? nextOffset = offset + items.Length < totalItems
            ? offset + items.Length
            : null;
        return new BoundedProjectionPage<ManagedThread>(items, totalItems, offset, nextOffset)
        {
            UsedFallback = usedFallback,
        };
    }

    public static BoundedProjectionPage<MonitorLockState> ProjectLocks(
        ThreadSnapshotArtifact snapshot,
        int requestedCount,
        int hardLimit,
        int offset = 0)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var limit = Math.Min(requestedCount, hardLimit);
        var items = SelectLockWindow(snapshot.Locks, offset, limit);
        int? nextOffset = offset + items.Length < snapshot.Locks.Count
            ? offset + items.Length
            : null;
        return new BoundedProjectionPage<MonitorLockState>(items, snapshot.Locks.Count, offset, nextOffset);
    }

    public static ProjectedLockWaiterPage ProjectLock(MonitorLockState lockState, int waiterOffset)
    {
        ArgumentNullException.ThrowIfNull(lockState);
        ArgumentOutOfRangeException.ThrowIfNegative(waiterOffset);
        var count = Math.Min(
            LockWaiterIdLimit,
            Math.Max(0, lockState.WaitingManagedThreadIds.Count - waiterOffset));
        var waiterIds = new int[count];
        for (var i = 0; i < count; i++)
        {
            waiterIds[i] = lockState.WaitingManagedThreadIds[waiterOffset + i];
        }
        int? nextOffset = waiterOffset + waiterIds.Length < lockState.WaitingManagedThreadIds.Count
            ? waiterOffset + waiterIds.Length
            : null;
        var projected = lockState with
        {
            WaitingManagedThreadIds = waiterIds,
            TotalWaitingManagedThreadIds = lockState.WaitingManagedThreadIds.Count,
            OmittedWaitingManagedThreadIds = lockState.WaitingManagedThreadIds.Count - waiterIds.Length,
        };
        return new ProjectedLockWaiterPage(projected, waiterOffset, nextOffset);
    }

    public static MonitorLockState? FindLock(ThreadSnapshotArtifact snapshot, ulong objectAddress)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Locks.FirstOrDefault(lockState => lockState.ObjectAddress == objectAddress);
    }

    private static int Rank(ManagedThread thread)
    {
        if (thread.IsDeadlockCandidate) return 0;
        if (thread.IsContendedLockOwner) return 1;
        if (!string.IsNullOrEmpty(thread.CurrentExceptionType)) return 2;
        if (!thread.IsLikelyBlocked) return 3;
        if (thread.IsLockWaiter) return 4;
        return IsGenericWaitingFrame(thread.TopFrameMethod) ? 6 : 5;
    }

    private static bool IsGenericWaitingFrame(string? method)
    {
        if (string.IsNullOrEmpty(method)) return true;

        return method.Contains("Wait", StringComparison.OrdinalIgnoreCase)
            || method.Contains("Park", StringComparison.OrdinalIgnoreCase)
            || method.Contains("Sleep", StringComparison.OrdinalIgnoreCase)
            || method.Contains("Poll", StringComparison.OrdinalIgnoreCase)
            || method.Contains("Epoll", StringComparison.OrdinalIgnoreCase)
            || method.Contains("Futex", StringComparison.OrdinalIgnoreCase);
    }

    private static ManagedThread Compact(ManagedThread thread, int frameLimit)
        => thread with { Frames = thread.Frames.Take(frameLimit).ToArray() };

    private static MonitorLockState Compact(MonitorLockState lockState)
        => ProjectLock(lockState, waiterOffset: 0).Lock;

    private readonly record struct RankedThread(ManagedThread Thread, int Rank, int OriginalIndex);

    private readonly record struct RankedLock(MonitorLockState Lock, int OriginalIndex);

    private static ManagedThread[] SelectThreadWindow(
        IReadOnlyList<ManagedThread> threads,
        bool blockedOnly,
        int offset,
        int limit,
        int frameLimit)
    {
        var result = new List<ManagedThread>(limit);
        RankedThread? cursor = null;
        var end = (long)offset + limit;
        for (long position = 0; position < end; position++)
        {
            RankedThread? best = null;
            for (var originalIndex = 0; originalIndex < threads.Count; originalIndex++)
            {
                var thread = threads[originalIndex];
                if (blockedOnly && !thread.IsLikelyBlocked && !thread.IsLockWaiter)
                {
                    continue;
                }

                var candidate = new RankedThread(thread, Rank(thread), originalIndex);
                if (cursor is { } previous && Compare(candidate, previous) <= 0)
                {
                    continue;
                }
                if (best is null || Compare(candidate, best.Value) < 0)
                {
                    best = candidate;
                }
            }

            if (best is not { } selected)
            {
                break;
            }
            cursor = selected;
            if (position >= offset)
            {
                result.Add(Compact(selected.Thread, frameLimit));
            }
        }

        return result.ToArray();
    }

    private static MonitorLockState[] SelectLockWindow(
        IReadOnlyList<MonitorLockState> locks,
        int offset,
        int limit)
    {
        var result = new List<MonitorLockState>(limit);
        RankedLock? cursor = null;
        var end = (long)offset + limit;
        for (long position = 0; position < end; position++)
        {
            RankedLock? best = null;
            for (var originalIndex = 0; originalIndex < locks.Count; originalIndex++)
            {
                var candidate = new RankedLock(locks[originalIndex], originalIndex);
                if (cursor is { } previous && Compare(candidate, previous) <= 0)
                {
                    continue;
                }
                if (best is null || Compare(candidate, best.Value) < 0)
                {
                    best = candidate;
                }
            }

            if (best is not { } selected)
            {
                break;
            }
            cursor = selected;
            if (position >= offset)
            {
                result.Add(Compact(selected.Lock));
            }
        }

        return result.ToArray();
    }

    private static int Compare(RankedThread left, RankedThread right)
    {
        var result = left.Rank.CompareTo(right.Rank);
        if (result != 0) return result;
        result = right.Thread.LockCount.CompareTo(left.Thread.LockCount);
        if (result != 0) return result;
        result = right.Thread.Frames.Count.CompareTo(left.Thread.Frames.Count);
        if (result != 0) return result;
        result = left.Thread.ManagedThreadId.CompareTo(right.Thread.ManagedThreadId);
        return result != 0 ? result : left.OriginalIndex.CompareTo(right.OriginalIndex);
    }

    private static int Compare(RankedLock left, RankedLock right)
    {
        var result = right.Lock.IsContended.CompareTo(left.Lock.IsContended);
        if (result != 0) return result;
        result = right.Lock.WaitingThreadCount.CompareTo(left.Lock.WaitingThreadCount);
        if (result != 0) return result;
        result = right.Lock.RecursionCount.CompareTo(left.Lock.RecursionCount);
        if (result != 0) return result;
        result = left.Lock.ObjectAddress.CompareTo(right.Lock.ObjectAddress);
        return result != 0 ? result : left.OriginalIndex.CompareTo(right.OriginalIndex);
    }
}

public sealed record BoundedProjectionPage<T>(
    IReadOnlyList<T> Items,
    int TotalItems,
    int Offset,
    int? NextOffset)
{
    public bool UsedFallback { get; init; }
}

public sealed record ProjectedLockWaiterPage(
    MonitorLockState Lock,
    int WaiterOffset,
    int? NextWaiterOffset);
