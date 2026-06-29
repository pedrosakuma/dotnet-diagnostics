namespace DotnetDiagnostics.Core.Threads;

/// <summary>
/// ClrMD-backed <c>!clrstack -a</c> equivalent (issue #449). Re-opens the origin of a thread
/// snapshot (dump file or live pid) and walks one thread's managed stack roots, surfacing the
/// object-typed locals/parameters alive on each frame. Best-effort: ClrMD 3.x exposes object
/// references with their register/stack location but not source-level names, and value-type
/// (struct/primitive) locals are not enumerable, so this complements rather than replaces an
/// offline <c>dotnet-dump analyze</c> session.
/// </summary>
public interface IFrameVariableResolver
{
    /// <summary>
    /// Re-opens the origin of <paramref name="artifact"/> and inspects the locals/parameters of
    /// <paramref name="managedThreadId"/>. Dump origin is time-consistent with the snapshot; live
    /// origin is best-effort (the process may have moved on or exited).
    /// </summary>
    Task<FrameVariablesResult> ResolveAsync(
        ThreadSnapshotArtifact artifact,
        int managedThreadId,
        bool includeSensitiveValues,
        CancellationToken cancellationToken = default);
}

/// <summary>Locals/parameters recovered for one thread, plus its current exception when faulted.</summary>
public sealed record FrameVariablesResult(
    int ManagedThreadId,
    uint OSThreadId,
    IReadOnlyList<FrameVariables> Frames)
{
    /// <summary>Throw-site exception type when the thread is unwinding/has a current managed exception.</summary>
    public string? CurrentExceptionType { get; init; }
    /// <summary>Throw-site exception message, when readable.</summary>
    public string? CurrentExceptionMessage { get; init; }
    /// <summary>Notes about degraded/partial recovery (no roots, value-type locals dropped, …).</summary>
    public IReadOnlyList<string>? Warnings { get; init; }
}

/// <summary>One managed frame with the object-typed roots ClrMD could attribute to it.</summary>
public sealed record FrameVariables(
    int FrameIndex,
    string DisplayName,
    string? TypeFullName,
    string? ModuleName,
    string InstructionPointer,
    string StackPointer,
    IReadOnlyList<FrameVariable> Variables);

/// <summary>
/// One stack root attributed to a frame. <see cref="Name"/> is null — ClrMD 3.x does not expose
/// source-level local/parameter names — so callers identify slots by <see cref="Location"/>.
/// </summary>
public sealed record FrameVariable(
    string? Name,
    string? TypeFullName,
    string Address,
    string Location,
    bool IsPinned,
    bool IsInterior)
{
    /// <summary>Optional value/string preview, only populated when sensitive heap reads are allowed.</summary>
    public string? ValuePreview { get; init; }
}
