using System.Globalization;
using System.Text;
using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Core.Capabilities;
using DotnetDiagnosticsMcp.Core.ProcessDiscovery;
using DotnetDiagnosticsMcp.Core.UseCases;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetDiagnosticsMcp.Cli;

/// <summary>
/// Outcome of a single CLI command handler: the raw
/// <see cref="DotnetDiagnosticsMcp.Core.DiagnosticResult{T}"/> envelope (boxed for JSON) plus a
/// pre-rendered human table, and whether the envelope is an error.
/// </summary>
internal sealed record CliCommandResult(bool IsError, bool Cancelled, object Envelope, string Human);

/// <summary>
/// The standalone CLI sub-command handlers (issue #288). Each handler runs one host-neutral use
/// case from <see cref="ProcessInspectionUseCases"/> against the shared Core engine and returns a
/// <see cref="CliCommandResult"/>. Handlers never call <c>Environment.Exit</c> and never own the
/// process lifecycle — <see cref="CliHost"/> owns exit codes and Ctrl-C cancellation.
/// </summary>
internal static class CliCommands
{
    /// <summary>The commands wired in this slice (#288 PR1), in help-listing order.</summary>
    public static readonly IReadOnlyList<string> Commands = new[]
    {
        "processes",
        "capabilities",
    };

    public static async Task<CliCommandResult> RunAsync(
        IServiceProvider services,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        return options.Command switch
        {
            "processes" => Processes(services),
            "capabilities" => await CapabilitiesAsync(services, options, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException($"Unknown command '{options.Command}'.", nameof(options)),
        };
    }

    private static CliCommandResult Processes(IServiceProvider services)
    {
        var discovery = services.GetRequiredService<IProcessDiscovery>();
        var result = ProcessInspectionUseCases.ListProcesses(discovery);

        var human = RenderEnvelope(result, static (sb, processes) =>
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

        return new CliCommandResult(result.IsError, result.Cancelled, result, human);
    }

    private static async Task<CliCommandResult> CapabilitiesAsync(
        IServiceProvider services,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        var detector = services.GetRequiredService<ICapabilityDetector>();
        var resolver = services.GetRequiredService<IProcessContextResolver>();
        var result = await ProcessInspectionUseCases
            .GetCapabilitiesAsync(detector, resolver, options.Pid, cancellationToken)
            .ConfigureAwait(false);

        var human = RenderEnvelope(result, static (sb, caps) =>
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

        return new CliCommandResult(result.IsError, result.Cancelled, result, human);
    }

    /// <summary>
    /// Renders the host-neutral parts of any <see cref="DiagnosticResult{T}"/> (summary, error,
    /// resolved-process digest, next-action hints) plus a command-specific data block supplied by
    /// <paramref name="renderData"/> (skipped on error / null payload).
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
