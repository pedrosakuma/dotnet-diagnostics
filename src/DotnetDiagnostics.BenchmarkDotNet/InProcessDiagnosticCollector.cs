using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Contention;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Db;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.EventSources;
using DotnetDiagnostics.Core.Exceptions;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Hosting;
using DotnetDiagnostics.Core.Jit;
using DotnetDiagnostics.Core.Logs;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.ThreadPool;
using DotnetDiagnostics.Core.UseCases;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetDiagnostics.BenchmarkDotNet;

/// <summary>
/// Runs a single dotnet-diagnostics <c>collect</c> kind <b>in-process</b> against a target PID by
/// composing the Core engine (<see cref="DiagnosticCoreServiceRegistration.AddDiagnosticCoreServices"/>)
/// into a private <see cref="ServiceProvider"/> and invoking the matching
/// <see cref="EventCollectionUseCases"/> entry point. The heavy ClrMD/TraceEvent dependencies load
/// in <i>this</i> (the BenchmarkDotNet orchestrator) process, never in the measured child — so the
/// collection does not contaminate the benchmark's timing or allocations.
/// </summary>
internal sealed class InProcessDiagnosticCollector : IDisposable
{
    /// <summary>
    /// The <c>collect</c> kinds the diagnoser can dispatch in-process. Mirrors the CLI's
    /// <c>CollectKinds</c> minus <c>event_source</c> (needs an explicit provider name and is
    /// not benchmark-relevant) and <c>startup</c> (the diagnoser attaches after the benchmark
    /// host is already running, so it cannot observe cold-start loader/DI events).
    /// </summary>
    public static readonly IReadOnlySet<string> SupportedKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "counters",
        "exceptions",
        "gc",
        "cpu",
        "allocation",
        "datas",
        "catalog",
        "activities",
        "logs",
        "jit",
        "threadpool",
        "contention",
        "db",
    };

    /// <summary>
    /// Number of top hotspots the in-process CPU sampler retains. The full call tree is always
    /// available behind the issued handle; this only bounds the inline hotspot list.
    /// </summary>
    private const int CpuTopHotspots = 25;

    /// <summary>Number of top types (by bytes and by count) the allocation summary retains.</summary>
    private const int AllocationTopTypes = 25;

    /// <summary>TTL for the CPU-sample drill-down handle registered in the in-process store.</summary>
    private static readonly TimeSpan CpuSampleHandleTtl = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lazy<ServiceProvider> _provider = new(BuildProvider, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool IsSupported(string kind) => SupportedKinds.Contains(kind);

    /// <summary>
    /// Collects a single kind against <paramref name="processId"/> and projects the Core envelope
    /// into a <see cref="KindCapture"/> (a serialized JSON artifact plus a one-line headline).
    /// </summary>
    public async Task<KindCapture> CollectAsync(int processId, string kind, int durationSeconds, CancellationToken cancellationToken)
    {
        if (!SupportedKinds.Contains(kind))
        {
            return KindCapture.Unsupported(kind);
        }

        var services = _provider.Value;
        var resolver = services.GetRequiredService<IProcessContextResolver>();
        var handles = services.GetRequiredService<IDiagnosticHandleStore>();

        return kind switch
        {
            "counters" => Materialize(kind, await EventCollectionUseCases.SnapshotCounters(
                services.GetRequiredService<ICounterCollector>(), resolver, handles, processId, durationSeconds, cancellationToken: cancellationToken).ConfigureAwait(false)),
            "exceptions" => Materialize(kind, await EventCollectionUseCases.CollectExceptions(
                services.GetRequiredService<IExceptionCollector>(), resolver, handles, processId, durationSeconds, cancellationToken: cancellationToken).ConfigureAwait(false)),
            "gc" => Materialize(kind, await EventCollectionUseCases.CollectGcEvents(
                services.GetRequiredService<IGcCollector>(), resolver, handles, processId, durationSeconds, cancellationToken: cancellationToken).ConfigureAwait(false)),
            "cpu" => Materialize(kind, await CollectCpuAsync(
                services.GetRequiredService<EventPipeCpuSampler>(), handles, processId, durationSeconds, cancellationToken).ConfigureAwait(false)),
            "allocation" => Materialize(kind, await CollectAllocationAsync(
                services.GetRequiredService<EventPipeAllocationSampler>(), handles, processId, durationSeconds, cancellationToken).ConfigureAwait(false)),
            "datas" => Materialize(kind, await EventCollectionUseCases.CollectGcDatas(
                services.GetRequiredService<IGcDatasCollector>(), resolver, handles, processId, durationSeconds, cancellationToken: cancellationToken).ConfigureAwait(false)),
            "catalog" => Materialize(kind, await EventCollectionUseCases.CollectEventCatalog(
                services.GetRequiredService<IEventCatalogCollector>(), resolver, handles, processId, durationSeconds, cancellationToken: cancellationToken).ConfigureAwait(false)),
            "logs" => Materialize(kind, await EventCollectionUseCases.CollectLogs(
                services.GetRequiredService<ILogCollector>(), resolver, handles, processId, durationSeconds, cancellationToken: cancellationToken).ConfigureAwait(false)),
            "jit" => Materialize(kind, await EventCollectionUseCases.CollectJit(
                services.GetRequiredService<IJitCollector>(), resolver, handles, processId, durationSeconds, cancellationToken: cancellationToken).ConfigureAwait(false)),
            "threadpool" => Materialize(kind, await EventCollectionUseCases.CollectThreadPool(
                services.GetRequiredService<IThreadPoolCollector>(), resolver, handles, processId, durationSeconds, cancellationToken: cancellationToken).ConfigureAwait(false)),
            "contention" => Materialize(kind, await EventCollectionUseCases.CollectContention(
                services.GetRequiredService<IContentionCollector>(), resolver, handles, processId, durationSeconds, cancellationToken: cancellationToken).ConfigureAwait(false)),
            "db" => Materialize(kind, await EventCollectionUseCases.CollectDb(
                services.GetRequiredService<IDbCollector>(), resolver, handles, processId, durationSeconds, cancellationToken: cancellationToken).ConfigureAwait(false)),
            "activities" => Materialize(kind, await EventCollectionUseCases.CollectActivities(
                services.GetRequiredService<IActivityCollector>(), resolver, handles, processId, durationSeconds: durationSeconds, cancellationToken: cancellationToken).ConfigureAwait(false)),
            _ => KindCapture.Unsupported(kind),
        };
    }

    private static KindCapture Materialize<T>(string kind, DiagnosticResult<T> result)
    {
        // result has compile-time type DiagnosticResult<T> (concrete per use-case), so the full
        // payload graph is serialized — unlike a System.Text.Json call over a boxed `object`.
        var json = JsonSerializer.Serialize(result, JsonOptions);
        var headline = result.IsError
            ? $"{result.Error!.Kind}: {result.Error.Message}"
            : result.Summary;
        return new KindCapture(kind, result.IsError, result.Summary, headline, json);
    }

    /// <summary>
    /// CPU sampling has no <see cref="EventCollectionUseCases"/> facade (it lives in the MCP tool
    /// layer), so the diagnoser drives <see cref="EventPipeCpuSampler"/> directly. Source-line and
    /// generic-instantiation resolution are intentionally disabled: the former risks PDB/SourceLink
    /// I/O (and network), the latter needs a ClrMD ptrace attach — neither is wanted inside a
    /// benchmark run. The captured hotspots still carry per-frame exclusive/inclusive sample counts
    /// (the "self vs subtree" cost) and the full caller→callee tree is retained behind the handle.
    /// </summary>
    private static async Task<DiagnosticResult<CpuSample>> CollectCpuAsync(
        EventPipeCpuSampler sampler,
        IDiagnosticHandleStore handles,
        int processId,
        int durationSeconds,
        CancellationToken cancellationToken)
    {
        CpuSampleResult result;
        try
        {
            result = await sampler.SampleAsync(
                processId,
                TimeSpan.FromSeconds(durationSeconds),
                CpuTopHotspots,
                sourceResolution: null,
                methodInstantiationResolution: null,
                nativeAotSymbols: null,
                exportTrace: false,
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("NativeAOT", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("SampleProfiler", StringComparison.OrdinalIgnoreCase))
        {
            // The SampleProfiler provider is CoreCLR-only; a NativeAOT benchmark child can't be
            // CPU-sampled in-process. Surface as NotSupported rather than a hard failure.
            return DiagnosticResult.Fail<CpuSample>(
                ex.Message,
                new DiagnosticError("NotSupported", ex.Message, ex.GetType().FullName));
        }

        var handle = handles.Register(processId, "cpu-sample", result.Artifact, CpuSampleHandleTtl);
        var summary = BuildCpuSummary(result.Summary, result.Artifact.Root, durationSeconds);
        return DiagnosticResult.OkWithHandle(
            result.Summary,
            summary,
            handle.Id,
            handle.ExpiresAt,
            new NextActionHint(
                "query_snapshot",
                "Walk the merged caller\u2192callee tree built from the same samples.",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "call-tree", ["maxDepth"] = 8, ["maxNodes"] = 200 }));
    }

    /// <summary>
    /// Builds the one-line CPU headline used by the offenders report. Exposed (internal) so the
    /// test project can assert the phrasing without a live sampler. Reports the hottest frame by
    /// self-cost (exclusive samples) and its share of the window.
    /// </summary>
    /// <remarks>
    /// The hottest self-cost frame is selected from the full caller→callee tree
    /// (<paramref name="root"/>), not from <see cref="CpuSample.TopHotspots"/>: that list is
    /// ranked by <em>inclusive</em> samples and truncated to the top-N, so a hot leaf can be
    /// excluded before its (exact) exclusive count is ever considered. Tree exclusive samples are
    /// leaf-attributed, so aggregating them per method across the tree is exact. The dedup'd
    /// inclusive figure is preferred from <see cref="CpuSample.TopHotspots"/> when the chosen
    /// method is present there, falling back to the tree-summed inclusive otherwise.
    /// </remarks>
    internal static string BuildCpuSummary(CpuSample sample, CallTreeNode? root, int durationSeconds)
    {
        (string Method, long Exclusive, long Inclusive)? hottest = FindHottestSelfCost(root);
        if (hottest is null && sample.TopHotspots.Count > 0)
        {
            var top = sample.TopHotspots.MaxBy(h => h.ExclusiveSamples)!;
            hottest = (top.Frame.Method, top.ExclusiveSamples, top.InclusiveSamples);
        }

        if (sample.TotalSamples <= 0 || hottest is null || hottest.Value.Exclusive <= 0)
        {
            return $"Captured {sample.TotalSamples} sample(s) over {durationSeconds}s but no method aggregation surfaced " +
                "\u2014 increase durationSeconds or verify the benchmark is CPU-bound during the window.";
        }

        var (method, exclusive, treeInclusive) = hottest.Value;
        var inclusive = sample.TopHotspots
            .FirstOrDefault(h => string.Equals(h.Frame.Method, method, StringComparison.Ordinal))?.InclusiveSamples
            ?? treeInclusive;
        var exclusivePercent = 100.0 * exclusive / sample.TotalSamples;
        return $"Captured {sample.TotalSamples} sample(s) over {durationSeconds}s across {sample.TopHotspots.Count} hotspot(s). " +
            $"Hottest self-cost: {method} ({exclusivePercent:F1}% exclusive \u2014 {exclusive} self / {inclusive} inclusive sample(s)).";
    }

    /// <summary>
    /// Aggregates exclusive (self) and inclusive samples per method across the full call tree and
    /// returns the method with the greatest self-cost. Exclusive samples are leaf-attributed so
    /// the per-method sum is exact; inclusive is summed best-effort (may over-count recursion).
    /// Returns <c>null</c> when the tree is absent or carries no self-cost.
    /// </summary>
    internal static (string Method, long Exclusive, long Inclusive)? FindHottestSelfCost(CallTreeNode? root)
    {
        if (root is null)
        {
            return null;
        }

        var byMethod = new Dictionary<(string Module, string Method), (long Exclusive, long Inclusive)>();
        var stack = new Stack<CallTreeNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            var key = (node.Frame.Module, node.Frame.Method);
            var agg = byMethod.GetValueOrDefault(key);
            byMethod[key] = (agg.Exclusive + node.ExclusiveSamples, agg.Inclusive + node.InclusiveSamples);
            foreach (var child in node.Children)
            {
                stack.Push(child);
            }
        }

        if (byMethod.Count == 0)
        {
            return null;
        }

        var best = byMethod.MaxBy(kv => kv.Value.Exclusive);
        if (best.Value.Exclusive <= 0)
        {
            return null;
        }

        return (best.Key.Method, best.Value.Exclusive, best.Value.Inclusive);
    }

    /// <summary>
    /// Allocation sampling (<c>GCAllocationTick</c>) likewise has no <see cref="EventCollectionUseCases"/>
    /// facade, so the diagnoser drives <see cref="EventPipeAllocationSampler"/> directly: it aggregates
    /// per-type allocated bytes / event counts (SOH vs LOH) and retains the merged allocation call-site
    /// tree behind a handle. The sampler is <b>observe-only</b> — it enables a native EventPipe keyword
    /// and reads events the target runtime already emits; it performs no managed allocation in the
    /// measured process, so it does not inflate the benchmark's own <c>Allocated</c> counter. With the
    /// default (out-of-process) toolchain the sampler also runs in this orchestrator process, never the
    /// measured child. The only co-located case (in-process toolchain) is flagged in the summary.
    /// </summary>
    private static async Task<DiagnosticResult<AllocationSample>> CollectAllocationAsync(
        EventPipeAllocationSampler sampler,
        IDiagnosticHandleStore handles,
        int processId,
        int durationSeconds,
        CancellationToken cancellationToken)
    {
        var result = await sampler.SampleAsync(
            processId,
            TimeSpan.FromSeconds(durationSeconds),
            AllocationTopTypes,
            cancellationToken).ConfigureAwait(false);

        var coLocated = processId == Environment.ProcessId;
        var handle = handles.Register(
            processId, "allocation-sample", new AllocationSampleArtifact(result.Summary, result.Artifact), CpuSampleHandleTtl);
        var summary = BuildAllocationSummary(result.Summary, durationSeconds, coLocated);
        return DiagnosticResult.OkWithHandle(
            result.Summary,
            summary,
            handle.Id,
            handle.ExpiresAt,
            new NextActionHint(
                "query_snapshot",
                "Walk the merged allocation call-site tree to find which code paths allocate the most.",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "call-tree", ["maxDepth"] = 8, ["maxNodes"] = 200 }));
    }

    /// <summary>
    /// Builds the one-line allocation headline used by the offenders report. Exposed (internal) so the
    /// test project can assert the phrasing without a live sampler. Reports total events/bytes and the
    /// top allocating type by bytes, with the NativeAOT (<c>&lt;unknown&gt;</c> TypeName) caveat. When
    /// <paramref name="coLocated"/> is true the benchmark shared this process (in-process toolchain),
    /// so the numbers are not isolated from <c>MemoryDiagnoser</c> — that is flagged explicitly.
    /// </summary>
    internal static string BuildAllocationSummary(AllocationSample sample, int durationSeconds, bool coLocated)
    {
        string headline;
        if (sample.TotalEvents <= 0 || sample.TopByBytes.Count == 0)
        {
            headline = $"Captured {sample.TotalEvents} allocation event(s) over {durationSeconds}s but no type aggregation surfaced " +
                "\u2014 increase durationSeconds or drive a workload that allocates during the window.";
        }
        else
        {
            var top = sample.TopByBytes[0];
            var unknownOnly = string.Equals(top.TypeName, "<unknown>", StringComparison.Ordinal) && sample.TopByBytes.Count == 1;
            headline = unknownOnly
                ? $"Captured {sample.TotalEvents} allocation event(s) ({sample.TotalBytes:N0} bytes) over {durationSeconds}s, " +
                    "but TypeName was empty for all events (expected on NativeAOT). Drill into call sites via the call-tree handle."
                : $"Captured {sample.TotalEvents} allocation event(s) ({sample.TotalBytes:N0} bytes) over {durationSeconds}s across {sample.TopByBytes.Count} type(s). " +
                    $"Top by bytes: {top.TypeName} ({top.TotalBytes:N0} bytes, {top.EventCount} event(s), {top.DominantKind} heap).";

            if (sample.TopBySite.Count > 0)
            {
                var site = sample.TopBySite[0];
                var where = string.IsNullOrEmpty(site.Frame.Module)
                    ? site.Frame.Method
                    : $"{site.Frame.Module}!{site.Frame.Method}";
                headline += $" Top site: {where} ({site.TotalBytes:N0} bytes, {site.EventCount} event(s)).";
            }
        }

        return coLocated
            ? headline + " \u26a0 benchmark ran in-process with the diagnoser (in-process toolchain) \u2014 these allocations " +
                "are NOT isolated from MemoryDiagnoser; use the default out-of-process toolchain (or a separate diagnostic job) for clean numbers."
            : headline;
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // Benchmark diagnosis never opts into sensitive heap values or EventSource allowlists, so
        // the default (safe) SecurityOptions is sufficient. Core stays configuration-free.
        services.AddDiagnosticCoreServices(new SecurityOptions());
        return services.BuildServiceProvider();
    }

    public void Dispose()
    {
        if (_provider.IsValueCreated)
        {
            _provider.Value.Dispose();
        }
    }
}

/// <summary>
/// The projection of a single in-process collection: the Core envelope's summary plus a serialized
/// JSON artifact and a one-line headline suitable for the offenders report.
/// </summary>
internal sealed record KindCapture(string Kind, bool IsError, string Summary, string Headline, string Json)
{
    public static KindCapture Unsupported(string kind) => new(
        kind,
        IsError: true,
        Summary: $"Unsupported collect kind '{kind}'.",
        Headline: $"unsupported kind '{kind}'",
        Json: $"{{ \"error\": \"unsupported collect kind '{kind}'\" }}");
}
