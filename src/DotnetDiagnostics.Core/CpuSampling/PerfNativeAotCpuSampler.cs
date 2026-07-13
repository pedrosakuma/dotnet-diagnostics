using System.Diagnostics;
using System.Globalization;
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
    private const long PerfDataMaxBytes = 512L * 1024 * 1024;

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
            var aggregate = await RunScriptAsync(
                perfDataPath,
                processId: 0,
                topN,
                methodMap,
                moduleName,
                modulePath,
                cancellationToken).ConfigureAwait(false);
            var stampedRoot = CallTreeIdentityProjector.Stamp(aggregate.Root, aggregate.Identities);
            // Compute the global self-time leader from the SAME (identity-stamped) tree the artifact
            // stores, so the inline signals match the signals:// Resource path.
            var topSelfTime = CpuSampleAnalytics.TopSelfTime(stampedRoot, aggregate.Total);
            var summary = new CpuSample(processId, startedAt, duration, aggregate.Total, aggregate.Hotspots)
            {
                SymbolSource = aggregate.SymbolSource,
                TopSelfTime = topSelfTime,
            };
            var artifact = new CpuSampleTraceArtifact(
                processId, startedAt, duration, aggregate.Total, stampedRoot, null, aggregate.Identities, aggregate.SymbolSource);
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
        // for reliable callstacks. The trade-off is larger perf.data files, so we pair
        // the sampling window with an explicit perf.data size cap. See https://github.com/pedrosakuma/dotnet-diagnostics
        // notes on NativeAOT validation for the bug history.
        var args = $"record -F {_samplingFrequencyHz} --call-graph dwarf --max-size {FormatPerfFileSize(PerfDataMaxBytes)} -p {pid} -o \"{outputPath}\" -- sleep {seconds}";
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

    internal static string FormatPerfFileSize(long bytes)
    {
        if (bytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), "perf max-size must be non-negative.");
        }

        const long oneMiB = 1024 * 1024;
        if (bytes % oneMiB == 0)
        {
            return (bytes / oneMiB).ToString(CultureInfo.InvariantCulture) + "M";
        }

        return bytes.ToString(CultureInfo.InvariantCulture);
    }

    private async Task<PerfScriptAggregationResult> RunScriptAsync(
        string perfDataPath,
        int processId,
        int topN,
        NativeAotMethodMap? methodMap,
        string? moduleName,
        string? modulePath,
        CancellationToken ct)
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
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        try
        {
            var aggregate = await AggregateAsync(
                process.StandardOutput,
                processId,
                topN,
                methodMap,
                moduleName,
                modulePath,
                cancellationToken: ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"perf script exited with code {process.ExitCode}. stderr: {stderr.Trim()}");
            }

            return aggregate;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { /* best effort */ }
            throw;
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(true); } catch { /* best effort */ }
            throw;
        }
    }

    internal static (long Total, IReadOnlyList<Hotspot> Hotspots, CallTreeNode Root, NativeAotSymbolDemangler.SymbolSource SymbolSource, IReadOnlyDictionary<SymbolRef, MethodIdentity> Identities) Aggregate(
        string perfScriptOutput, int processId, int topN,
        NativeAotMethodMap? methodMap = null, string? moduleName = null, string? modulePath = null)
    {
        using var reader = new StringReader(perfScriptOutput);
        var aggregate = AggregateAsync(reader, processId, topN, methodMap, moduleName, modulePath)
            .GetAwaiter()
            .GetResult();
        return (aggregate.Total, aggregate.Hotspots, aggregate.Root, aggregate.SymbolSource, aggregate.Identities);
    }

    internal static async Task<PerfScriptAggregationResult> AggregateAsync(
        TextReader reader,
        int processId,
        int topN,
        NativeAotMethodMap? methodMap = null,
        string? moduleName = null,
        string? modulePath = null,
        long? sampleBudget = null,
        CancellationToken cancellationToken = default)
    {
        var builder = new PerfScriptAggregationBuilder(methodMap, moduleName, modulePath);
        var parseResult = await PerfScriptParser.ParseAsync(
            reader,
            processId,
            sample =>
            {
                builder.AddSample(sample);
                return sampleBudget is null || builder.TotalSamples < sampleBudget.Value;
            },
            cancellationToken).ConfigureAwait(false);
        return builder.Build(topN, truncated: !parseResult.Completed);
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
