using System.Globalization;
using System.Text;
using System.Text.Json;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Comparison;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Investigation;
using DotnetDiagnostics.Core.Memory;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.UseCases;
using DotnetDiagnostics.Core.Drilldown;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetDiagnostics.Cli;

internal static partial class CliCommands
{
    private static async Task<CliCommandResult> CompareAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var snapshots = new List<ComparableSnapshot>(options.ComparePaths.Count);
        foreach (var path in options.ComparePaths)
        {
            ComparableSnapshot? snapshot;
            try
            {
                await using var stream = File.OpenRead(path);
                snapshot = await JsonSerializer.DeserializeAsync(
                    stream,
                    ComparableSnapshotJsonContext.Default.ComparableSnapshot,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
            {
                return BuildResult<object>(DiagnosticResult.Fail<object>(
                    $"compare: failed to read '{path}'.",
                    new DiagnosticError("InvalidSnapshot", ex.Message)), static (_, _) => { });
            }

            if (snapshot is null || !string.Equals(snapshot.Schema, ComparableSnapshot.SchemaV1, StringComparison.Ordinal))
            {
                return BuildResult<object>(DiagnosticResult.Fail<object>(
                    $"compare: '{path}' is not a comparable snapshot v1 JSON file.",
                    new DiagnosticError("InvalidSnapshot", $"Expected schema '{ComparableSnapshot.SchemaV1}'.")), static (_, _) => { });
            }

            snapshots.Add(snapshot);
        }

        if (!JourneyModeParser.TryParse(options.Mode, out var mode))
        {
            return BuildResult<object>(DiagnosticResult.Fail<object>(
                $"compare: unknown --mode '{options.Mode}'.",
                new DiagnosticError("InvalidArgument", "Valid values: trend, dispersion.", nameof(options.Mode))), static (_, _) => { });
        }

        var diff = SnapshotDiffer.Compare(snapshots, mode);
        if (!string.IsNullOrWhiteSpace(options.SavePath))
        {
            try
            {
                var fullPath = Path.GetFullPath(options.SavePath);
                var directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                await using var output = File.Create(fullPath);
                await JsonSerializer.SerializeAsync(
                    output,
                    diff,
                    ComparableSnapshotJsonContext.Default.SnapshotJourneyDiff,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                return BuildResult<object>(DiagnosticResult.Fail<object>(
                    $"compare: failed to write '{options.SavePath}'.",
                    new DiagnosticError("OutputWriteFailure", ex.Message)), static (_, _) => { });
            }
        }

        return new CliCommandResult(IsError: false, Cancelled: false, diff, RenderJourneyDiff(diff));
    }

    private static async Task<CliCommandResult> InvestigateAsync(
        IServiceProvider services,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        var planner = services.GetRequiredService<IInvestigationPlanner>();
        var resolver = services.GetRequiredService<IProcessContextResolver>();

        var resolved = await ProcessResolutionHelpers.ResolveContextAsync<InvestigationPlan>(
            resolver, options.Pid, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null)
        {
            return BuildResult(resolved.Failure, static (_, _) => { });
        }

        var constraints = new InvestigationConstraints(
            MaxToolCalls: options.MaxToolCalls ?? 8);

        var request = new InvestigationRequest(
            ProcessId: resolved.ProcessId,
            Symptom: options.Symptom,
            Hypothesis: options.Hypothesis,
            Constraints: constraints);

        var plan = planner.Plan(request);
        var cliPlan = CliInvestigationProjection.Project(plan);
        var summary = $"Mode={cliPlan.Mode}. Next step #{cliPlan.NextStep.StepNumber}: {cliPlan.NextStep.StepId}. " +
                      $"{cliPlan.AllSteps.Count} total step(s), {cliPlan.EarlyStopConditions.Count} early-stop condition(s). " +
                      $"Honor MaxToolCalls={cliPlan.MaxToolCalls}.";
        var result = ProcessResolutionHelpers.WithContext(DiagnosticResult.Ok(cliPlan, summary), resolved.Context);

        return BuildResult(result, static (sb, plan) =>
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"  investigation-id : {plan.InvestigationId}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  mode             : {plan.Mode}");
            if (!string.IsNullOrWhiteSpace(plan.Symptom))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  symptom          : {plan.Symptom}");
            }

            if (!string.IsNullOrWhiteSpace(plan.Hypothesis))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  hypothesis       : {plan.Hypothesis}");
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"  max-tool-calls   : {plan.MaxToolCalls}");
            sb.AppendLine();
            sb.AppendLine("  next step:");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    #{plan.NextStep.StepNumber} {plan.NextStep.StepId}{FormatStepCommand(plan.NextStep.Command)}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    rationale: {plan.NextStep.Rationale}");
            if (plan.AllSteps.Count > 1)
            {
                sb.AppendLine();
                sb.AppendLine(CultureInfo.InvariantCulture, $"  all steps ({plan.AllSteps.Count}):");
                foreach (var step in plan.AllSteps)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    #{step.StepNumber} [{step.Status}] {step.StepId}{FormatStepCommand(step.Command)}");
                }
            }

            if (plan.EarlyStopConditions.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("  early-stop conditions:");
                foreach (var cond in plan.EarlyStopConditions)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    - {cond.Description} → {cond.Action}");
                }
            }
        });
    }

    private static string FormatStepCommand(string? command)
        => string.IsNullOrWhiteSpace(command) ? string.Empty : $" (via {command})";

    private static async Task<CliCommandResult> ExportSummaryAsync(
        IServiceProvider services,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        if (!TryValidateExportSummary(options, out var validationError))
        {
            throw new ArgumentException(validationError, nameof(options));
        }

        var exporter = services.GetRequiredService<IInvestigationSummaryExporter>();
        var handles = services.GetRequiredService<IDiagnosticHandleStore>();

        var lookup = handles.TryGetWithKind(options.Handle!);
        if (lookup is null)
        {
            return BuildResult<ExportedInvestigationSummary>(
                DiagnosticResult.Fail<ExportedInvestigationSummary>(
                    $"Handle '{options.Handle}' is unknown or expired. Collect a CPU sample first with 'collect --kind cpu', then re-run export-summary.",
                    new DiagnosticError("HandleExpired",
                        "Drill-down handles live until the session ends or the target process exits.",
                        options.Handle)),
                static (_, _) => { });
        }

        if (lookup.Value.Artifact is not CpuSampleTraceArtifact artifact)
        {
            return BuildResult<ExportedInvestigationSummary>(
                DiagnosticResult.Fail<ExportedInvestigationSummary>(
                    $"Handle '{options.Handle}' is a '{lookup.Value.Kind}' handle, not a CPU sample. " +
                    "export-summary needs a CPU-sample handle; re-run with a handle from 'collect --kind cpu'.",
                    new DiagnosticError("HandleKindMismatch",
                        "export-summary projects CPU-sample hotspots into a portable investigation summary.",
                        options.Handle)),
                static (_, _) => { });
        }

        var topHotspots = options.TopHotspots ?? 10;
        var exported = exporter.Export(new ExportRequest(
            Handle: options.Handle!,
            Artifact: artifact,
            TopHotspots: topHotspots,
            Format: SummaryFormat.Json));

        if (!string.IsNullOrWhiteSpace(options.OutDir))
        {
            try
            {
                var fullPath = Path.GetFullPath(options.OutDir);
                var dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                // Atomic write: a failure mid-write must never truncate/clobber a pre-existing summary.
                var tempPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    await File.WriteAllTextAsync(tempPath, exported.Rendered, cancellationToken).ConfigureAwait(false);
                    File.Move(tempPath, fullPath, overwrite: true);
                }
                catch
                {
                    TryDeleteQuietly(tempPath);
                    throw;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                return BuildResult<ExportedInvestigationSummary>(
                    DiagnosticResult.Fail<ExportedInvestigationSummary>(
                        $"export-summary: failed to write '{options.OutDir}'.",
                        new DiagnosticError("OutputWriteFailure", ex.Message)),
                    static (_, _) => { });
            }

            var writtenBytes = exported.Rendered.Length;
            var writeSummary = $"Exported investigation summary {exported.Summary.InvestigationId} ({writtenBytes} chars) to {options.OutDir}.";
            return BuildResult(DiagnosticResult.Ok(exported, writeSummary), (sb, e) =>
            {
                sb.AppendLine();
                sb.AppendLine(CultureInfo.InvariantCulture, $"  written to : {options.OutDir}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  id         : {e.Summary.InvestigationId}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  hotspots   : {e.Summary.Findings.TopHotspots.Count}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  size       : {writtenBytes} chars");
            });
        }

        // stdout mode: emit exactly the portable summary document (verbatim, pipe-able), identical to
        // what --out persists. Both --json and human paths print the same portable JSON — never a
        // decorated human envelope that a consumer would have to strip.
        return RawJsonResult(exported.Rendered);
    }

    private static CliCommandResult RawJsonResult(string json)
    {
        using var document = JsonDocument.Parse(json);
        var element = document.RootElement.Clone();
        return new CliCommandResult(IsError: false, Cancelled: false, Envelope: element, Human: json)
        {
            RawHuman = true,
        };
    }

    private static void TryDeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of the temp file; the original write failure is already surfaced.
        }
    }

}
