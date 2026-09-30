using System.Text.Json;
using DotnetDiagnostics.Core.Threads;

namespace DotnetDiagnostics.Core.Captures;

// ManagedThread's lock-role properties are intentionally JsonIgnore on public responses.
// Durable compatibility material must retain them, rather than recomputing or discarding them.
internal sealed record ThreadSnapshotEncoding(ThreadSnapshotArtifact Snapshot, IEnumerable<ThreadSnapshotRow> Threads)
{
    internal static ThreadSnapshotEncoding From(ThreadSnapshotArtifact snapshot)
        => new(snapshot with { Threads = [] }, snapshot.Threads.Select(thread =>
        {
            if (thread is null) throw new JsonException("Null thread row.");
            return new ThreadSnapshotRow(thread, thread.IsContendedLockOwner, thread.IsLockWaiter, thread.IsDeadlockCandidate);
        }));

    internal ThreadSnapshotArtifact Restore()
    {
        if (Snapshot is null || Snapshot.Threads is null || Snapshot.Threads.Count != 0 || Threads is null)
            throw new JsonException("Invalid thread snapshot encoding.");
        return Snapshot with
        {
            Threads = Threads.Select(row =>
            {
                if (row?.Thread is null) throw new JsonException("Null thread row.");
                return row.Thread with
                {
                    IsContendedLockOwner = row.IsContendedLockOwner,
                    IsLockWaiter = row.IsLockWaiter,
                    IsDeadlockCandidate = row.IsDeadlockCandidate,
                };
            }).ToArray(),
        };
    }
}

internal sealed record ThreadSnapshotRow(ManagedThread Thread, bool IsContendedLockOwner, bool IsLockWaiter, bool IsDeadlockCandidate);
