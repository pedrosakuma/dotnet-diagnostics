using System.Globalization;
using System.Text;
using System.Text.Json;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Comparison;
using DotnetDiagnostics.Core.Captures;
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
    private static async Task<CliCommandResult> CompareAsync(IServiceProvider services, CliOptions options, CancellationToken cancellationToken)
    {
        if (options.HasHistoricalReferences)
        {
            try
            {
                var result = await CliDurableCaptures.For(services).Get(options.CaptureRoot).CompareHistoricalAsync(
                    new(new(options.BaselineCaptureId!, options.BaselineArtifactId!),
                        new(options.CandidateCaptureId!, options.CandidateArtifactId!)),
                    CliCaptureRootProvider.CurrentAccess(),
                    static (_, _, _, token) => { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; },
                    cancellationToken).ConfigureAwait(false);
                return new(false, false, result,
                    JsonSerializer.Serialize(result, HistoricalComparisonJsonContext.Default.HistoricalComparisonResult));
            }
            catch (CaptureStoreException exception)
            {
                return BuildResult<object>(DiagnosticResult.Fail<object>("Historical comparison failed.",
                    new DiagnosticError("CaptureStoreError", exception.Message, exception.Code.ToString())), static (_, _) => { });
            }
        }

        if (options.ComparePaths.Count == 2)
        {
            var schemas = new string?[2];
            for (var index = 0; index < options.ComparePaths.Count; index++)
            {
                try
                {
                    await using var stream = File.OpenRead(options.ComparePaths[index]);
                    using var document = await JsonDocument.ParseAsync(
                        stream,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    schemas[index] = document.RootElement.ValueKind == JsonValueKind.Object
                        && document.RootElement.TryGetProperty("schema", out var camelSchema)
                        && camelSchema.ValueKind == JsonValueKind.String
                        ? camelSchema.GetString()
                        : document.RootElement.ValueKind == JsonValueKind.Object
                            && document.RootElement.TryGetProperty("Schema", out var pascalSchema)
                            && pascalSchema.ValueKind == JsonValueKind.String
                            ? pascalSchema.GetString()
                            : null;
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
                {
                    return BuildResult<object>(DiagnosticResult.Fail<object>(
                        $"compare: failed to read '{options.ComparePaths[index]}'.",
                        new DiagnosticError("InvalidSnapshot", ex.Message)), static (_, _) => { });
                }
            }

            if (schemas.Any(schema =>
                    string.Equals(schema, RequestLatencyWindowSet.SchemaV1, StringComparison.Ordinal)))
            {
                if (!schemas.All(schema =>
                        string.Equals(schema, RequestLatencyWindowSet.SchemaV1, StringComparison.Ordinal)))
                {
                    return BuildResult<object>(DiagnosticResult.Fail<object>(
                        "compare: mixed request-latency and snapshot schemas are unsupported.",
                        new DiagnosticError(
                            "MixedSchemas",
                            "Both request-latency inputs must use the request-latency window-set v1 schema.")),
                        static (_, _) => { });
                }
                if (options.Mode is not null)
                {
                    return BuildResult<object>(DiagnosticResult.Fail<object>(
                        "compare: --mode does not apply to request-latency window sets.",
                        new DiagnosticError(
                            "InvalidArgument",
                            "Request-latency comparison has fixed paired-window semantics.",
                            nameof(options.Mode))),
                        static (_, _) => { });
                }

                return await CompareRequestLatencyAsync(options, cancellationToken).ConfigureAwait(false);
            }

            if (schemas.Any(schema =>
                    string.Equals(schema, CpuHotspotCaptureSet.SchemaV1, StringComparison.Ordinal)))
            {
                if (!schemas.All(schema =>
                        string.Equals(schema, CpuHotspotCaptureSet.SchemaV1, StringComparison.Ordinal)))
                {
                    return BuildResult<object>(DiagnosticResult.Fail<object>(
                        "compare: mixed CPU-hotspot and snapshot schemas are unsupported.",
                        new DiagnosticError(
                            "MixedSchemas",
                            "Both CPU-hotspot inputs must use the CPU-hotspot capture-set v1 schema.")),
                        static (_, _) => { });
                }
                if (options.Mode is not null)
                {
                    return BuildResult<object>(DiagnosticResult.Fail<object>(
                        "compare: --mode does not apply to CPU-hotspot capture sets.",
                        new DiagnosticError(
                            "InvalidArgument",
                            "CPU-hotspot comparison has fixed paired-capture semantics.",
                            nameof(options.Mode))),
                        static (_, _) => { });
                }

                return await CompareCpuHotspotAsync(options, cancellationToken).ConfigureAwait(false);
            }
        }

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

    private static async Task<CliCommandResult> CompareRequestLatencyAsync(
        CliOptions options,
        CancellationToken cancellationToken)
    {
        RequestLatencyComparisonResult result;
        try
        {
            var baseline = await ReadRequestLatencyWindowSetAsync(
                options.ComparePaths[0],
                cancellationToken).ConfigureAwait(false);
            var candidate = await ReadRequestLatencyWindowSetAsync(
                options.ComparePaths[1],
                cancellationToken).ConfigureAwait(false);
            result = RequestLatencyComparison.Compare(baseline, candidate);
        }
        catch (Exception ex) when (
            ex is IOException
                or JsonException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException)
        {
            return BuildResult<object>(DiagnosticResult.Fail<object>(
                "compare: request-latency evidence is invalid.",
                new DiagnosticError("InvalidRequestLatencyEvidence", ex.Message)),
                static (_, _) => { });
        }

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
                    result,
                    RequestLatencyComparisonJsonContext.Default.RequestLatencyComparisonResult,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (
                ex is IOException
                    or UnauthorizedAccessException
                    or NotSupportedException
                    or ArgumentException)
            {
                return BuildResult<object>(DiagnosticResult.Fail<object>(
                    $"compare: failed to write '{options.SavePath}'.",
                    new DiagnosticError("OutputWriteFailure", ex.Message)),
                    static (_, _) => { });
            }
        }

        return new(false, false, result, RenderRequestLatencyComparison(result));
    }

    private static async Task<RequestLatencyWindowSet> ReadRequestLatencyWindowSetAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Request-latency window-set file was not found.", path);
        }
        if (info.Length is < 1 or > RequestLatencyComparison.MaximumSerializedWindowSetBytes)
        {
            throw new InvalidDataException(
                $"Request-latency window-set files must be 1..{RequestLatencyComparison.MaximumSerializedWindowSetBytes} bytes.");
        }

        await using var stream = info.OpenRead();
        return await JsonSerializer.DeserializeAsync(
            stream,
            RequestLatencyComparisonJsonContext.Default.RequestLatencyWindowSet,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Request-latency window-set JSON was empty.");
    }

    private static async Task<CliCommandResult> CompareCpuHotspotAsync(
        CliOptions options,
        CancellationToken cancellationToken)
    {
        CpuHotspotComparisonResult result;
        try
        {
            var baseline = await ReadCpuHotspotCaptureSetAsync(
                options.ComparePaths[0],
                cancellationToken).ConfigureAwait(false);
            var candidate = await ReadCpuHotspotCaptureSetAsync(
                options.ComparePaths[1],
                cancellationToken).ConfigureAwait(false);
            result = CpuHotspotComparison.Compare(baseline, candidate);
        }
        catch (Exception ex) when (
            ex is IOException
                or JsonException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException)
        {
            return BuildResult<object>(DiagnosticResult.Fail<object>(
                "compare: CPU-hotspot evidence is invalid.",
                new DiagnosticError("InvalidCpuHotspotEvidence", ex.Message)),
                static (_, _) => { });
        }

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
                    result,
                    CpuHotspotComparisonJsonContext.Default.CpuHotspotComparisonResult,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (
                ex is IOException
                    or UnauthorizedAccessException
                    or NotSupportedException
                    or ArgumentException)
            {
                return BuildResult<object>(DiagnosticResult.Fail<object>(
                    $"compare: failed to write '{options.SavePath}'.",
                    new DiagnosticError("OutputWriteFailure", ex.Message)),
                    static (_, _) => { });
            }
        }

        return new(false, false, result, RenderCpuHotspotComparison(result));
    }

    private static async Task<CpuHotspotCaptureSet> ReadCpuHotspotCaptureSetAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("CPU-hotspot capture-set file was not found.", path);
        }
        if (info.Length is < 1 or > CpuHotspotComparison.MaximumSerializedCaptureSetBytes)
        {
            throw new InvalidDataException(
                $"CPU-hotspot capture-set files must be 1..{CpuHotspotComparison.MaximumSerializedCaptureSetBytes} bytes.");
        }

        await using var stream = info.OpenRead();
        return await JsonSerializer.DeserializeAsync(
            stream,
            CpuHotspotComparisonJsonContext.Default.CpuHotspotCaptureSet,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("CPU-hotspot capture-set JSON was empty.");
    }

    internal static string RenderCpuHotspotComparison(CpuHotspotComparisonResult result)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"CPU-hotspot comparison: availability={ToWireName(result.Availability)}; no performance or causal verdict");
        if (result.UnavailableReason is not null)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"  unavailable reason : {result.UnavailableReason}");
        }
        text.AppendLine(CultureInfo.InvariantCulture,
            $"  paired captures     : matched={result.PairCounts.Matched}; valid-independent-complete={result.PairCounts.ValidIndependentComplete}; unpaired={result.PairCounts.Unpaired}; order-mismatch={result.PairCounts.PairOrderMismatches}; run-mismatch={result.PairCounts.ProcessRunMismatches}; process-mismatch={result.PairCounts.ProcessIdMismatches}; overlapping={result.PairCounts.OverlappingCaptureIntervals}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"  baseline counts     : captures={result.BaselineCounts.Captures}; samples={result.BaselineCounts.TotalSamples}; unresolved-exclusive={result.BaselineCounts.UnresolvedExclusiveSamples}; below-minimum={result.BaselineCounts.BelowMinimumSamples}; selected-method-missing={result.BaselineCounts.MissingSelectedMethod}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"  candidate counts    : captures={result.CandidateCounts.Captures}; samples={result.CandidateCounts.TotalSamples}; unresolved-exclusive={result.CandidateCounts.UnresolvedExclusiveSamples}; below-minimum={result.CandidateCounts.BelowMinimumSamples}; selected-method-missing={result.CandidateCounts.MissingSelectedMethod}");
        if (result.IdentityMismatches.Count > 0)
        {
            text.AppendLine("  compatibility mismatches:");
            foreach (var mismatch in result.IdentityMismatches)
            {
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"    {mismatch.Field}{(mismatch.PairKey is null ? string.Empty : $"[{mismatch.PairKey}]")}: baseline={mismatch.BaselineValue ?? "unavailable"}, candidate={mismatch.CandidateValue ?? "unavailable"} ({mismatch.Reason})");
            }
        }

        AppendCpuHotspotCaptureMeasurements(text, "baseline", result.BaselineCaptures);
        AppendCpuHotspotCaptureMeasurements(text, "candidate", result.CandidateCaptures);
        AppendCpuHotspotDistribution(text, "baseline selected share", result.BaselineSelectedShare);
        AppendCpuHotspotDistribution(text, "candidate selected share", result.CandidateSelectedShare);
        AppendCpuHotspotDistribution(text, "baseline unresolved share", result.BaselineUnresolvedShare);
        AppendCpuHotspotDistribution(text, "candidate unresolved share", result.CandidateUnresolvedShare);

        if (result.PairedShareDeltas.Count > 0)
        {
            text.AppendLine("  paired selected-share deltas (candidate - baseline):");
            foreach (var delta in result.PairedShareDeltas)
            {
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"    {delta.PairKey}: {FormatCpuHotspotDeltaPercent(delta.CandidateMinusBaseline)}");
            }
            text.AppendLine(CultureInfo.InvariantCulture,
                $"    median: {FormatCpuHotspotDeltaPercent(result.MedianPairedShareDelta)}");
        }

        if (result.RankStability is { } rank)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"  top-{rank.TopK}/rank stability: pairs={rank.CompletePairs}; median-overlap={rank.MedianTopKOverlap:0.###}; same-selected-rank={rank.SameSelectedRankFraction:0.###}; median-absolute-rank-delta={rank.MedianAbsoluteSelectedRankDelta:0.###}");
        }
        if (result.UnavailableFields.Count > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"  unavailable fields  : {string.Join(", ", result.UnavailableFields.Select(field => $"{field.Field}={field.Reason}"))}");
        }

        text.Append("  samples are correlated within each capture; shares are not CPU time or offered-load-normalized performance.");
        return text.ToString();
    }

    private static void AppendCpuHotspotCaptureMeasurements(
        StringBuilder text,
        string label,
        IReadOnlyList<CpuHotspotCaptureMeasurement> captures)
    {
        text.AppendLine(CultureInfo.InvariantCulture, $"  {label} captures:");
        foreach (var capture in captures)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"    {capture.PairKey}/{capture.Id}: samples={capture.TotalSamples}; selected-inclusive={capture.SelectedMethodInclusiveSamples?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"}; selected-share={FormatCpuHotspotPercent(capture.SelectedMethodInclusiveShare)}; selected-rank={capture.SelectedMethodRank?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"}; unresolved-exclusive={capture.UnresolvedExclusiveSamples}; unresolved-share={FormatCpuHotspotPercent(capture.UnresolvedExclusiveShare)}");
        }
    }

    private static void AppendCpuHotspotDistribution(
        StringBuilder text,
        string label,
        CpuHotspotDistribution distribution)
        => text.AppendLine(CultureInfo.InvariantCulture,
            $"  {label,-27}: median={FormatCpuHotspotPercent(distribution.Median)}; IQR={FormatCpuHotspotPercent(distribution.InterquartileRange)}; range=[{FormatCpuHotspotPercent(distribution.Minimum)}, {FormatCpuHotspotPercent(distribution.Maximum)}]");

    private static string FormatCpuHotspotPercent(double? value)
        => value is { } share
            ? share.ToString("0.###%", CultureInfo.InvariantCulture)
            : "unavailable";

    private static string FormatCpuHotspotDeltaPercent(double? value)
        => value is { } share
            ? share.ToString("+0.###%;-0.###%;0%", CultureInfo.InvariantCulture)
            : "unavailable";

    private static string ToWireName(CpuHotspotEvidenceAvailability availability)
        => availability switch
        {
            CpuHotspotEvidenceAvailability.Available => "available",
            CpuHotspotEvidenceAvailability.InsufficientObservations => "insufficient_observations",
            CpuHotspotEvidenceAvailability.Incompatible => "incompatible",
            CpuHotspotEvidenceAvailability.UnsupportedApproximation => "unsupported_approximation",
            _ => throw new ArgumentOutOfRangeException(nameof(availability)),
        };

    internal static string RenderRequestLatencyComparison(RequestLatencyComparisonResult result)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"request-latency comparison: availability={ToWireName(result.Availability)}; no performance verdict");
        if (result.UnavailableReason is not null)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"  unavailable reason : {result.UnavailableReason}");
        }
        text.AppendLine(CultureInfo.InvariantCulture,
            $"  baseline counts     : completed source={result.BaselineCounts.SourceCompleted}, retained={result.BaselineCounts.RetainedCompleted}; errors={result.BaselineCounts.Errors}; censored={result.BaselineCounts.Censored}; missing={result.BaselineCounts.MissingCompletions}; windows complete/failed/aborted={result.BaselineCounts.CompleteWindows}/{result.BaselineCounts.FailedWindows}/{result.BaselineCounts.AbortedWindows}; loss={result.BaselineCounts.LostOrDropped}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"  candidate counts    : completed source={result.CandidateCounts.SourceCompleted}, retained={result.CandidateCounts.RetainedCompleted}; errors={result.CandidateCounts.Errors}; censored={result.CandidateCounts.Censored}; missing={result.CandidateCounts.MissingCompletions}; windows complete/failed/aborted={result.CandidateCounts.CompleteWindows}/{result.CandidateCounts.FailedWindows}/{result.CandidateCounts.AbortedWindows}; loss={result.CandidateCounts.LostOrDropped}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"  paired windows      : matched={result.PairCounts.Matched}; valid-independent-complete={result.PairCounts.ValidIndependentComplete}; unpaired={result.PairCounts.Unpaired}; order-mismatch={result.PairCounts.PairOrderMismatches}; incomplete={result.PairCounts.Incomplete}; failed={result.PairCounts.Failed}; aborted={result.PairCounts.Aborted}; below-minimum={result.PairCounts.BelowMinimumCompletedRequests}");
        if (result.IdentityMismatches.Count > 0)
        {
            text.AppendLine("  compatibility mismatches:");
            foreach (var mismatch in result.IdentityMismatches)
            {
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"    {mismatch.Field}: baseline={mismatch.BaselineValue ?? "unavailable"}, candidate={mismatch.CandidateValue ?? "unavailable"} ({mismatch.Reason})");
            }
        }

        text.AppendLine();
        text.AppendLine("  quantile  baseline ms (source/retained)  candidate ms (source/retained)  absolute ms  relative  paired median ms  95% interval ms");
        foreach (var quantile in result.Quantiles)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"  {quantile.Name,-8} {FormatLatencyNumber(quantile.Baseline.ValueMilliseconds),11} ({quantile.Baseline.SourceCompletedCount}/{quantile.Baseline.RetainedCompletedCount})" +
                $"  {FormatLatencyNumber(quantile.Candidate.ValueMilliseconds),12} ({quantile.Candidate.SourceCompletedCount}/{quantile.Candidate.RetainedCompletedCount})" +
                $"  {FormatLatencyNumber(quantile.AbsoluteDeltaMilliseconds),11}" +
                $"  {FormatLatencyRelative(quantile.RelativeDelta),8}" +
                $"  {FormatLatencyNumber(quantile.PairedWindowMedianDeltaMilliseconds),16}" +
                $"  {FormatLatencyInterval(quantile.PairedWindowBootstrapInterval)}");
            if (quantile.UnavailableFields.Count > 0)
            {
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"    {quantile.Name} unavailable: {string.Join(", ", quantile.UnavailableFields.Select(field => $"{field.Field}={field.Reason}"))}");
            }
        }
        text.AppendLine();
        text.Append("  unavailable values are not zero, unchanged, insignificant, or equivalent load.");
        return text.ToString();
    }

    private static string ToWireName(RequestLatencyEvidenceAvailability availability)
        => availability switch
        {
            RequestLatencyEvidenceAvailability.Available => "available",
            RequestLatencyEvidenceAvailability.InsufficientObservations => "insufficient_observations",
            RequestLatencyEvidenceAvailability.Incompatible => "incompatible",
            RequestLatencyEvidenceAvailability.LossDetected => "loss_detected",
            RequestLatencyEvidenceAvailability.UnsupportedApproximation => "unsupported_approximation",
            _ => throw new ArgumentOutOfRangeException(nameof(availability)),
        };

    private static string FormatLatencyNumber(double? value)
        => value is { } number
            ? number.ToString("0.###", CultureInfo.InvariantCulture)
            : "unavailable";

    private static string FormatLatencyRelative(double? value)
        => value is { } ratio
            ? ratio.ToString("+0.##%;-0.##%;0%", CultureInfo.InvariantCulture)
            : "unavailable";

    private static string FormatLatencyInterval(RequestLatencyInterval? interval)
        => interval is null
            ? "unavailable"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"[{interval.LowerMilliseconds:0.###}, {interval.UpperMilliseconds:0.###}]");

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
