namespace DotnetDiagnosticsMcp.Core.Drilldown;

/// <summary>
/// In-process registry that stores a heavy diagnostic artifact (parsed trace, gcdump, etc.)
/// keyed by an opaque short-lived handle. Lets a tool return a small summary to the LLM and
/// expose a follow-up tool to drill down without re-running the collector.
/// </summary>
public interface IDiagnosticHandleStore
{
    /// <summary>
    /// Stores <paramref name="artifact"/> under a fresh handle. The artifact is evicted after
    /// <paramref name="ttl"/> elapses or after the store's capacity is reached.
    /// </summary>
    /// <param name="processId">PID the artifact was collected from. Used for invalidation when
    /// <paramref name="evictWhenProcessExits"/> is true.</param>
    /// <param name="kind">Short discriminator (e.g. "cpu-sample", "gc-dump").</param>
    /// <param name="artifact">Opaque payload retained in memory.</param>
    /// <param name="ttl">Maximum age before automatic eviction.</param>
    /// <param name="evictWhenProcessExits">When true (default), the handle is dropped as soon as
    /// the eviction background service notices the PID is no longer alive. Set to <c>false</c> for
    /// artifacts collected from an offline source (dump files, imported traces) whose <c>ProcessId</c>
    /// refers to a process on another host or a PID that may have been reused locally.</param>
    /// <param name="origin">Logical provenance of the artifact (see <see cref="HandleOrigin"/>).
    /// When <c>null</c> (the default), origin is inferred from <paramref name="evictWhenProcessExits"/>:
    /// <c>true</c> → <see cref="HandleOrigin.Live"/>, <c>false</c> → <see cref="HandleOrigin.Dump"/>.
    /// Explicit values (e.g. <see cref="HandleOrigin.Imported"/>) override the inference and let
    /// the legacy-boundary authorization table (#207) distinguish offline-imported artifacts from
    /// dumps captured on-host.</param>
    /// <returns>The newly-issued handle.</returns>
    DiagnosticHandle Register(int processId, string kind, object artifact, TimeSpan ttl, bool evictWhenProcessExits = true, HandleOrigin? origin = null);

    /// <summary>
    /// Retrieves the artifact previously stored under <paramref name="handle"/>, casting it
    /// to <typeparamref name="T"/>. Returns <c>null</c> if the handle is unknown, expired, or
    /// holds an artifact of an incompatible type.
    /// </summary>
    T? TryGet<T>(string handle) where T : class;

    /// <summary>
    /// Retrieves the artifact previously stored under <paramref name="handle"/> together with the
    /// <c>kind</c> it was registered with — without forcing a generic type assertion. Returns
    /// <c>null</c> when the handle is unknown or expired. Used by the polymorphic
    /// <c>query_collection</c> dispatcher, which selects the artifact's concrete type based on
    /// <see cref="DiagnosticHandle.Kind"/>.
    /// </summary>
    HandleLookup? TryGetWithKind(string handle);

    /// <summary>
    /// Removes the artifact stored under <paramref name="handle"/> immediately. Safe to call
    /// when the handle is already expired or unknown.
    /// </summary>
    bool Invalidate(string handle);

    /// <summary>
    /// Removes every artifact previously registered for <paramref name="processId"/> that
    /// opted in to process-exit eviction (the default). Use when the target process exits
    /// so consumers don't drill into a dead trace. Artifacts registered with
    /// <c>evictWhenProcessExits: false</c> (e.g. offline dump-file snapshots) are intentionally
    /// preserved so they survive the originating PID's exit.
    /// </summary>
    int InvalidateForProcess(int processId);
}

/// <summary>
/// Logical provenance of a registered artifact. Consumed by the legacy-boundary
/// authorization table (RFC 0002 / #207) to decide which scopes a drilldown view requires.
/// </summary>
public enum HandleOrigin
{
    /// <summary>Collected by attaching to a live process via the diagnostic IPC socket.
    /// PID-evictable (<c>evictWhenProcessExits=true</c> by convention).</summary>
    Live,
    /// <summary>Walked from a process dump file captured on-host. Not PID-evictable —
    /// the originating PID may no longer exist or refer to an unrelated process.</summary>
    Dump,
    /// <summary>Loaded from an artifact originating off-host (e.g. uploaded trace,
    /// manifest import). Not PID-evictable; treated more conservatively than
    /// <see cref="Dump"/> by the legacy-boundary authorization table.</summary>
    Imported,
}

/// <summary>Lightweight value-type description of a registered artifact.</summary>
public sealed record DiagnosticHandle(string Id, DateTimeOffset ExpiresAt, int ProcessId, string Kind)
{
    /// <summary>Logical provenance of the artifact (see <see cref="HandleOrigin"/>).
    /// Defaults to <see cref="HandleOrigin.Live"/> so existing callers that construct
    /// <see cref="DiagnosticHandle"/> via the positional record syntax keep compiling.</summary>
    public HandleOrigin Origin { get; init; } = HandleOrigin.Live;
}

/// <summary>Bundle returned by <see cref="IDiagnosticHandleStore.TryGetWithKind"/>: the
/// metadata plus the untyped artifact, so polymorphic dispatchers can branch on
/// <see cref="DiagnosticHandle.Kind"/> without paying a generic type assertion first.</summary>
public readonly record struct HandleLookup(DiagnosticHandle Handle, object Artifact)
{
    /// <summary>Convenience accessor for <see cref="DiagnosticHandle.Kind"/>.</summary>
    public string Kind => Handle.Kind;
}
