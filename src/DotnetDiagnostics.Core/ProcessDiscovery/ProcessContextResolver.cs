using System.Collections.Concurrent;
using DotnetDiagnostics.Core.Capabilities;

namespace DotnetDiagnostics.Core.ProcessDiscovery;

/// <summary>
/// Default <see cref="IProcessContextResolver"/> implementation.
/// </summary>
/// <remarks>
/// Auto-resolution semantics (when <c>requestedProcessId</c> is null/zero):
/// <list type="bullet">
///   <item><b>0 candidates</b> → <c>NoDotnetProcessFound</c>.</item>
///   <item><b>1 candidate</b> → returns it with <see cref="ProcessContext.AutoResolved"/> = <c>true</c>.</item>
///   <item><b>N candidates</b> → <c>AmbiguousDotnetProcess</c> carrying the list inline.</item>
/// </list>
/// Capability digests are cached per-pid for <see cref="DefaultCacheTtl"/> so that follow-up
/// tool calls within an investigation pay the probe cost once. Cache entries are invalidated
/// implicitly when the pid is no longer reachable (next miss re-detects).
/// </remarks>
public sealed class ProcessContextResolver : IProcessContextResolver
{
    public static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromSeconds(60);
    private const int TrimPeriod = 64;

    private readonly IProcessDiscovery _discovery;
    private readonly ICapabilityDetector _detector;
    private readonly ISessionTargetBindingStore? _bindings;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<int, CacheEntry> _cache = new();
    private int _resolveCountSinceTrim;

    public ProcessContextResolver(IProcessDiscovery discovery, ICapabilityDetector detector)
        : this(discovery, detector, bindings: null, TimeProvider.System, DefaultCacheTtl)
    {
    }

    public ProcessContextResolver(IProcessDiscovery discovery, ICapabilityDetector detector, ISessionTargetBindingStore? bindings)
        : this(discovery, detector, bindings, TimeProvider.System, DefaultCacheTtl)
    {
    }

    public ProcessContextResolver(IProcessDiscovery discovery, ICapabilityDetector detector, TimeProvider clock, TimeSpan cacheTtl)
        : this(discovery, detector, bindings: null, clock, cacheTtl)
    {
    }

    public ProcessContextResolver(IProcessDiscovery discovery, ICapabilityDetector detector, ISessionTargetBindingStore? bindings, TimeProvider clock, TimeSpan cacheTtl)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _bindings = bindings;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _ttl = cacheTtl;
    }

    public Task<ProcessContextResolution> ResolveAsync(int? requestedProcessId, CancellationToken cancellationToken)
        => ResolveAsync(sessionId: null, requestedProcessId, cancellationToken);

    public async Task<ProcessContextResolution> ResolveAsync(string? sessionId, int? requestedProcessId, CancellationToken cancellationToken)
    {
        if (requestedProcessId is { } requested)
        {
            if (requested < 0)
            {
                return new ProcessContextResolution(
                    Context: null,
                    Error: new DiagnosticError(
                        "InvalidArgument",
                        $"processId must be a positive process id (or null/0 for auto-resolution). Got {requested}.",
                        "Omit processId entirely (or pass 0) to auto-resolve when exactly one .NET process is reachable; otherwise pass a pid returned by inspect_process(view=\"list\")."));
            }

            if (requested > 0)
            {
                return await ResolveExplicitAsync(requested, autoResolved: false, source: "explicit", cancellationToken).ConfigureAwait(false);
            }
        }

        // Session binding wins over local auto-resolution but only when the caller did not
        // pass an explicit pid above. This is what lets the orchestrator (Phase 3, #20)
        // route every subsequent tool call to the attached pod without touching any tool
        // signature. When no session id is supplied OR no binding is registered, we fall
        // through to the legacy local-discovery path — preserving every pre-Phase-2 flow
        // exactly as before.
        if (_bindings is not null && _bindings.TryGet(sessionId) is { } binding)
        {
            return await ResolveExplicitAsync(binding.ProcessId, autoResolved: false, source: $"session-binding:{binding.Source}", cancellationToken).ConfigureAwait(false);
        }

        var processes = _discovery.ListProcesses();
        if (processes.Count == 0)
        {
            return new ProcessContextResolution(
                Context: null,
                Error: new DiagnosticError(
                    "NoDotnetProcessFound",
                    "No .NET process exposes a diagnostic IPC endpoint on this host.",
                    "Verify the target is running, that you share its PID namespace (in containers / Kubernetes), and that the sidecar runs as the same UID as the target."));
        }

        if (processes.Count > 1)
        {
            var preview = string.Join(", ", processes.Take(5).Select(p => $"{p.ProcessId}={p.ManagedEntrypointAssemblyName ?? "?"}"));
            return new ProcessContextResolution(
                Context: null,
                Error: new DiagnosticError(
                    "AmbiguousDotnetProcess",
                    $"{processes.Count} .NET processes are visible: {preview}{(processes.Count > 5 ? ", …" : "")}. Pass processId explicitly.",
                    null),
                Candidates: processes);
        }

        return await ResolveExplicitAsync(processes[0].ProcessId, autoResolved: true, source: "local-auto", cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProcessContextResolution> ResolveExplicitAsync(int pid, bool autoResolved, string source, CancellationToken cancellationToken)
    {
        if (TryGetCachedContext(pid, autoResolved, source) is { } cached)
        {
            return cached;
        }

        // Fail-fast for non-existent / non-.NET PIDs (#72): without this guard
        // CapabilityDetector happily returns a blank DiagnosticCapabilities for a PID
        // that is not reachable, which gets stamped onto ResolvedProcess with
        // RuntimeVersion=null and confuses both the LLM (no signal that the target is
        // gone) and strict MCP clients (which historically rejected the partial envelope
        // on schema grounds; that part is fixed by the ProcessContext schema patch in
        // this same change). IProcessDiscovery.TryGetProcess returns null both for PIDs
        // that simply don't exist and for PIDs that exist but don't expose a .NET
        // diagnostic IPC endpoint — both are equally useless to the agent.
        if (_discovery.TryGetProcess(pid) is null)
        {
            return new ProcessContextResolution(
                Context: null,
                Error: new DiagnosticError(
                    "ProcessNotFound",
                    $"No .NET process with pid {pid} is reachable on this host (either the pid is not running, it is not a .NET process, or the sidecar runs under a different UID than the target).",
                    "Call inspect_process(view=\"list\") to discover currently-running .NET processes. In containers / Kubernetes, verify the sidecar shares the target's PID namespace and runs as the same UID."));
        }

        DiagnosticCapabilities caps;
        try
        {
            caps = await _detector.DetectAsync(pid, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProcessContextResolution(
                Context: null,
                Error: new DiagnosticError(
                    "EndpointUnavailable",
                    $"Could not probe pid {pid}: {ex.Message}",
                    ex.GetType().FullName));
        }

        // The process exists but doesn't expose a .NET diagnostic IPC endpoint. Without
        // this check the LLM would receive a fully-populated ProcessContext with
        // Runtime=Unknown and every Can* flag false — easy to misread as "this .NET
        // process supports nothing". A structured NotADotnetProcess error is unambiguous.
        if (caps.Runtime == RuntimeFlavor.Unknown && string.IsNullOrEmpty(caps.RuntimeVersion))
        {
            return new ProcessContextResolution(
                Context: null,
                Error: new DiagnosticError(
                    "NotADotnetProcess",
                    $"Process {pid} is running but does not expose a .NET diagnostic IPC endpoint. Either it is not a .NET process, or the sidecar runs under a different UID than the target.",
                    "Verify the target is .NET, that the sidecar runs as the same UID as the target, and (in containers / Kubernetes) that it shares the target's PID namespace."));
        }

        var context = new ProcessContext(
            ProcessId: pid,
            Runtime: caps.Runtime,
            CanSampleCpu: caps.CanSampleCpu,
            CanCollectGcDump: caps.CanCollectGcDump,
            AutoResolved: autoResolved,
            RuntimeVersion: string.IsNullOrEmpty(caps.RuntimeVersion) ? null : caps.RuntimeVersion,
            BindingSource: source);

        _cache[pid] = new CacheEntry(context, _clock.GetUtcNow() + _ttl);
        TrimExpiredEntriesIfNeeded();
        return new ProcessContextResolution(context, Error: null);
    }

    /// <summary>For tests: drops every cached entry.</summary>
    internal void ClearCache() => _cache.Clear();

    /// <summary>For tests: reports the current cache size.</summary>
    internal int CachedEntryCount => _cache.Count;

    private ProcessContextResolution? TryGetCachedContext(int pid, bool autoResolved, string source)
    {
        if (!_cache.TryGetValue(pid, out var entry))
        {
            return null;
        }

        var now = _clock.GetUtcNow();
        if (now >= entry.ExpiresAt)
        {
            _cache.TryRemove(pid, out _);
            return null;
        }

        return new ProcessContextResolution(
            entry.Context with { AutoResolved = autoResolved, BindingSource = source },
            Error: null);
    }

    private void TrimExpiredEntriesIfNeeded()
    {
        if (Interlocked.Increment(ref _resolveCountSinceTrim) < TrimPeriod)
        {
            return;
        }

        Interlocked.Exchange(ref _resolveCountSinceTrim, 0);
        var now = _clock.GetUtcNow();
        foreach (var pair in _cache)
        {
            if (now >= pair.Value.ExpiresAt)
            {
                _cache.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed record CacheEntry(ProcessContext Context, DateTimeOffset ExpiresAt);
}
