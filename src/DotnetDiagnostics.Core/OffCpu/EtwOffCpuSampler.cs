using System.Runtime.InteropServices;
using System.Security.Principal;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Etw;
using DotnetDiagnostics.Core.Memory;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using DotnetDiagnostics.Core.Symbols;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Core.OffCpu;

/// <summary>
/// Windows off-CPU sampler driven by the NT Kernel Logger's <c>ContextSwitch</c> +
/// <c>DispatcherReadyThread</c> tracepoints with stack walks enabled. For every CSwitch we observe
/// two attributable events: an OUT for the outgoing thread (with its blocking stack — captured by
/// the ETW kernel-mode stackwalker on the switch-out path) and an IN for the incoming thread.
/// Per-thread pending-out tracking closes each off-CPU span exactly the way
/// <see cref="PerfSchedOffCpuSampler"/> closes pairs on Linux, so the resulting
/// <see cref="OffCpuSnapshotArtifact"/> is platform-agnostic and the
/// the <c>query_snapshot</c> off-CPU drilldown does not need a Windows branch.
/// </summary>
/// <remarks>
/// <para>
/// Issue #829 syscall/wait-reason attribution: the same kernel session additionally enables the
/// <c>FileIOInit</c>/<c>FileIO</c>/<c>NetworkTCPIP</c> keywords (a second NT Kernel Logger session
/// is not an option — see <see cref="KernelEtwSessionGate"/>). Unlike the Linux
/// <c>raw_syscalls:sys_enter</c>/<c>sys_exit</c> pairing (which brackets a syscall with an exact
/// enter/exit interval), Windows kernel FileIO/TcpIp events do not consistently pair start/end
/// records across all their subtypes, so this sampler uses a looser "most recent qualifying I/O
/// event on this thread within a short lookback window before the CSwitch OUT" heuristic keyed off
/// the event's generic <c>TaskName:OpcodeName</c> (e.g. <c>FileIO:Read</c>, <c>TcpIp:Send</c>) —
/// see <see cref="TryGetIoLabel"/>. When no such event correlates, the span instead falls back to
/// a normalized bucket derived from the existing <c>OldThreadWaitReason</c> (<c>KWAIT_REASON</c>)
/// already surfaced as <see cref="OffCpuSpan.PrevState"/> — see <see cref="NormalizeWaitReason"/>.
/// This intentionally documents the Linux/Windows asymmetry: Linux syscall names are precise and
/// only present when a syscall was actually in flight (<see langword="null"/> otherwise), while
/// Windows always yields a label because every CSwitch OUT carries a wait reason. Both platforms
/// populate the SAME <see cref="OffCpuSpan.Syscall"/> field so downstream <c>query_snapshot</c>
/// views stay platform-agnostic, per the design constraint documented on
/// <see cref="RoutingOffCpuSampler"/>.
/// </para>
/// <para>
/// Requirements (validated by <see cref="IsAvailable"/>): Windows host with administrative elevation
/// (or <c>SeSystemProfilePrivilege</c>). Kernel ETW sessions are inherently system-wide so concurrent
/// captures are serialized through a static gate to keep buffer pressure predictable.
/// </para>
/// <para>
/// The stack we attribute to an off-CPU span is the stack captured at the CSwitch OUT event — i.e.
/// the call chain at the point the scheduler put the thread to sleep. The <c>WaitReason</c>
/// reported by the kernel (e.g. <c>UserRequest</c>, <c>WrLpcReceive</c>, <c>WrQueue</c>) is
/// propagated as the per-span <c>PrevState</c>, mirroring the perf <c>S/D/I</c> state characters.
/// Pending OUTs that never paired with an IN before the capture window ended are emitted as
/// <see cref="OffCpuSpan.IsCensored"/> spans with a lower-bound duration, matching the Linux
/// backend's flush behaviour so the LLM sees uniform "long blocker" attribution on both OSes.
/// </para>
/// <para>
/// Symbol resolution uses <see cref="SymbolReader"/> with precedence <c>symbolPath</c> →
/// <c>MCP_SYMBOL_PATH</c> → <c>_NT_SYMBOL_PATH</c> → target main-module directory;
/// managed↔kernel stack merging lands in slice 2c together
/// with the <c>depth</c> parameter, so for now we report user-mode frames as <c>module!method</c>
/// (or <c>module!0xADDR</c> when PDBs are missing) and kernel frames as <c>ntoskrnl!Function</c>
/// when symbols are available.
/// </para>
/// </remarks>
public sealed class EtwOffCpuSampler : IOffCpuSampler
{
    // Serialize concurrent kernel ETW sessions across ALL kernel samplers via the shared
    // process-wide gate (see KernelEtwSessionGate): the NT Kernel Logger is one global slot.
    private readonly ILogger<EtwOffCpuSampler> _logger;
    private readonly MvidReader _mvidReader;
    private readonly SymbolPathBuilder _symbolPathBuilder;

    public EtwOffCpuSampler(
        ILogger<EtwOffCpuSampler>? logger = null,
        MvidReader? mvidReader = null,
        SymbolPathBuilder? symbolPathBuilder = null)
    {
        _logger = logger ?? NullLogger<EtwOffCpuSampler>.Instance;
        _mvidReader = mvidReader ?? new MvidReader();
        _symbolPathBuilder = symbolPathBuilder ?? new SymbolPathBuilder();
    }

    internal const string SystemProfilePrivilegeName = "SeSystemProfilePrivilege";
    internal const string KernelLoggerPermissionDeniedMessage =
        "NT Kernel Logger 'ContextSwitch' provider requires either BUILTIN\\Administrators membership or SeSystemProfilePrivilege ('Profile system performance'). Grant one of those rights to the diagnostics sidecar account and restart the service.";

    [System.Runtime.Versioning.SupportedOSPlatformGuard("windows")]
    public bool IsAvailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger.LogTrace("ETW off-CPU sampler not available: not running on Windows.");
            return false;
        }

        try
        {
            var access = GetKernelLoggerAccess();
            if (!HasKernelLoggerAccess(access.IsAdministrator, access.HasSystemProfilePrivilege))
            {
                _logger.LogTrace(
                    "ETW off-CPU sampler not available: token is neither BUILTIN\\Administrators nor granted {Privilege}.",
                    SystemProfilePrivilegeName);
            }

            return access.IsAllowed;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ETW off-CPU sampler not available: failed to inspect Windows token privileges.");
            return false;
        }
    }

    public async Task<OffCpuSampleResult> SampleAsync(
        int processId,
        TimeSpan duration,
        int topN = 25,
        string? symbolPath = null,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be (0, 5min].");
        }
        if (topN <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(topN), "topN must be positive.");
        }
        if (!IsAvailable())
        {
            throw new UnauthorizedAccessException(KernelLoggerPermissionDeniedMessage);
        }

        if (OperatingSystem.IsWindows())
        {
            EnsureSystemProfilePrivilegeEnabledIfPresent();
        }

        await KernelEtwSessionGate.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CaptureAndProcessAsync(processId, duration, topN, symbolPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            KernelEtwSessionGate.Gate.Release();
        }
    }

    private async Task<OffCpuSampleResult> CaptureAndProcessAsync(
        int processId,
        TimeSpan duration,
        int topN,
        string? explicitSymbolPath,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var captureDir = Path.Combine(Path.GetTempPath(), $"diagmcp-etw-offcpu-{processId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(captureDir);

        var sessionName = $"dotnet-diag-mcp-offcpu-{processId}-{Guid.NewGuid():N}";
        var etlPath = Path.Combine(captureDir, "trace.etl");
        var notes = new List<string>();

        try
        {
            await CaptureEtwAsync(sessionName, etlPath, duration, cancellationToken).ConfigureAwait(false);
            return ProcessEtl(etlPath, processId, startedAt, duration, topN, explicitSymbolPath, notes, _mvidReader);
        }
        finally
        {
            TryDeleteDirectory(captureDir);
        }
    }

    private async Task CaptureEtwAsync(
        string sessionName,
        string etlPath,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        TraceEventSession? session = null;
        try
        {
            session = new TraceEventSession(sessionName, etlPath)
            {
                StopOnDispose = true,
            };

            // ContextSwitch: the primary tracepoint — fires once per OS scheduler switch with both
            //   OldThread/NewThread identities plus the OS-level wait reason on the OUT side.
            // Dispatcher: surfaces ReadyThread events (who woke the blocked thread); we already
            //   turn it on now so the ETL is forward-compatible for slice 2c without re-capture.
            // ImageLoad/Process/Thread: required for module → symbol resolution and
            //   thread-name population by the TraceLog conversion.
            // FileIOInit/FileIO/NetworkTCPIP (issue #829): a single process can generally only
            //   drive one active NT Kernel Logger session (see KernelEtwSessionGate), so the
            //   syscall/wait-reason attribution enrichment extends THIS session's keyword mask
            //   rather than starting a second kernel session. These keywords are lower-volume than
            //   ContextSwitch and we deliberately do NOT request stack walks for them (see
            //   stackKeywords below) — we only need the generic Task/Opcode name (e.g.
            //   "FileIO:Read", "TcpIp:Send") to correlate against the most recent CSwitch OUT for
            //   the same thread, not another callstack.
            var keywords = KernelTraceEventParser.Keywords.ContextSwitch |
                           KernelTraceEventParser.Keywords.Dispatcher |
                           KernelTraceEventParser.Keywords.ImageLoad |
                           KernelTraceEventParser.Keywords.Process |
                           KernelTraceEventParser.Keywords.Thread |
                           KernelTraceEventParser.Keywords.FileIOInit |
                           KernelTraceEventParser.Keywords.FileIO |
                           KernelTraceEventParser.Keywords.NetworkTCPIP;
            // Walk stacks specifically on ContextSwitch: stack captured at switch-out time IS the
            // blocking call chain we want to surface. FileIO/TcpIp events are consulted only for
            // their Task/Opcode name and timestamp, so no stack walk is requested for them —
            // this keeps the added overhead of the syscall-attribution enrichment low.
            var stackKeywords = KernelTraceEventParser.Keywords.ContextSwitch;

            session.EnableKernelProvider(keywords, stackKeywords);
            _logger.LogDebug("ETW off-CPU session '{Session}' started, capturing for {Duration}s.",
                sessionName, duration.TotalSeconds);

            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ETW off-CPU session '{Session}' failed.", sessionName);
            if (IsKernelLoggerPermissionFailure(ex))
            {
                throw new UnauthorizedAccessException(KernelLoggerPermissionDeniedMessage, ex);
            }

            throw new InvalidOperationException(
                $"Failed to start or run ETW kernel CSwitch session. Ensure admin elevation and that " +
                $"no conflicting kernel session is active. Details: {ex.Message}", ex);
        }
        finally
        {
            try { session?.Stop(); }
            catch (Exception ex) { _logger.LogDebug(ex, "ETW off-CPU session stop failed (best effort)."); }
            session?.Dispose();
        }
    }

    private OffCpuSampleResult ProcessEtl(
        string etlPath,
        int processId,
        DateTimeOffset startedAt,
        TimeSpan duration,
        int topN,
        string? explicitSymbolPath,
        List<string> notes,
        MvidReader mvidReader)
    {
        if (!File.Exists(etlPath))
        {
            throw new InvalidOperationException("ETW capture produced no output file.");
        }

        var symbolPath = _symbolPathBuilder.BuildForProcess(processId, explicitSymbolPath);
        var options = new TraceLogOptions
        {
            LocalSymbolsOnly = true,
            ShouldResolveSymbols = _ => false,
        };
        var etlxPath = TraceLog.CreateFromEventTraceLogFile(etlPath, null, options);

        try
        {
            using var traceLog = TraceLog.OpenOrConvert(etlxPath);
            return AggregateFromTraceLog(traceLog, processId, startedAt, duration, topN, symbolPath, notes, mvidReader);
        }
        finally
        {
            TryDelete(etlxPath);
        }
    }

    private static OffCpuSampleResult AggregateFromTraceLog(
        TraceLog traceLog,
        int processId,
        DateTimeOffset startedAt,
        TimeSpan duration,
        int topN,
        string? symbolPath,
        List<string> notes,
        MvidReader mvidReader)
    {
        if (symbolPath is not null)
        {
            try
            {
                using var symbolReader = new SymbolReader(TextWriter.Null, symbolPath);
                foreach (var process in traceLog.Processes)
                {
                    if (process.ProcessID != processId) continue;
                    foreach (var module in process.LoadedModules)
                    {
                        try { traceLog.CodeAddresses.LookupSymbolsForModule(symbolReader, module.ModuleFile); }
                        catch { /* best effort per module */ }
                    }
                }
            }
            catch { /* best effort */ }
        }

        // Per-TID pending OUT awaiting a matching IN. Tracking by kernel TID (not process) so
        // a thread that briefly migrates between cores is still attributed to one off-CPU span.
        var pending = new Dictionary<int, (double Ts, string State, List<OffCpuFrame> Stack, string Comm)>();
        var builder = OffCpuAggregator.CreateBuilder();
        long switches = 0;
        double maxTs = double.MinValue;

        // Issue #829: per-thread "most recently observed FileIO/TcpIp event" — a single entry per
        // TID (overwritten by newer events, never appended), so this is O(distinct thread count)
        // for the whole capture and needs no separate resource-boundedness cap. Only entries whose
        // timestamp falls within IoLookbackWindowSeconds of the CSwitch OUT are actually consulted;
        // events are visited in time order, so by the time a CSwitch is processed every I/O event
        // that could plausibly precede it has already been recorded.
        var lastIoByThread = new Dictionary<int, (double Ts, string Label)>();

        foreach (var ev in traceLog.Events)
        {
            if (ev is CSwitchTraceData cs)
            {
                var ts = cs.TimeStampRelativeMSec / 1000.0;
                if (ts > maxTs) maxTs = ts;

                // OUT side: the thread leaving the CPU belongs to the target.
                if (cs.OldProcessID == processId)
                {
                    switches++;
                    var stack = ExtractStack(cs.CallStack(), mvidReader);
                    pending[cs.OldThreadID] = (
                        Ts: ts,
                        State: cs.OldThreadWaitReason.ToString(),
                        Stack: stack,
                        Comm: SafeProcessName(cs.OldProcessName));
                }

                // IN side: the thread coming on CPU belongs to the target — close the matching OUT.
                if (cs.NewProcessID == processId)
                {
                    if (pending.Remove(cs.NewThreadID, out var p))
                    {
                        var micros = (long)Math.Round((ts - p.Ts) * 1_000_000.0);
                        if (micros > 0)
                        {
                            builder.AddSpan(new OffCpuSpan(
                                Tid: cs.NewThreadID,
                                Comm: p.Comm,
                                DurationMicros: micros,
                                PrevState: p.State,
                                BlockingStack: p.Stack,
                                Syscall: ResolveSyscallLabel(cs.NewThreadID, p.Ts, p.State, lastIoByThread)));
                        }
                    }
                }

                continue;
            }

            if (ev.ProcessID == processId && TryGetIoLabel(ev, out var ioLabel))
            {
                lastIoByThread[ev.ThreadID] = (ev.TimeStampRelativeMSec / 1000.0, ioLabel);
            }
        }

        // Flush any still-pending OUTs as censored spans (long blockers that outlived the window).
        if (maxTs > double.MinValue)
        {
            foreach (var kv in pending)
            {
                var micros = (long)Math.Round((maxTs - kv.Value.Ts) * 1_000_000.0);
                if (micros > 0)
                {
                    builder.AddSpan(new OffCpuSpan(
                        Tid: kv.Key,
                        Comm: kv.Value.Comm,
                        DurationMicros: micros,
                        PrevState: kv.Value.State,
                        BlockingStack: kv.Value.Stack,
                        IsCensored: true,
                        Syscall: ResolveSyscallLabel(kv.Key, kv.Value.Ts, kv.Value.State, lastIoByThread)));
                }
            }
        }

        return builder.Build(
            processId,
            startedAt,
            duration,
            switches,
            topN,
            symbolSource: "etw-cswitch-pdb",
            notes: notes.Count > 0 ? notes : null);
    }

    /// <summary>
    /// Issue #829: how far back (from a CSwitch OUT) we'll look for a FileIO/TcpIp event on the
    /// same thread before treating it as "not correlated" and falling back to the normalized
    /// wait-reason bucket. Chosen to be generous enough to catch the common "issue I/O, then block"
    /// pattern (the kernel logs the I/O-start event essentially synchronously with the syscall that
    /// initiates it) while staying short enough that an unrelated I/O op from seconds earlier on a
    /// reused/idle thread can't be mistaken for the cause of the current block.
    /// </summary>
    private const double IoLookbackWindowSeconds = 0.010;

    /// <summary>
    /// Best-effort syscall/wait-reason label for a span that blocked on thread <paramref name="tid"/>
    /// at time <paramref name="blockedAtTs"/>. Prefers the most recent FileIO/TcpIp event recorded
    /// for that thread if it falls inside <see cref="IoLookbackWindowSeconds"/> (the specific,
    /// precise case); otherwise falls back to <see cref="NormalizeWaitReason"/> applied to the
    /// kernel's own <c>KWAIT_REASON</c> (already captured as <paramref name="waitReason"/>/
    /// <see cref="OffCpuSpan.PrevState"/>). Unlike the Linux path, this fallback means a Windows
    /// span (almost) always gets a non-null label — see the class remarks for why this asymmetry
    /// is intentional rather than a bug.
    /// </summary>
    internal static string? ResolveSyscallLabel(
        int tid,
        double blockedAtTs,
        string waitReason,
        Dictionary<int, (double Ts, string Label)> lastIoByThread)
    {
        if (lastIoByThread.TryGetValue(tid, out var io) &&
            blockedAtTs - io.Ts is >= 0 and <= IoLookbackWindowSeconds)
        {
            return io.Label;
        }

        return NormalizeWaitReason(waitReason);
    }

    /// <summary>
    /// Extracts a generic <c>TaskName:OpcodeName</c> label (e.g. <c>FileIO:Read</c>,
    /// <c>TcpIp:Send</c>, <c>TcpIp:Connect</c>) from a FileIO or TcpIp kernel event without needing
    /// to hard-code every concrete TraceEvent subtype (<c>FileIOReadWriteTraceData</c>,
    /// <c>TcpIpSendTraceData</c>, <c>TcpIpV6ConnectTraceData</c>, ...) — TraceEvent already
    /// populates <see cref="TraceEvent.TaskName"/>/<see cref="TraceEvent.OpcodeName"/> generically
    /// for every kernel event, and only FileIO/TcpIp tasks can appear here because those are the
    /// only extra keywords this sampler enables (see <see cref="CaptureEtwAsync"/>).
    /// </summary>
    internal static bool TryGetIoLabel(TraceEvent ev, out string label)
    {
        if (ev.TaskName is "FileIO" or "TcpIp")
        {
            label = $"{ev.TaskName}:{ev.OpcodeName}";
            return true;
        }

        label = string.Empty;
        return false;
    }

    /// <summary>
    /// Reduces the .NET <c>System.Diagnostics.ThreadWaitReason</c> enum (TraceEvent's own mapping
    /// of the kernel <c>KWAIT_REASON</c>) to the shared cross-platform vocabulary
    /// (<c>Network</c>/<c>Disk</c>/<c>Sync</c>/<c>Sleep</c>/<c>Other</c>) called out as an option in
    /// issue #829. We chose normalization over surfacing the raw enum names verbatim because
    /// <c>KWAIT_REASON</c> is materially coarser than a Linux syscall name (e.g. it cannot
    /// distinguish a socket read from a pipe read — both are <c>UserRequest</c>), so exposing it
    /// unnormalized next to precise Linux syscall names in the same field would misleadingly imply
    /// a level of detail Windows doesn't have. <c>Network</c> is intentionally unreachable from this
    /// mapping alone (KWAIT_REASON can't identify network waits) — it is only ever produced by the
    /// more specific <see cref="TryGetIoLabel"/> correlation above.
    /// </summary>
    internal static string NormalizeWaitReason(string waitReason) => waitReason switch
    {
        "ExecutionDelay" => "Sleep",
        "UserRequest" or "EventPairHigh" or "EventPairLow" or "LpcReceive" or "LpcReply" => "Sync",
        "PageIn" or "PageOut" => "Disk",
        "FreePage" or "SystemAllocation" or "VirtualMemory" or "Executive" or "Suspended" => "Other",
        _ => "Other",
    };

    private static List<OffCpuFrame> ExtractStack(TraceCallStack? stack, MvidReader mvidReader)
    {
        // TraceLog stacks are leaf→root (Caller chains to parent). The aggregator reverses to
        // root→leaf so we keep TraceLog's order here to match perf's leaf-first convention.
        var frames = new List<OffCpuFrame>();
        var current = stack;
        var depth = 0;
        while (current is not null && depth < 256)
        {
            var ca = current.CodeAddress;
            var module = ca?.ModuleFile?.Name ?? string.Empty;
            var method = ResolveMethodName(ca);
            var identity = TryBuildIdentity(ca, module, mvidReader);
            frames.Add(new OffCpuFrame(module, method, identity));
            current = current.Caller;
            depth++;
        }
        return frames;
    }

    private static string ResolveMethodName(TraceCodeAddress? ca)
    {
        if (ca is null) return "[unknown]";
        var name = ca.FullMethodName;
        if (!string.IsNullOrEmpty(name) && name != "?") return name;
        return $"0x{ca.Address:X}";
    }

    /// <summary>
    /// Builds a <see cref="MethodIdentity"/> handoff payload from a TraceLog
    /// <see cref="TraceCodeAddress"/> when it points to a managed method. Mirrors
    /// <see cref="EventPipeCpuSampler"/>'s extraction so on-CPU and off-CPU hotspots
    /// hand off identical shapes to <c>dotnet-assembly-mcp</c>. Returns <c>null</c> for
    /// native / kernel frames and for managed frames missing both an MVID-readable
    /// module path and a metadata token (nothing useful to hand off).
    /// </summary>
    private static MethodIdentity? TryBuildIdentity(TraceCodeAddress? ca, string moduleNameFallback, MvidReader mvidReader)
    {
        if (ca is null) return null;
        var method = ca.Method;
        if (method is null) return null;

        var moduleFile = method.MethodModuleFile;
        var modulePath = moduleFile?.FilePath;
        var moduleName = !string.IsNullOrEmpty(modulePath)
            ? Path.GetFileName(modulePath)
            : (moduleFile?.Name is { Length: > 0 } n ? n : moduleNameFallback);

        var token = method.MethodToken;
        var mvid = mvidReader.TryRead(modulePath);

        // Skip frames where we have nothing useful for the handoff (native / unresolved JIT).
        if (mvid is null && token == 0 && string.IsNullOrEmpty(modulePath) && string.IsNullOrEmpty(moduleName))
        {
            return null;
        }

        var parsed = EventPipeCpuSampler.ParseFullMethodName(method.FullMethodName);
        return new MethodIdentity(
            ModuleName: moduleName,
            ModulePath: modulePath,
            ModuleVersionId: mvid,
            MetadataToken: token > 0 ? token : null,
            TypeFullName: parsed.TypeFullName,
            MethodName: parsed.MethodName,
            GenericArity: parsed.GenericArity)
        {
            GenericTypeArguments = parsed.GenericTypeArguments,
        };
    }

    private static string SafeProcessName(string? name)
        => string.IsNullOrEmpty(name) ? string.Empty : name!;

    internal static bool HasKernelLoggerAccess(bool isAdministrator, bool hasSystemProfilePrivilege)
        => isAdministrator || hasSystemProfilePrivilege;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static EtwKernelLoggerAccess GetKernelLoggerAccess()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Duplicate);
        var principal = new WindowsPrincipal(identity);
        var privilege = GetTokenPrivilegeState(identity.AccessToken, SystemProfilePrivilegeName);
        return new EtwKernelLoggerAccess(
            principal.IsInRole(WindowsBuiltInRole.Administrator),
            privilege.IsPresent,
            privilege.IsEnabled);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static void EnsureSystemProfilePrivilegeEnabledIfPresent()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.AdjustPrivileges);
        var privilege = GetTokenPrivilegeState(identity.AccessToken, SystemProfilePrivilegeName);
        if (!privilege.IsPresent || privilege.IsEnabled)
        {
            return;
        }

        var newState = new TOKEN_PRIVILEGES_SINGLE
        {
            PrivilegeCount = 1,
            Privileges = new LUID_AND_ATTRIBUTES
            {
                Luid = privilege.Luid,
                Attributes = NativeMethods.SE_PRIVILEGE_ENABLED,
            },
        };

        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<TOKEN_PRIVILEGES_SINGLE>());
        try
        {
            Marshal.StructureToPtr(newState, buffer, fDeleteOld: false);
            Marshal.SetLastPInvokeError(0);
            if (!NativeMethods.AdjustTokenPrivileges(identity.AccessToken, disableAllPrivileges: false, buffer, 0, IntPtr.Zero, IntPtr.Zero))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            }

            var lastError = Marshal.GetLastPInvokeError();
            if (lastError == NativeMethods.ERROR_NOT_ALL_ASSIGNED)
            {
                throw new UnauthorizedAccessException(KernelLoggerPermissionDeniedMessage);
            }

            if (lastError != NativeMethods.ERROR_SUCCESS)
            {
                throw new System.ComponentModel.Win32Exception(lastError);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static TokenPrivilegeState GetTokenPrivilegeState(Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token, string privilegeName)
    {
        if (!NativeMethods.LookupPrivilegeValue(lpSystemName: null, privilegeName, out var expectedLuid))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }

        _ = NativeMethods.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenPrivileges, IntPtr.Zero, 0, out var requiredBytes);
        var lastError = Marshal.GetLastPInvokeError();
        if (requiredBytes == 0 || lastError != NativeMethods.ERROR_INSUFFICIENT_BUFFER)
        {
            throw new System.ComponentModel.Win32Exception(lastError);
        }

        var buffer = Marshal.AllocHGlobal((int)requiredBytes);
        try
        {
            if (!NativeMethods.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenPrivileges, buffer, requiredBytes, out _))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            }

            var privilegeCount = Marshal.ReadInt32(buffer);
            var current = IntPtr.Add(buffer, sizeof(uint));
            var entrySize = Marshal.SizeOf<LUID_AND_ATTRIBUTES>();
            for (var index = 0; index < privilegeCount; index++)
            {
                var privilege = Marshal.PtrToStructure<LUID_AND_ATTRIBUTES>(current);
                if (privilege.Luid.LowPart == expectedLuid.LowPart && privilege.Luid.HighPart == expectedLuid.HighPart)
                {
                    return new TokenPrivilegeState(
                        privilege.Luid,
                        IsPresent: true,
                        IsEnabled: (privilege.Attributes & NativeMethods.SE_PRIVILEGE_ENABLED) != 0);
                }

                current = IntPtr.Add(current, entrySize);
            }

            return new TokenPrivilegeState(expectedLuid, IsPresent: false, IsEnabled: false);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool IsKernelLoggerPermissionFailure(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is UnauthorizedAccessException)
            {
                return true;
            }

            if (current is System.ComponentModel.Win32Exception win32 &&
                (win32.NativeErrorCode == NativeMethods.ERROR_ACCESS_DENIED ||
                 win32.NativeErrorCode == NativeMethods.ERROR_NOT_ALL_ASSIGNED ||
                 win32.NativeErrorCode == NativeMethods.ERROR_PRIVILEGE_NOT_HELD))
            {
                return true;
            }

            if (current.Message.Contains("access is denied", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("privilege", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal readonly record struct EtwKernelLoggerAccess(
        bool IsAdministrator,
        bool HasSystemProfilePrivilege,
        bool IsSystemProfilePrivilegeEnabled)
    {
        public bool IsAllowed => HasKernelLoggerAccess(IsAdministrator, HasSystemProfilePrivilege);
    }

    private readonly record struct TokenPrivilegeState(LUID Luid, bool IsPresent, bool IsEnabled);

    private enum TOKEN_INFORMATION_CLASS
    {
        TokenUser = 1,
        TokenGroups,
        TokenPrivileges,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES_SINGLE
    {
        public uint PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privileges;
    }

    private static class NativeMethods
    {
        internal const int ERROR_SUCCESS = 0;
        internal const int ERROR_ACCESS_DENIED = 5;
        internal const int ERROR_INSUFFICIENT_BUFFER = 122;
        internal const int ERROR_NOT_ALL_ASSIGNED = 1300;
        internal const int ERROR_PRIVILEGE_NOT_HELD = 1314;
        internal const uint SE_PRIVILEGE_ENABLED = 0x00000002;

        [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTokenInformation(
            Microsoft.Win32.SafeHandles.SafeAccessTokenHandle tokenHandle,
            TOKEN_INFORMATION_CLASS tokenInformationClass,
            IntPtr tokenInformation,
            uint tokenInformationLength,
            out uint returnLength);

        [DllImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool LookupPrivilegeValue(
            string? lpSystemName,
            string lpName,
            out LUID luid);

        [DllImport("advapi32.dll", EntryPoint = "AdjustTokenPrivileges", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AdjustTokenPrivileges(
            Microsoft.Win32.SafeHandles.SafeAccessTokenHandle tokenHandle,
            [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
            IntPtr newState,
            uint bufferLength,
            IntPtr previousState,
            IntPtr returnLength);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }
}
