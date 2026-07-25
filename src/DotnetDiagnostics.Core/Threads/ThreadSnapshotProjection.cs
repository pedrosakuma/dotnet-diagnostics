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

    public static IReadOnlyList<ManagedThread> SelectThreads(
        ThreadSnapshotArtifact snapshot,
        int requestedCount,
        int hardThreadLimit,
        int frameLimit,
        bool blockedOnly = false)
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
        return candidates
            .OrderBy(thread => Rank(thread, cycleCandidates, lockOwners, lockWaiters))
            .ThenByDescending(static thread => thread.LockCount)
            .ThenByDescending(static thread => thread.Frames.Count)
            .ThenBy(static thread => thread.ManagedThreadId)
            .Take(limit)
            .Select(thread => Compact(thread, frameLimit))
            .ToArray();
    }

    public static IReadOnlyList<MonitorLockState> SelectLocks(
        ThreadSnapshotArtifact snapshot,
        int requestedCount,
        int hardLimit)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Locks
            .OrderByDescending(static lockState => lockState.IsContended)
            .ThenByDescending(static lockState => lockState.WaitingThreadCount)
            .ThenByDescending(static lockState => lockState.RecursionCount)
            .ThenBy(static lockState => lockState.ObjectAddress)
            .Take(Math.Min(requestedCount, hardLimit))
            .ToArray();
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
}
