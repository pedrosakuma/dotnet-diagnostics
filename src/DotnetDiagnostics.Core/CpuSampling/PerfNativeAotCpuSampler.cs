using System.Diagnostics;
using System.Runtime.InteropServices;
using DotnetDiagnostics.Core.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Core.CpuSampling;

/// <summary>
/// Linux-only CPU sampler that wraps the <c>perf</c> kernel profiler. Used as the
/// fallback for NativeAOT processes where the managed <c>Microsoft-DotNETCore-SampleProfiler</c>
/// EventSource is not implemented.
/// </summary>
/// <remarks>
/// <para>
/// Native stacks resolve to ELF symbols (the NativeAOT binary's <c>.symtab</c> or
/// <c>.dynsym</c>) so the returned <see cref="CpuSample"/> still has meaningful method
/// names. <c>MethodIdentity</c> is always <c>null</c> for native frames because
/// the IL <c>(MVID, MetadataToken)</c> handoff to <c>dotnet-assembly-mcp</c> does not
/// apply — there is no managed metadata token for an AOT-compiled method.
/// </para>
/// <para>
/// Requirements (validated by <see cref="IsAvailable"/>): Linux host, <c>perf</c> binary
/// in <c>PATH</c>, and kernel permission to attach (<c>perf_event_paranoid &lt;= 2</c> or
/// the calling process has <c>CAP_PERFMON</c> / <c>CAP_SYS_ADMIN</c>). On Kubernetes this
/// typically means adding <c>CAP_PERFMON</c> (or <c>SYS_ADMIN</c>) to the ephemeral
/// debug container's <c>securityContext.capabilities.add</c>.
/// </para>
/// </remarks>
public sealed class PerfNativeAotCpuSampler : ICpuSampler
{
    private readonly ILogger<PerfNativeAotCpuSampler> _logger;
    private readonly string _configuredPath;
    private readonly int _samplingFrequencyHz;
    private string? _resolvedPath;
    private bool _resolutionAttempted;
    private readonly object _resolveLock = new();

    public PerfNativeAotCpuSampler(
        ILogger<PerfNativeAotCpuSampler>? logger = null,
        string perfPath = "perf",
        int samplingFrequencyHz = 99)
    {
        _logger = logger ?? NullLogger<PerfNativeAotCpuSampler>.Instance;
        _configuredPath = perfPath;
        _samplingFrequencyHz = samplingFrequencyHz;
    }

    private string? ResolvePerfPath()
    {
        if (_resolutionAttempted) return _resolvedPath;
        lock (_resolveLock)
        {
            if (_resolutionAttempted) return _resolvedPath;
            _resolvedPath = PerfBinaryResolver.Resolve(
                _configuredPath,
                PerfBinaryResolver.EnumerateDefaultLinuxToolsCandidates,
                PerfBinaryResolver.ProbePerfVersion);
            _resolutionAttempted = true;
            if (_resolvedPath is not null && !string.Equals(_resolvedPath, _configuredPath, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Configured perf path '{Configured}' was unusable; resolved to '{Resolved}' from linux-tools candidates.",
                    _configuredPath, _resolvedPath);
            }
            return _resolvedPath;
        }
    }

    /// <summary>
    /// Returns true when the host can run this sampler. Cheap probe — checks the OS and
    /// resolves a working <c>perf</c> binary (trying the configured path first, then
    /// <c>/usr/lib/linux-tools-*/perf</c> on Debian/Ubuntu/WSL where <c>/usr/bin/perf</c>
    /// is often a wrapper that requires <c>linux-tools-$(uname -r)</c> to be installed).
    /// Does NOT verify <c>perf_event_paranoid</c>; that surfaces as a "Permission denied"
    /// at the first sample attempt and is reported back to the LLM in the error payload.
    /// </summary>
    public bool IsAvailable()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return false;
        return ResolvePerfPath() is not null;
    }

    public async Task<CpuSampleResult> SampleAsync(
        int processId,
        TimeSpan duration,
        int topN = 25,
        SourceResolutionOptions? sourceResolution = null,
        MethodInstantiationResolutionOptions? methodInstantiationResolution = null,
        NativeAotSymbolResolutionOptions? nativeAotSymbols = null,
        bool exportTrace = false,
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
            throw new InvalidOperationException(
                "perf is not available on this host. Install linux-perf and ensure the diagnostics " +
                "container has CAP_PERFMON (or CAP_SYS_ADMIN) and perf_event_paranoid <= 2.");
        }

        // #395: optional NativeAOT identity resolution. The ILC *.map.xml is the authoritative set
        // of managed MethodCode symbols; loading it lets us emit a (TypeFullName, MethodName)
        // MethodIdentity for genuine managed frames and skip runtime helpers / native code.
        var methodMap = NativeAotMethodMap.TryLoad(nativeAotSymbols?.MapFilePath, _logger);
        if (methodMap is not null)
        {
            _logger.LogInformation(
                "Loaded NativeAOT map file with {Count} managed method symbols for pid {Pid}; MethodIdentity handoff enabled.",
                methodMap.Count, processId);
        }

        var (moduleName, modulePath) = TryResolveTargetModule(processId);

        var perfDataPath = Path.Combine(Path.GetTempPath(), $"diagnosticsmcp-perf-{processId}-{Guid.NewGuid():N}.data");
        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            await RecordAsync(processId, perfDataPath, duration, cancellationToken).ConfigureAwait(false);
            var script = await RunScriptAsync(perfDataPath, cancellationToken).ConfigureAwait(false);
            // Trust perf record -p <pid> for process scoping. Passing processId=0 here avoids
            // a /proc/<pid>/task post-hoc race that would discard samples from threadpool /
            // GC workers that exited between recording and parsing.
            var (total, hotspots, root, symbolSource, identities) = Aggregate(
                script, processId: 0, topN, methodMap, moduleName, modulePath);
            var stampedRoot = CallTreeIdentityProjector.Stamp(root, identities);
            // Compute the global self-time leader from the SAME (identity-stamped) tree the artifact
            // stores, so the inline signals match the signals:// Resource path.
            var topSelfTime = CpuSampleAnalytics.TopSelfTime(stampedRoot, total);
            var summary = new CpuSample(processId, startedAt, duration, total, hotspots) { SymbolSource = symbolSource, TopSelfTime = topSelfTime };
            var artifact = new CpuSampleTraceArtifact(
                processId, startedAt, duration, total, stampedRoot, null, identities, symbolSource);
            return new CpuSampleResult(summary, artifact);
        }
        finally
        {
            TryDelete(perfDataPath);
        }
    }

    /// <summary>
    /// Resolves the target's on-disk native image (<c>/proc/&lt;pid&gt;/exe</c>) so the emitted
    /// <see cref="MethodIdentity"/> carries a module name + path hint for the consumer. Best-effort:
    /// returns <c>(null, null)</c> when the link is unreadable (the identity then omits the hint, and
    /// the <c>(TypeFullName, MethodName)</c> half still drives the handoff). The path is a hint only —
    /// see <c>docs/handoff-contract.md</c> on untrusted path hints.
    /// </summary>
    private static (string? ModuleName, string? ModulePath) TryResolveTargetModule(int processId)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return (null, null);
        }

        try
        {
            var exeLink = $"/proc/{processId}/exe";
            var target = File.ResolveLinkTarget(exeLink, returnFinalTarget: true)?.FullName;
            if (string.IsNullOrEmpty(target))
            {
                return (null, null);
            }

            return (Path.GetFileName(target), target);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    private async Task RecordAsync(int pid, string outputPath, TimeSpan duration, CancellationToken ct)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds));
        // NativeAOT binaries may not always emit frame pointers; dwarf unwinding is required
        // for reliable callstacks. The trade-off is larger perf.data files, but the cap is
        // implicit in the sampling window. See https://github.com/pedrosakuma/dotnet-diagnostics
        // notes on NativeAOT validation for the bug history.
        var args = $"record -F {_samplingFrequencyHz} --call-graph dwarf -p {pid} -o \"{outputPath}\" -- sleep {seconds}";
        _logger.LogDebug("Spawning perf: {Bin} {Args}", ResolvePerfPath()!, args);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ResolvePerfPath()!,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
            EnableRaisingEvents = true,
        };

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { /* best effort */ }
            throw;
        }

        await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"perf record exited with code {process.ExitCode}. stderr: {stderr.Trim()}");
        }
    }

    private async Task<string> RunScriptAsync(string perfDataPath, CancellationToken ct)
    {
        // --no-inline keeps line cost predictable; symbols are already demangled by default.
        var args = $"script -i \"{perfDataPath}\" --no-inline";

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ResolvePerfPath()!,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { /* best effort */ }
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"perf script exited with code {process.ExitCode}. stderr: {stderr.Trim()}");
        }

        return stdout;
    }

    internal static (long Total, IReadOnlyList<Hotspot> Hotspots, CallTreeNode Root, NativeAotSymbolDemangler.SymbolSource SymbolSource, IReadOnlyDictionary<SymbolRef, MethodIdentity> Identities) Aggregate(
        string perfScriptOutput, int processId, int topN,
        NativeAotMethodMap? methodMap = null, string? moduleName = null, string? modulePath = null)
    {
        var samples = PerfScriptParser.Parse(perfScriptOutput, processId);
        var inclusive = new Dictionary<string, long>(StringComparer.Ordinal);
        var exclusive = new Dictionary<string, long>(StringComparer.Ordinal);
        var modules = new Dictionary<string, string>(StringComparer.Ordinal);
        var displayCache = new Dictionary<string, string>(StringComparer.Ordinal);
        var identities = new Dictionary<SymbolRef, MethodIdentity>();
        var builder = new CallTreeBuilder();
        long total = 0;
        var aggregatedSource = NativeAotSymbolDemangler.SymbolSource.Unknown;
        var anyMangledFrameDemangled = false;

        foreach (var sample in samples)
        {
            if (sample.Frames.Count == 0) continue;
            total++;

            // perf prints leaf→root; reverse to root→leaf for tree traversal. Demangle each
            // symbol once and cache so the hotspot map + call tree share identical display strings.
            var rootToLeaf = new List<(string Key, string Module, string Display)>(sample.Frames.Count);
            for (var i = sample.Frames.Count - 1; i >= 0; i--)
            {
                var f = sample.Frames[i];
                var classification = NativeAotSymbolDemangler.Classify(f.Symbol);
                aggregatedSource = NativeAotSymbolDemangler.Combine(aggregatedSource, classification);
                if (!displayCache.TryGetValue(f.Symbol, out var demangled))
                {
                    demangled = NativeAotSymbolDemangler.Demangle(f.Symbol);
                    displayCache[f.Symbol] = demangled;
                    if (classification == NativeAotSymbolDemangler.SymbolSource.ElfMangled &&
                        !ReferenceEquals(demangled, f.Symbol) &&
                        !string.Equals(demangled, f.Symbol, StringComparison.Ordinal))
                    {
                        anyMangledFrameDemangled = true;
                    }
                }
                var key = string.IsNullOrEmpty(f.Module) ? demangled : f.Module + "!" + demangled;
                rootToLeaf.Add((key, f.Module, demangled));
                modules.TryAdd(key, f.Module);

                // #395: gate MethodIdentity emission on the map. The raw perf symbol (f.Symbol,
                // mangled, offset already stripped) matches the map's MethodCode Name byte-for-byte;
                // only genuine managed method bodies get a name-based handoff identity.
                if (methodMap is not null && methodMap.ContainsMethod(f.Symbol))
                {
                    var symbolRef = new SymbolRef(f.Module, demangled);
                    if (!identities.ContainsKey(symbolRef))
                    {
                        identities[symbolRef] = BuildAotIdentity(f.Symbol, demangled, moduleName, modulePath);
                    }
                }
            }

            var leafKey = rootToLeaf[^1].Key;
            exclusive[leafKey] = exclusive.GetValueOrDefault(leafKey) + 1;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (k, _, _) in rootToLeaf)
            {
                if (seen.Add(k))
                {
                    inclusive[k] = inclusive.GetValueOrDefault(k) + 1;
                }
            }

            builder.AddStack(rootToLeaf, leafKey);
        }

        var ranked = inclusive
            .OrderByDescending(kv => kv.Value)
            .Take(topN)
            .ToArray();

        var hotspots = ranked
            .Select(kv =>
            {
                var module = modules.GetValueOrDefault(kv.Key, string.Empty);
                var display = !string.IsNullOrEmpty(module) && kv.Key.StartsWith(module + "!", StringComparison.Ordinal)
                    ? kv.Key[(module.Length + 1)..]
                    : kv.Key;
                identities.TryGetValue(new SymbolRef(module, display), out var identity);
                return new Hotspot(
                    Frame: new SampledFrame(Module: module, Method: display),
                    InclusiveSamples: kv.Value,
                    ExclusiveSamples: exclusive.GetValueOrDefault(kv.Key),
                    Identity: identity);
            })
            .ToList();

        // Promote ElfMangled → ElfDemangled only when at least one mangled frame was actually
        // rewritten by the demangler (review-fix: previous "any cached display changed" check
        // could promote on the back of an unrelated non-mangled frame).
        if (anyMangledFrameDemangled)
        {
            aggregatedSource = NativeAotSymbolDemangler.Combine(
                aggregatedSource,
                NativeAotSymbolDemangler.SymbolSource.ElfDemangled);
        }

        IReadOnlyDictionary<SymbolRef, MethodIdentity> identityView = identities;
        return (total, hotspots, builder.Build(), aggregatedSource, identityView);
    }

    /// <summary>
    /// Builds a name-based <see cref="MethodIdentity"/> for a NativeAOT frame confirmed (via the ILC
    /// map file) to be a managed method body (issue #395). The canonical CoreCLR
    /// <c>(ModuleVersionId, MetadataToken)</c> handoff does not apply to AOT — there is no IL metadata
    /// token at runtime — so those stay <c>null</c> and the consumer resolves by
    /// <c>(TypeFullName, MethodName)</c> against the same map / symbol table. <c>GenericArity</c> is
    /// reported as 0 because it cannot be recovered reliably from the mangled name alone.
    /// </summary>
    internal static MethodIdentity BuildAotIdentity(string mangledSymbol, string demangledDisplay, string? moduleName, string? modulePath)
    {
        var (typeFullName, methodName) = NativeAotSymbolDemangler.SplitTypeAndMethod(demangledDisplay);
        return new MethodIdentity(
            MethodName: string.IsNullOrEmpty(methodName) ? mangledSymbol : methodName,
            GenericArity: 0,
            ModuleName: moduleName,
            ModulePath: modulePath,
            ModuleVersionId: null,
            MetadataToken: null,
            TypeFullName: typeFullName);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
