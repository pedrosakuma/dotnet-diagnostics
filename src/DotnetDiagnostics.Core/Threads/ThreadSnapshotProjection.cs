using System.Runtime.CompilerServices;

namespace DotnetDiagnostics.Core.Threads;

/// <summary>
/// Builds bounded, decision-oriented thread projections while leaving the complete snapshot
/// unchanged behind its handle.
/// </summary>
public static class ThreadSnapshotProjection
{
    private static readonly ConditionalWeakTable<ThreadSnapshotArtifact, ThreadSnapshotProjectionIndex> Indexes = new();

    public const int SummaryThreadLimit = 6;
    public const int SummaryFrameLimit = 6;
    public const int DetailThreadLimit = 8;
    public const int DetailFrameLimit = 7;
    public const int QueryThreadLimit = 8;
    public const int QueryFrameLimit = 8;
    public const int DetailLockLimit = 12;
    public const int LockWaiterIdLimit = 8;

    /// <summary>
    /// Builds the stable ranking index once while the capture is being registered. Query pages then
    /// allocate only their bounded rows instead of rebuilding full waiter sets and sorts.
    /// </summary>
    public static void Prepare(ThreadSnapshotArtifact snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _ = Indexes.GetValue(snapshot, static artifact => ThreadSnapshotProjectionIndex.Create(artifact));
    }

    public static BoundedProjectionPage<ManagedThread> ProjectThreads(
        ThreadSnapshotArtifact snapshot,
        int requestedCount,
        int hardThreadLimit,
        int frameLimit,
        bool blockedOnly = false,
        int offset = 0)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var index = GetIndex(snapshot);
        var candidates = blockedOnly ? index.BlockedThreads : index.AllThreads;
        var limit = Math.Min(requestedCount, hardThreadLimit);
        var count = Math.Min(limit, Math.Max(0, candidates.Length - offset));
        var items = new ManagedThread[count];
        for (var i = 0; i < count; i++)
        {
            items[i] = Compact(candidates[offset + i].Thread, frameLimit);
        }
        int? nextOffset = offset + items.Length < candidates.Length
            ? offset + items.Length
            : null;
        return new BoundedProjectionPage<ManagedThread>(items, candidates.Length, offset, nextOffset)
        {
            UsedFallback = blockedOnly && index.BlockedUsesFallback,
        };
    }

    public static BoundedProjectionPage<MonitorLockState> ProjectLocks(
        ThreadSnapshotArtifact snapshot,
        int requestedCount,
        int hardLimit,
        int offset = 0)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var locks = GetIndex(snapshot).Locks;
        var limit = Math.Min(requestedCount, hardLimit);
        var count = Math.Min(limit, Math.Max(0, locks.Length - offset));
        var items = new MonitorLockState[count];
        for (var i = 0; i < count; i++)
        {
            items[i] = Compact(locks[offset + i].Lock);
        }
        int? nextOffset = offset + items.Length < locks.Length
            ? offset + items.Length
            : null;
        return new BoundedProjectionPage<MonitorLockState>(items, locks.Length, offset, nextOffset);
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
        return GetIndex(snapshot).LocksByAddress.GetValueOrDefault(objectAddress);
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

    private static ThreadSnapshotProjectionIndex GetIndex(ThreadSnapshotArtifact snapshot)
        => Indexes.TryGetValue(snapshot, out var index)
            ? index
            : throw new InvalidOperationException(
                "Thread snapshot projection index is not prepared. Call ThreadSnapshotProjection.Prepare when the artifact is captured or registered.");

    private readonly record struct RankedThread(ManagedThread Thread, int Rank, int OriginalIndex);

    private readonly record struct RankedLock(MonitorLockState Lock, int OriginalIndex);

    private sealed class ThreadSnapshotProjectionIndex
    {
        private ThreadSnapshotProjectionIndex(
            RankedThread[] allThreads,
            RankedThread[] blockedThreads,
            bool blockedUsesFallback,
            RankedLock[] locks,
            IReadOnlyDictionary<ulong, MonitorLockState> locksByAddress)
        {
            AllThreads = allThreads;
            BlockedThreads = blockedThreads;
            BlockedUsesFallback = blockedUsesFallback;
            Locks = locks;
            LocksByAddress = locksByAddress;
        }

        public RankedThread[] AllThreads { get; }
        public RankedThread[] BlockedThreads { get; }
        public bool BlockedUsesFallback { get; }
        public RankedLock[] Locks { get; }
        public IReadOnlyDictionary<ulong, MonitorLockState> LocksByAddress { get; }

        public static ThreadSnapshotProjectionIndex Create(ThreadSnapshotArtifact snapshot)
        {
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

            var allThreads = snapshot.Threads
                .Select((thread, originalIndex) => new RankedThread(
                    thread,
                    Rank(thread, cycleCandidates, lockOwners, lockWaiters),
                    originalIndex))
                .ToArray();
            Array.Sort(allThreads, static (left, right) =>
            {
                var result = left.Rank.CompareTo(right.Rank);
                if (result != 0) return result;
                result = right.Thread.LockCount.CompareTo(left.Thread.LockCount);
                if (result != 0) return result;
                result = right.Thread.Frames.Count.CompareTo(left.Thread.Frames.Count);
                if (result != 0) return result;
                result = left.Thread.ManagedThreadId.CompareTo(right.Thread.ManagedThreadId);
                return result != 0 ? result : left.OriginalIndex.CompareTo(right.OriginalIndex);
            });

            var blockedThreads = allThreads
                .Where(candidate =>
                    candidate.Thread.IsLikelyBlocked ||
                    lockWaiters.Contains(candidate.Thread.ManagedThreadId))
                .ToArray();
            var blockedUsesFallback = blockedThreads.Length == 0;
            if (blockedUsesFallback)
            {
                blockedThreads = allThreads;
            }

            var locks = snapshot.Locks
                .Select((lockState, originalIndex) => new RankedLock(lockState, originalIndex))
                .ToArray();
            Array.Sort(locks, static (left, right) =>
            {
                var result = right.Lock.IsContended.CompareTo(left.Lock.IsContended);
                if (result != 0) return result;
                result = right.Lock.WaitingThreadCount.CompareTo(left.Lock.WaitingThreadCount);
                if (result != 0) return result;
                result = right.Lock.RecursionCount.CompareTo(left.Lock.RecursionCount);
                if (result != 0) return result;
                result = left.Lock.ObjectAddress.CompareTo(right.Lock.ObjectAddress);
                return result != 0 ? result : left.OriginalIndex.CompareTo(right.OriginalIndex);
            });
            var locksByAddress = new Dictionary<ulong, MonitorLockState>();
            foreach (var lockState in snapshot.Locks)
            {
                locksByAddress.TryAdd(lockState.ObjectAddress, lockState);
            }

            return new ThreadSnapshotProjectionIndex(allThreads, blockedThreads, blockedUsesFallback, locks, locksByAddress);
        }
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
