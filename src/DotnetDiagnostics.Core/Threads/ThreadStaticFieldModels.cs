namespace DotnetDiagnostics.Core.Threads;

// ClrMD-backed [ThreadStatic] field inspector (issue #1120). Re-opens the thread snapshot's
// origin (dump file or live pid) on demand, same strategy as IFrameVariableResolver, and reads
// ClrType.ThreadStaticFields for one caller-named type against every thread in the snapshot.
// Scoped to a single typeFullName by design: ClrMD 4.x removed unscoped
// ClrHeap.EnumerateTypes()-style enumeration (see microsoft/clrmd:doc/FAQ.md).
public interface IThreadStaticFieldResolver
{
    Task<ThreadStaticFieldsResult> ResolveAsync(
        ThreadSnapshotArtifact artifact,
        string typeFullName,
        bool includeSensitiveValues,
        CancellationToken cancellationToken = default);
}

/// <summary>Per-thread <c>[ThreadStatic]</c> field values recovered for one resolved type.</summary>
public sealed record ThreadStaticFieldsResult(
    string TypeFullName,
    IReadOnlyList<ThreadStaticFieldsForThread> Threads)
{
    /// <summary>Degraded/partial-recovery notes (type not found, no ThreadStaticFields, …).</summary>
    public IReadOnlyList<string>? Warnings { get; init; }
}

/// <summary>Every <c>[ThreadStatic]</c> field of the resolved type, as read on one managed thread.</summary>
public sealed record ThreadStaticFieldsForThread(
    int ManagedThreadId,
    IReadOnlyList<ThreadStaticFieldValue> Fields);

/// <summary>One field's value on one thread. <see cref="IsInitialized"/> false means no value was ever set (not garbage).</summary>
public sealed record ThreadStaticFieldValue(
    string Name,
    bool IsInitialized)
{
    /// <summary>Truncated preview; requires <c>includeSensitiveValues</c> (gated for every kind, not just strings).</summary>
    public string? ValuePreview { get; init; }
}
