namespace DotnetDiagnostics.Core.Threads;

/// <summary>
/// ClrMD-backed <c>[ThreadStatic]</c> field inspector (issue #1120). Re-opens the origin of a
/// thread snapshot (dump file or live pid) — the same re-attach strategy as
/// <see cref="IFrameVariableResolver"/> — and reads <see cref="Microsoft.Diagnostics.Runtime.ClrType.ThreadStaticFields"/>
/// for an explicitly named type against every thread captured in the snapshot. Deliberately scoped
/// to a single caller-supplied <c>typeFullName</c>: ClrMD 4.x removed unscoped
/// <c>ClrHeap.EnumerateTypes()</c>-style enumeration, so an unscoped "list every ThreadStatic
/// field in the process" mode is out of scope (see the issue body and
/// <c>microsoft/clrmd:doc/FAQ.md</c> for the reimplementation recipe this sidesteps).
/// </summary>
public interface IThreadStaticFieldResolver
{
    /// <summary>
    /// Re-opens the origin of <paramref name="artifact"/>, resolves <paramref name="typeFullName"/>
    /// via <see cref="Microsoft.Diagnostics.Runtime.ClrHeap.GetTypeByName(string)"/>, and reads each
    /// of its <c>[ThreadStatic]</c> fields against every managed thread present in the snapshot.
    /// </summary>
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
    /// <summary>Notes about degraded/partial recovery (type not found, no ThreadStaticFields, …).</summary>
    public IReadOnlyList<string>? Warnings { get; init; }
}

/// <summary>Every <c>[ThreadStatic]</c> field of the resolved type, as read on one managed thread.</summary>
public sealed record ThreadStaticFieldsForThread(
    int ManagedThreadId,
    uint OSThreadId,
    IReadOnlyList<ThreadStaticFieldValue> Fields);

/// <summary>
/// One <c>[ThreadStatic]</c> field's value on one thread. <see cref="IsInitialized"/> mirrors
/// <see cref="Microsoft.Diagnostics.Runtime.ClrThreadStaticField.IsInitialized(Microsoft.Diagnostics.Runtime.ClrThread)"/>:
/// a thread that never touched the field's storage slot reports <c>false</c> with no
/// <see cref="ValuePreview"/>/<see cref="Address"/>, not a garbage/default value.
/// </summary>
public sealed record ThreadStaticFieldValue(
    string Name,
    string? TypeFullName,
    bool IsInitialized)
{
    /// <summary>Field address on this thread; null when uninitialized.</summary>
    public string? Address { get; init; }
    /// <summary>
    /// Truncated string preview of the value. Gated behind <c>includeSensitiveValues</c> for
    /// every element kind — including primitives — not just strings/object references. This is a
    /// deliberately more conservative stance than <see cref="FrameVariable.ValuePreview"/> (which
    /// only gates string previews): unlike frame locals, <c>Read&lt;T&gt;</c> makes primitive
    /// thread-static values readable too, so gating them uniformly keeps one simple rule for the
    /// whole view rather than a per-kind sensitivity carve-out.
    /// </summary>
    public string? ValuePreview { get; init; }
    /// <summary>True when <see cref="ValuePreview"/> was truncated at the preview length cap.</summary>
    public bool ValuePreviewTruncated { get; init; }
}
