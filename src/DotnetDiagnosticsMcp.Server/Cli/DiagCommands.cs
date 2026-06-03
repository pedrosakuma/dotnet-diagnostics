using System.Globalization;
using System.Text;
using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Core.Activities;
using DotnetDiagnosticsMcp.Core.Capabilities;
using DotnetDiagnosticsMcp.Core.Contention;
using DotnetDiagnosticsMcp.Core.Counters;
using DotnetDiagnosticsMcp.Core.Db;
using DotnetDiagnosticsMcp.Core.Drilldown;
using DotnetDiagnosticsMcp.Core.Dump;
using DotnetDiagnosticsMcp.Core.EventSources;
using DotnetDiagnosticsMcp.Core.Exceptions;
using DotnetDiagnosticsMcp.Core.Gc;
using DotnetDiagnosticsMcp.Core.Jit;
using DotnetDiagnosticsMcp.Core.Logs;
using DotnetDiagnosticsMcp.Core.ProcessDiscovery;
using DotnetDiagnosticsMcp.Core.Security;
using DotnetDiagnosticsMcp.Core.ThreadPool;
using DotnetDiagnosticsMcp.Core.UseCases;
using DotnetDiagnosticsMcp.Server.Security;
using DotnetDiagnosticsMcp.Server.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetDiagnosticsMcp.Server.Cli;

/// <summary>
/// Outcome of a single <c>diag</c> command handler (issue #287): the raw
/// <see cref="DotnetDiagnosticsMcp.Core.DiagnosticResult{T}"/> envelope (boxed for JSON) plus a
/// pre-rendered human table, and whether the envelope is an error.
/// </summary>
internal sealed record DiagCommandResult(bool IsError, bool Cancelled, object Envelope, string Human);

/// <summary>
/// The <c>diag</c> sub-command handlers (issue #287, Phase-0 spike of #283). Each handler runs one
/// host-neutral use case against the shared Core engine and returns a <see cref="DiagCommandResult"/>.
/// Handlers never call <c>Environment.Exit</c> and never own the process lifecycle — the host-mode
/// shell (<see cref="DiagHostMode"/>) owns exit codes and Ctrl-C cancellation.
/// </summary>
internal static class DiagCommands
{
    /// <summary>The commands wired by the Phase-0 spike, in help-listing order.</summary>
    public static readonly IReadOnlyList<string> Commands = new[]
    {
        "processes",
        "capabilities",
        "collect",
        "inspect-heap",
    };

    public static async Task<DiagCommandResult> RunAsync(
        IServiceProvider services,
        DiagOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        return options.Command switch
        {
            "processes" => Processes(services),
            "capabilities" => await CapabilitiesAsync(services, options, cancellationToken).ConfigureAwait(false),
            "collect" => await CollectAsync(services, options, cancellationToken).ConfigureAwait(false),
            "inspect-heap" => await InspectHeapAsync(services, options, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException($"Unknown command '{options.Command}'.", nameof(options)),
        };
    }

    private static DiagCommandResult Processes(IServiceProvider services)
    {
        var discovery = services.GetRequiredService<IProcessDiscovery>();
        var result = ProcessInspectionUseCases.ListProcesses(discovery);

        var human = RenderEnvelope(result, (sb, processes) =>
        {
            if (processes.Count == 0)
            {
                return;
            }

            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"{"PID",-8} {"RUNTIME",-16} {"OS/ARCH",-16} ENTRYPOINT");
            foreach (var p in processes)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"{p.ProcessId,-8} {Trunc(p.RuntimeVersion, 16),-16} {Trunc($"{p.OperatingSystem}/{p.ProcessArchitecture}", 16),-16} {p.ManagedEntrypointAssemblyName ?? "<unknown>"}");
            }
        });

        return new DiagCommandResult(result.IsError, result.Cancelled, result, human);
    }

    private static async Task<DiagCommandResult> CapabilitiesAsync(
        IServiceProvider services,
        DiagOptions options,
        CancellationToken cancellationToken)
    {
        var detector = services.GetRequiredService<ICapabilityDetector>();
        var resolver = services.GetRequiredService<IProcessContextResolver>();
        var result = await ProcessInspectionUseCases
            .GetCapabilitiesAsync(detector, resolver, options.Pid, cancellationToken)
            .ConfigureAwait(false);

        var human = RenderEnvelope(result, (sb, caps) =>
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Runtime           : {caps.Runtime} {caps.RuntimeVersion}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  CPU sampling      : {caps.CanSampleCpu}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  GC dump           : {caps.CanCollectGcDump}");
            if (!string.IsNullOrWhiteSpace(caps.Notes))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Notes             : {caps.Notes}");
            }
        });

        return new DiagCommandResult(result.IsError, result.Cancelled, result, human);
    }

    private static async Task<DiagCommandResult> CollectAsync(
        IServiceProvider services,
        DiagOptions options,
        CancellationToken cancellationToken)
    {
        var kind = string.IsNullOrWhiteSpace(options.Kind) ? "counters" : options.Kind;

        // Reuse the unified MCP collector entry point verbatim — the seam under test (#283 / #287).
        // requestContext: null makes CollectionProgressTicker a no-op (no MCP progress channel); the
        // synthetic root principal keeps every per-kind [RequireScope] re-check satisfied off-transport.
        var result = await CollectEventsTool.CollectEvents(
            services.GetRequiredService<ICounterCollector>(),
            services.GetRequiredService<IExceptionCollector>(),
            services.GetRequiredService<IGcCollector>(),
            services.GetRequiredService<IActivityCollector>(),
            services.GetRequiredService<IEventSourceCollector>(),
            services.GetRequiredService<ILogCollector>(),
            services.GetRequiredService<IJitCollector>(),
            services.GetRequiredService<IThreadPoolCollector>(),
            services.GetRequiredService<IContentionCollector>(),
            services.GetRequiredService<IDbCollector>(),
            services.GetRequiredService<IProcessContextResolver>(),
            services.GetRequiredService<IDiagnosticHandleStore>(),
            services.GetRequiredService<EventSourceAllowlist>(),
            services.GetRequiredService<SensitiveValueGate>(),
            StdioRootPrincipalAccessor.Instance,
            kind: kind,
            processId: options.Pid,
            durationSeconds: options.DurationSeconds,
            providerName: options.Provider,
            requestContext: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var human = RenderEnvelope(result, (sb, env) =>
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"  kind: {env.Kind}");
        });

        return new DiagCommandResult(result.IsError, result.Cancelled, result, human);
    }

    private static async Task<DiagCommandResult> InspectHeapAsync(
        IServiceProvider services,
        DiagOptions options,
        CancellationToken cancellationToken)
    {
        var source = string.IsNullOrWhiteSpace(options.Source) ? "live" : options.Source;

        var result = await InspectHeapTool.InspectHeap(
            services.GetRequiredService<IDumpInspector>(),
            services.GetRequiredService<IDiagnosticHandleStore>(),
            services.GetRequiredService<IProcessContextResolver>(),
            services.GetRequiredService<SymbolServerAllowlist>(),
            StdioRootPrincipalAccessor.Instance,
            source: source,
            processId: options.Pid,
            dumpFilePath: options.DumpPath,
            topTypes: options.Top ?? 20,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var human = RenderEnvelope(result, static (_, _) => { });
        return new DiagCommandResult(result.IsError, result.Cancelled, result, human);
    }

    /// <summary>
    /// Renders the host-neutral parts of any <see cref="DiagnosticResult{T}"/> (summary, error,
    /// resolved-process digest, drill-down handle, next-action hints) plus a command-specific data
    /// block supplied by <paramref name="renderData"/> (skipped on error / null payload).
    /// </summary>
    private static string RenderEnvelope<T>(DiagnosticResult<T> result, Action<StringBuilder, T> renderData)
    {
        var sb = new StringBuilder();
        sb.Append(result.IsError ? "ERROR: " : string.Empty);
        sb.AppendLine(result.Summary);

        if (result.Error is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  kind   : {result.Error.Kind}");
            if (!string.IsNullOrWhiteSpace(result.Error.Detail))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  detail : {result.Error.Detail}");
            }
        }

        if (result.ResolvedProcess is { } ctx)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  target : pid {ctx.ProcessId}{(ctx.AutoResolved ? " (auto-resolved)" : string.Empty)}");
        }

        if (!result.IsError && result.Data is not null)
        {
            renderData(sb, result.Data);
        }

        if (result.Handle is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  handle : {result.Handle} (in-memory; drill-down handles are MCP-session scoped and do not survive this one-shot process)");
        }

        if (result.Hints.Count > 0)
        {
            sb.AppendLine("  next:");
            foreach (var hint in result.Hints)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    - {hint.NextTool}: {hint.Reason}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string Trunc(string? value, int max)
    {
        value ??= string.Empty;
        return value.Length <= max ? value : value[..(max - 1)] + "…";
    }
}
