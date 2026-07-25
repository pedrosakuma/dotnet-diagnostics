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
    public const int MaxDirectOffset = 256;

    public static BoundedProjectionPage<ManagedThread> ProjectThreads(
        ThreadSnapshotArtifact snapshot,
        int requestedCount,
        int hardThreadLimit,
        int frameLimit,
        bool blockedOnly = false,
        int offset = 0)
        => ProjectThreads(snapshot, requestedCount, hardThreadLimit, frameLimit, blockedOnly, offset, null, null);

    public static BoundedProjectionPage<ManagedThread> ProjectThreads(
        ThreadSnapshotArtifact snapshot,
        int requestedCount,
        int hardThreadLimit,
        int frameLimit,
        bool blockedOnly,
        int offset,
        string? handle,
        string? cursor)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidatePagingArguments(offset, handle, cursor);

        var blockedCandidateCount = blockedOnly
            ? snapshot.Threads.Count(static thread => thread.IsLikelyBlocked || thread.IsLockWaiter)
            : snapshot.Threads.Count;
        var usedFallback = blockedOnly && blockedCandidateCount == 0;
        var totalItems = usedFallback || !blockedOnly
            ? snapshot.Threads.Count
            : blockedCandidateCount;
        var actualBlockedOnly = blockedOnly && !usedFallback;
        RankedThread? after = null;
        var pageOffset = offset;
        if (cursor is not null)
        {
            if (!ThreadSnapshotCursorCodec.TryDecodeThread(
                    cursor,
                    handle!,
                    blockedOnly,
                    usedFallback,
                    out var decoded,
                    out var error))
            {
                throw new ThreadSnapshotCursorException(error);
            }
            after = new RankedThread(
                Thread: null!,
                decoded.Rank,
                decoded.LockCount,
                decoded.FrameCount,
                decoded.ManagedThreadId,
                decoded.OriginalIndex);
            if (!ValidateThreadCursor(snapshot.Threads, actualBlockedOnly, after.Value, decoded.Position))
            {
                throw new ThreadSnapshotCursorException("cursor no longer matches the retained thread snapshot");
            }
            pageOffset = decoded.Position;
        }

        if (pageOffset >= totalItems)
        {
            return new BoundedProjectionPage<ManagedThread>(
                Array.Empty<ManagedThread>(),
                totalItems,
                pageOffset,
                NextOffset: null,
                NextCursor: null)
            {
                UsedFallback = usedFallback,
            };
        }
        var limit = Math.Min(requestedCount, hardThreadLimit);
        var selection = SelectThreadWindow(
            snapshot.Threads,
            actualBlockedOnly,
            cursor is null ? offset : 0,
            limit,
            frameLimit,
            after);
        int? nextOffset = pageOffset + selection.Items.Length < totalItems
            ? pageOffset + selection.Items.Length
            : null;
        var nextCursor = nextOffset is not null && handle is not null && selection.Last is { } last
            ? ThreadSnapshotCursorCodec.EncodeThread(
                handle,
                new ThreadSnapshotCursorCodec.ThreadCursor(
                    blockedOnly,
                    usedFallback,
                    nextOffset.Value,
                    last.Rank,
                    last.LockCount,
                    last.FrameCount,
                    last.ManagedThreadId,
                    last.OriginalIndex))
            : null;
        return new BoundedProjectionPage<ManagedThread>(
            selection.Items,
            totalItems,
            pageOffset,
            nextOffset,
            nextCursor)
        {
            UsedFallback = usedFallback,
        };
    }

    public static BoundedProjectionPage<MonitorLockState> ProjectLocks(
        ThreadSnapshotArtifact snapshot,
        int requestedCount,
        int hardLimit,
        int offset = 0)
        => ProjectLocks(snapshot, requestedCount, hardLimit, offset, null, null);

    public static BoundedProjectionPage<MonitorLockState> ProjectLocks(
        ThreadSnapshotArtifact snapshot,
        int requestedCount,
        int hardLimit,
        int offset,
        string? handle,
        string? cursor)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidatePagingArguments(offset, handle, cursor);
        RankedLock? after = null;
        var pageOffset = offset;
        if (cursor is not null)
        {
            if (!ThreadSnapshotCursorCodec.TryDecodeLock(cursor, handle!, out var decoded, out var error))
            {
                throw new ThreadSnapshotCursorException(error);
            }
            after = new RankedLock(
                Lock: null!,
                decoded.IsContended,
                decoded.WaitingThreadCount,
                decoded.RecursionCount,
                decoded.ObjectAddress,
                decoded.OriginalIndex);
            if (!ValidateLockCursor(snapshot.Locks, after.Value, decoded.Position))
            {
                throw new ThreadSnapshotCursorException("cursor no longer matches the retained lock snapshot");
            }
            pageOffset = decoded.Position;
        }

        if (pageOffset >= snapshot.Locks.Count)
        {
            return new BoundedProjectionPage<MonitorLockState>(
                Array.Empty<MonitorLockState>(),
                snapshot.Locks.Count,
                pageOffset,
                NextOffset: null,
                NextCursor: null);
        }
        var limit = Math.Min(requestedCount, hardLimit);
        var selection = SelectLockWindow(snapshot.Locks, cursor is null ? offset : 0, limit, after);
        int? nextOffset = pageOffset + selection.Items.Length < snapshot.Locks.Count
            ? pageOffset + selection.Items.Length
            : null;
        var nextCursor = nextOffset is not null && handle is not null && selection.Last is { } last
            ? ThreadSnapshotCursorCodec.EncodeLock(
                handle,
                new ThreadSnapshotCursorCodec.LockCursor(
                    nextOffset.Value,
                    last.IsContended,
                    last.WaitingThreadCount,
                    last.RecursionCount,
                    last.ObjectAddress,
                    last.OriginalIndex))
            : null;
        return new BoundedProjectionPage<MonitorLockState>(
            selection.Items,
            snapshot.Locks.Count,
            pageOffset,
            nextOffset,
            nextCursor);
    }

    public static ProjectedLockWaiterPage ProjectLock(
        MonitorLockState lockState,
        int waiterOffset)
        => ProjectLock(lockState, waiterOffset, LockWaiterIdLimit, null, null);

    public static ProjectedLockWaiterPage ProjectLock(
        MonitorLockState lockState,
        int waiterOffset,
        string? handle,
        string? cursor)
        => ProjectLock(lockState, waiterOffset, LockWaiterIdLimit, handle, cursor);

    public static ProjectedLockWaiterPage ProjectLock(
        MonitorLockState lockState,
        int waiterOffset,
        int requestedCount,
        string? handle,
        string? cursor)
    {
        ArgumentNullException.ThrowIfNull(lockState);
        ArgumentOutOfRangeException.ThrowIfNegative(waiterOffset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestedCount);
        ValidatePagingArguments(waiterOffset, handle, cursor);
        var pageOffset = waiterOffset;
        if (cursor is not null)
        {
            if (!ThreadSnapshotCursorCodec.TryDecodeWaiter(
                    cursor,
                    handle!,
                    lockState.ObjectAddress,
                    out var decoded,
                    out var error))
            {
                throw new ThreadSnapshotCursorException(error);
            }
            if (decoded.Position > lockState.WaitingManagedThreadIds.Count ||
                lockState.WaitingManagedThreadIds[decoded.Position - 1] != decoded.PreviousWaiterId)
            {
                throw new ThreadSnapshotCursorException("cursor no longer matches the retained lock waiter list");
            }
            pageOffset = decoded.Position;
        }

        if (pageOffset >= lockState.WaitingManagedThreadIds.Count)
        {
            return new ProjectedLockWaiterPage(
                lockState with
                {
                    WaitingManagedThreadIds = Array.Empty<int>(),
                    TotalWaitingManagedThreadIds = lockState.WaitingManagedThreadIds.Count,
                    OmittedWaitingManagedThreadIds = lockState.WaitingManagedThreadIds.Count,
                },
                pageOffset,
                NextWaiterOffset: null,
                NextWaiterCursor: null);
        }
        var count = Math.Min(
            Math.Min(requestedCount, LockWaiterIdLimit),
            Math.Max(0, lockState.WaitingManagedThreadIds.Count - pageOffset));
        var waiterIds = new int[count];
        for (var i = 0; i < count; i++)
        {
            waiterIds[i] = lockState.WaitingManagedThreadIds[pageOffset + i];
        }
        int? nextOffset = pageOffset + waiterIds.Length < lockState.WaitingManagedThreadIds.Count
            ? pageOffset + waiterIds.Length
            : null;
        var nextCursor = nextOffset is not null && handle is not null
            ? ThreadSnapshotCursorCodec.EncodeWaiter(
                handle,
                new ThreadSnapshotCursorCodec.WaiterCursor(
                    nextOffset.Value,
                    lockState.ObjectAddress,
                    waiterIds[^1]))
            : null;
        var projected = lockState with
        {
            WaitingManagedThreadIds = waiterIds,
            TotalWaitingManagedThreadIds = lockState.WaitingManagedThreadIds.Count,
            OmittedWaitingManagedThreadIds = lockState.WaitingManagedThreadIds.Count - waiterIds.Length,
        };
        return new ProjectedLockWaiterPage(projected, pageOffset, nextOffset, nextCursor);
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

    private readonly record struct RankedThread(
        ManagedThread Thread,
        int Rank,
        uint LockCount,
        int FrameCount,
        int ManagedThreadId,
        int OriginalIndex);

    private readonly record struct RankedLock(
        MonitorLockState Lock,
        bool IsContended,
        int WaitingThreadCount,
        int RecursionCount,
        ulong ObjectAddress,
        int OriginalIndex);

    private static ThreadSelection SelectThreadWindow(
        IReadOnlyList<ManagedThread> threads,
        bool blockedOnly,
        int offset,
        int limit,
        int frameLimit,
        RankedThread? initialCursor)
    {
        var result = new List<ManagedThread>(limit);
        var cursor = initialCursor;
        RankedThread? last = null;
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

                var candidate = CreateRankedThread(thread, originalIndex);
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
            last = selected;
            if (position >= offset)
            {
                result.Add(Compact(selected.Thread, frameLimit));
            }
        }

        return new ThreadSelection(result.ToArray(), last);
    }

    private static LockSelection SelectLockWindow(
        IReadOnlyList<MonitorLockState> locks,
        int offset,
        int limit,
        RankedLock? initialCursor)
    {
        var result = new List<MonitorLockState>(limit);
        var cursor = initialCursor;
        RankedLock? last = null;
        var end = (long)offset + limit;
        for (long position = 0; position < end; position++)
        {
            RankedLock? best = null;
            for (var originalIndex = 0; originalIndex < locks.Count; originalIndex++)
            {
                var candidate = CreateRankedLock(locks[originalIndex], originalIndex);
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
            last = selected;
            if (position >= offset)
            {
                result.Add(Compact(selected.Lock));
            }
        }

        return new LockSelection(result.ToArray(), last);
    }

    private static int Compare(RankedThread left, RankedThread right)
    {
        var result = left.Rank.CompareTo(right.Rank);
        if (result != 0) return result;
        result = right.LockCount.CompareTo(left.LockCount);
        if (result != 0) return result;
        result = right.FrameCount.CompareTo(left.FrameCount);
        if (result != 0) return result;
        result = left.ManagedThreadId.CompareTo(right.ManagedThreadId);
        return result != 0 ? result : left.OriginalIndex.CompareTo(right.OriginalIndex);
    }

    private static int Compare(RankedLock left, RankedLock right)
    {
        var result = right.IsContended.CompareTo(left.IsContended);
        if (result != 0) return result;
        result = right.WaitingThreadCount.CompareTo(left.WaitingThreadCount);
        if (result != 0) return result;
        result = right.RecursionCount.CompareTo(left.RecursionCount);
        if (result != 0) return result;
        result = left.ObjectAddress.CompareTo(right.ObjectAddress);
        return result != 0 ? result : left.OriginalIndex.CompareTo(right.OriginalIndex);
    }

    private static RankedThread CreateRankedThread(ManagedThread thread, int originalIndex)
        => new(
            thread,
            Rank(thread),
            thread.LockCount,
            thread.Frames.Count,
            thread.ManagedThreadId,
            originalIndex);

    private static RankedLock CreateRankedLock(MonitorLockState lockState, int originalIndex)
        => new(
            lockState,
            lockState.IsContended,
            lockState.WaitingThreadCount,
            lockState.RecursionCount,
            lockState.ObjectAddress,
            originalIndex);

    private static bool ValidateThreadCursor(
        IReadOnlyList<ManagedThread> threads,
        bool blockedOnly,
        RankedThread cursor,
        int expectedPosition)
    {
        if (cursor.OriginalIndex >= threads.Count)
        {
            return false;
        }

        var actual = CreateRankedThread(threads[cursor.OriginalIndex], cursor.OriginalIndex);
        if (Compare(actual, cursor) != 0)
        {
            return false;
        }

        var position = 0;
        for (var index = 0; index < threads.Count; index++)
        {
            var thread = threads[index];
            if (blockedOnly && !thread.IsLikelyBlocked && !thread.IsLockWaiter)
            {
                continue;
            }
            if (Compare(CreateRankedThread(thread, index), cursor) <= 0)
            {
                position++;
            }
        }
        return position == expectedPosition;
    }

    private static bool ValidateLockCursor(
        IReadOnlyList<MonitorLockState> locks,
        RankedLock cursor,
        int expectedPosition)
    {
        if (cursor.OriginalIndex >= locks.Count)
        {
            return false;
        }

        var actual = CreateRankedLock(locks[cursor.OriginalIndex], cursor.OriginalIndex);
        if (Compare(actual, cursor) != 0)
        {
            return false;
        }

        var position = 0;
        for (var index = 0; index < locks.Count; index++)
        {
            if (Compare(CreateRankedLock(locks[index], index), cursor) <= 0)
            {
                position++;
            }
        }
        return position == expectedPosition;
    }

    private static void ValidatePagingArguments(int offset, string? handle, string? cursor)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (cursor is not null)
        {
            if (offset != 0)
            {
                throw new ThreadSnapshotCursorException("cursor and a non-zero offset cannot be combined");
            }
            if (string.IsNullOrWhiteSpace(handle))
            {
                throw new ThreadSnapshotCursorException("cursor pagination requires the snapshot handle");
            }
        }
        else if (offset > MaxDirectOffset)
        {
            throw new ThreadSnapshotDeepOffsetException(offset);
        }
    }

    private sealed record ThreadSelection(ManagedThread[] Items, RankedThread? Last);

    private sealed record LockSelection(MonitorLockState[] Items, RankedLock? Last);
}

public sealed record BoundedProjectionPage<T>(
    IReadOnlyList<T> Items,
    int TotalItems,
    int Offset,
    int? NextOffset,
    string? NextCursor = null)
{
    public BoundedProjectionPage(
        IReadOnlyList<T> items,
        int totalItems,
        int offset,
        int? nextOffset)
        : this(items, totalItems, offset, nextOffset, null)
    {
    }

    public void Deconstruct(
        out IReadOnlyList<T> items,
        out int totalItems,
        out int offset,
        out int? nextOffset)
    {
        items = Items;
        totalItems = TotalItems;
        offset = Offset;
        nextOffset = NextOffset;
    }

    public bool UsedFallback { get; init; }
}

public sealed record ProjectedLockWaiterPage(
    MonitorLockState Lock,
    int WaiterOffset,
    int? NextWaiterOffset,
    string? NextWaiterCursor = null)
{
    public ProjectedLockWaiterPage(
        MonitorLockState @lock,
        int waiterOffset,
        int? nextWaiterOffset)
        : this(@lock, waiterOffset, nextWaiterOffset, null)
    {
    }

    public void Deconstruct(
        out MonitorLockState @lock,
        out int waiterOffset,
        out int? nextWaiterOffset)
    {
        @lock = Lock;
        waiterOffset = WaiterOffset;
        nextWaiterOffset = NextWaiterOffset;
    }
}

public sealed class ThreadSnapshotCursorException(string message) : ArgumentException(message);

public sealed class ThreadSnapshotDeepOffsetException(int offset)
    : ArgumentOutOfRangeException(
        nameof(offset),
        offset,
        $"Direct offsets above {ThreadSnapshotProjection.MaxDirectOffset} are rejected because ranked random access is quadratic. Start at offset=0 and continue with the returned cursor.");
