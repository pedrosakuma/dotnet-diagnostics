namespace DotnetDiagnostics.Core.Threads;

internal static class MonitorWaitGraph
{
    internal static Dictionary<int, ManagedThread> BuildThreadsById(ThreadSnapshotArtifact snapshot)
        => snapshot.Threads
            .Where(t => t.ManagedThreadId > 0)
            .GroupBy(t => t.ManagedThreadId)
            .ToDictionary(group => group.Key, group => group.First());

    internal static MonitorWaitEdge[] BuildEdges(
        ThreadSnapshotArtifact snapshot,
        IReadOnlyDictionary<int, ManagedThread> threadsById)
        => snapshot.Locks
            .Where(l => l.OwnerManagedThreadId > 0 && l.WaitingManagedThreadIds.Count > 0)
            .SelectMany(l => l.WaitingManagedThreadIds
                .Where(waiterId => waiterId > 0 && waiterId != l.OwnerManagedThreadId)
                .Where(waiterId => threadsById.ContainsKey(waiterId) && threadsById.ContainsKey(l.OwnerManagedThreadId))
                .Distinct()
                .Select(waiterId => new MonitorWaitEdge(
                    waiterId,
                    l.OwnerManagedThreadId,
                    l.ObjectAddress,
                    l.ObjectTypeFullName,
                    l.LockKind)))
            .Distinct()
            .ToArray();

    internal static Dictionary<int, MonitorWaitEdge[]> GroupByWaiter(MonitorWaitEdge[] edges)
        => edges
            .GroupBy(edge => edge.WaitingThreadId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(edge => edge.OwnerThreadId)
                    .ThenBy(edge => edge.LockObjectAddress)
                    .ToArray());
}

internal sealed record MonitorWaitEdge(
    int WaitingThreadId,
    int OwnerThreadId,
    ulong LockObjectAddress,
    string? LockObjectTypeFullName,
    string LockKind);
