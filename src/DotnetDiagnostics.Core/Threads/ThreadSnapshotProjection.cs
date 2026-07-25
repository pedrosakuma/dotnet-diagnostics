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

        var lockOwners = snapshot.Locks
            .Where(static lockState => lockState.IsContended)
            .Select(static lockState => lockState.OwnerManagedThreadId)
            .Where(static id => id > 0)
            .ToHashSet();
        var lockWaiters = snapshot.Locks
            .SelectMany(static lockState => lockState.WaitingManagedThreadIds)
            .Where(static id => id > 0)
            .ToHashSet();
        var cycleCandidates = lockOwners
            .Where(lockWaiters.Contains)
            .ToHashSet();

        var candidates = blockedOnly
            ? snapshot.Threads
                .Where(thread => thread.IsLikelyBlocked || lockWaiters.Contains(thread.ManagedThreadId))
                .ToArray()
            : snapshot.Threads;
        if (blockedOnly && candidates.Count == 0)
        {
            candidates = snapshot.Threads.ToArray();
        }

        var limit = Math.Min(requestedCount, hardThreadLimit);
        var ordered = candidates
            .OrderBy(thread => Rank(thread, cycleCandidates, lockOwners, lockWaiters))
            .ThenByDescending(static thread => thread.LockCount)
            .ThenByDescending(static thread => thread.Frames.Count)
            .ThenBy(static thread => thread.ManagedThreadId);
        var items = ordered
            .Skip(offset)
            .Take(limit)
            .Select(thread => Compact(thread, frameLimit))
            .ToArray();
        int? nextOffset = offset + items.Length < candidates.Count
            ? offset + items.Length
            : null;
        return new BoundedProjectionPage<ManagedThread>(items, candidates.Count, offset, nextOffset);
    }

    public static BoundedProjectionPage<MonitorLockState> ProjectLocks(
        ThreadSnapshotArtifact snapshot,
        int requestedCount,
        int hardLimit,
        int offset = 0)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var items = snapshot.Locks
            .OrderByDescending(static lockState => lockState.IsContended)
            .ThenByDescending(static lockState => lockState.WaitingThreadCount)
            .ThenByDescending(static lockState => lockState.RecursionCount)
            .ThenBy(static lockState => lockState.ObjectAddress)
            .Skip(offset)
            .Take(Math.Min(requestedCount, hardLimit))
            .Select(Compact)
            .ToArray();
        int? nextOffset = offset + items.Length < snapshot.Locks.Count
            ? offset + items.Length
            : null;
        return new BoundedProjectionPage<MonitorLockState>(items, snapshot.Locks.Count, offset, nextOffset);
    }

    public static ProjectedLockWaiterPage ProjectLock(MonitorLockState lockState, int waiterOffset)
    {
        ArgumentNullException.ThrowIfNull(lockState);
        var waiterIds = lockState.WaitingManagedThreadIds
            .Skip(waiterOffset)
            .Take(LockWaiterIdLimit)
            .ToArray();
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

    private static int Rank(
        ManagedThread thread,
        HashSet<int> cycleCandidates,
        HashSet<int> lockOwners,
        HashSet<int> lockWaiters)
    {
        if (cycleCandidates.Contains(thread.ManagedThreadId)) return 0;
        if (lockOwners.Contains(thread.ManagedThreadId)) return 1;
        if (!string.IsNullOrEmpty(thread.CurrentExceptionType)) return 2;
        if (!thread.IsLikelyBlocked) return 3;
        if (lockWaiters.Contains(thread.ManagedThreadId)) return 4;
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
}

public sealed record BoundedProjectionPage<T>(
    IReadOnlyList<T> Items,
    int TotalItems,
    int Offset,
    int? NextOffset);

public sealed record ProjectedLockWaiterPage(
    MonitorLockState Lock,
    int WaiterOffset,
    int? NextWaiterOffset);
