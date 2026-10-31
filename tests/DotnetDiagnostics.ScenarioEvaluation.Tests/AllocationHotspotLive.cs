using System.Runtime.InteropServices;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.TestSupport;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal static class AllocationHotspotEnvironmentVariables
{
    public const string Repetitions = "DOTNET_DIAGNOSTICS_ALLOCATION_HOTSPOT_REPETITIONS";
    public const string FixturePath = "DOTNET_DIAGNOSTICS_ALLOCATION_HOTSPOT_FIXTURE_PATH";
}

public static class AllocationHotspotLive
{
    private static readonly TimeSpan CaptureDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ArmDelay = TimeSpan.FromMilliseconds(900);
    private const int WorkloadMilliseconds = 1500;

    public static async Task<IReadOnlyList<AllocationHotspotCapture>> CaptureAllAsync(
        int repetitions,
        CancellationToken cancellationToken)
    {
        if (repetitions is < 1 or > AllocationHotspotBounds.MaximumCapturesPerScenario)
        {
            throw new ArgumentOutOfRangeException(
                nameof(repetitions),
                $"Repetitions must be in [1, {AllocationHotspotBounds.MaximumCapturesPerScenario}].");
        }

        var captures = new List<AllocationHotspotCapture>(repetitions * 3 + 5);
        for (var repetition = 1; repetition <= repetitions; repetition++)
        {
            captures.Add(await CaptureAsync(
                AllocationHotspotScenario.StableHotspot,
                "primary",
                repetition,
                cancellationToken).ConfigureAwait(false));
            captures.Add(await CaptureAsync(
                AllocationHotspotScenario.RepeatedAa,
                "primary",
                repetition,
                cancellationToken).ConfigureAwait(false));
            captures.Add(await CaptureAsync(
                AllocationHotspotScenario.ChangedMix,
                "changed",
                repetition,
                cancellationToken).ConfigureAwait(false));
        }

        var source = captures.First(capture =>
            capture.Scenario == AllocationHotspotScenario.StableHotspot);
        captures.Add(LowSample(source));
        captures.Add(RawEmpty(source));
        captures.Add(NoStack(source));
        captures.Add(ZeroWeight(source));
        captures.Add(InvalidHandle(source));
        return captures;
    }

    private static async Task<AllocationHotspotCapture> CaptureAsync(
        AllocationHotspotScenario scenario,
        string workloadVariant,
        int repetition,
        CancellationToken cancellationToken)
    {
        await using var sample = await LiveSampleProcess.StartPublishedAsync(
            "BadCodeSample",
            new LiveSampleOptions
            {
                HarvestListeningUrl = true,
                WaitForHttpReady = true,
                ReadinessPath = "/",
                DiagnosticTimeout = TimeSpan.FromSeconds(20),
                HttpTimeout = TimeSpan.FromSeconds(20),
                Environment = new Dictionary<string, string>
                {
                    ["DOTNET_gcServer"] = "0",
                    ["DOTNET_TieredCompilation"] = "1",
                    ["DOTNET_TC_QuickJit"] = "1",
                    ["DOTNET_TieredPGO"] = "1",
                },
            },
            cancellationToken).ConfigureAwait(false);
        using var http = new HttpClient
        {
            BaseAddress = new Uri(sample.BaseUrl),
            Timeout = TimeSpan.FromSeconds(20),
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        var samplerTask = new EventPipeAllocationSampler().SampleAsync(
            sample.ProcessId,
            CaptureDuration,
            topN: AllocationHotspotBounds.MaximumRankedRows,
            timeout.Token);
        await Task.Delay(ArmDelay, timeout.Token).ConfigureAwait(false);
        using var response = await http.GetAsync(
            $"/allocation-shape?shape={workloadVariant}&milliseconds={WorkloadMilliseconds}",
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var sampled = await samplerTask.ConfigureAwait(false);
        return Project(
            $"{ScenarioName(scenario)}-{repetition:D2}",
            scenario,
            workloadVariant,
            sampled);
    }

    private static AllocationHotspotCapture Project(
        string captureId,
        AllocationHotspotScenario scenario,
        string workloadVariant,
        AllocationSampleResult sampled)
    {
        var summary = sampled.Summary;
        var attributedTicks = sampled.Artifact.Root.InclusiveSamples;
        var attributedBytes = sampled.EstimatedBytesArtifact?.Root.InclusiveSamples ?? 0;
        var types = summary.TopByBytes
            .Where(type => IsWorkloadType(type.TypeName))
            .OrderByDescending(type => type.TotalBytes)
            .ThenBy(type => type.TypeName, StringComparer.Ordinal)
            .Take(AllocationHotspotBounds.MaximumRankedRows)
            .Select((type, index) => new AllocationRankedEvidence(
                type.TypeName,
                index + 1,
                type.TotalBytes,
                type.EventCount))
            .ToArray();
        var stacks = summary.TopBySite
            .Where(site => site.Frame.Method.Contains(
                "AllocationShapeWorkload",
                StringComparison.Ordinal))
            .OrderByDescending(site => site.TotalBytes)
            .ThenBy(site => site.Frame.Method, StringComparer.Ordinal)
            .Take(AllocationHotspotBounds.MaximumRankedRows)
            .Select((site, index) => new AllocationRankedEvidence(
                $"{site.Frame.Module}!{site.Frame.Method}",
                index + 1,
                site.TotalBytes,
                site.EventCount))
            .ToArray();
        var limitations = new List<string>
        {
            "Live ranks independently filter the workload's array types and allocation helper methods; no type-to-stack join is inferred.",
            "Estimated bytes and allocation ticks are sampled GCAllocationTick evidence, not exact allocation totals.",
        };
        if (attributedTicks < summary.TotalEvents || attributedBytes < summary.TotalBytes)
        {
            limitations.Add(
                "Some sampled allocation evidence was not attributed to a usable stack; raw totals remain separate from stack-attributed totals.");
        }

        return new(
            captureId,
            scenario,
            workloadVariant,
            AllocationEvidenceOrigin.LiveCapture,
            Environment.Version.ToString(),
            OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "unsupported",
            RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            summary.Duration.TotalSeconds,
            AllocationHandleState.Available,
            summary.TotalEvents,
            summary.TotalBytes,
            attributedTicks,
            attributedBytes,
            types,
            stacks,
            limitations);
    }

    private static AllocationHotspotCapture LowSample(AllocationHotspotCapture source)
    {
        var estimatedBytesPerTick = Math.Max(
            1,
            source.RawEstimatedBytes / Math.Max(1, source.RawAllocationTicks));
        var rawEstimatedBytes = estimatedBytesPerTick > long.MaxValue / 2
            ? long.MaxValue
            : estimatedBytesPerTick * 2;
        var type = source.Types.Take(1).Select(item => item with
        {
            Rank = 1,
            EstimatedBytes = rawEstimatedBytes,
            AllocationTicks = 2,
        }).ToArray();
        var stack = source.Stacks.Take(1).Select(item => item with
        {
            Rank = 1,
            EstimatedBytes = estimatedBytesPerTick,
            AllocationTicks = 1,
        }).ToArray();
        return source with
        {
            CaptureId = "low-sample-controlled-01",
            Scenario = AllocationHotspotScenario.LowSample,
            Origin = AllocationEvidenceOrigin.ControlledProjection,
            RawAllocationTicks = 2,
            RawEstimatedBytes = rawEstimatedBytes,
            AttributedAllocationTicks = stack.Length == 0 ? 0 : 1,
            AttributedEstimatedBytes = stack.Length == 0 ? 0 : estimatedBytesPerTick,
            Types = type,
            Stacks = stack,
            Limitations =
            [
                "Controlled projection of two live allocation ticks, one with usable stack attribution, validates both the low-sample and partial-attribution paths without claiming event-level independence.",
            ],
        };
    }

    private static AllocationHotspotCapture RawEmpty(AllocationHotspotCapture source)
        => source with
        {
            CaptureId = "raw-empty-controlled-01",
            Scenario = AllocationHotspotScenario.RawEmpty,
            Origin = AllocationEvidenceOrigin.ControlledProjection,
            RawAllocationTicks = 0,
            RawEstimatedBytes = 0,
            AttributedAllocationTicks = 0,
            AttributedEstimatedBytes = 0,
            Types = [],
            Stacks = [],
            Limitations =
            [
                "Controlled projection validates that a completed bounded capture with no ticks remains raw-empty rather than zero-allocation evidence.",
            ],
        };

    private static AllocationHotspotCapture NoStack(AllocationHotspotCapture source)
        => source with
        {
            CaptureId = "no-stack-controlled-01",
            Scenario = AllocationHotspotScenario.RawNonzeroNoStack,
            Origin = AllocationEvidenceOrigin.ControlledProjection,
            AttributedAllocationTicks = 0,
            AttributedEstimatedBytes = 0,
            Stacks = [],
            Limitations =
            [
                "Controlled projection preserves live raw totals and type ranks while removing every usable stack.",
            ],
        };

    private static AllocationHotspotCapture ZeroWeight(AllocationHotspotCapture source)
        => source with
        {
            CaptureId = "zero-weight-controlled-01",
            Scenario = AllocationHotspotScenario.UsableStackZeroWeight,
            Origin = AllocationEvidenceOrigin.ControlledProjection,
            AttributedEstimatedBytes = 0,
            Stacks = source.Stacks.Select(item => item with { EstimatedBytes = 0 }).ToArray(),
            Limitations =
            [
                "Controlled projection preserves usable stack ticks while removing sampled byte weights; tick and byte rankings remain distinct.",
            ],
        };

    private static AllocationHotspotCapture InvalidHandle(AllocationHotspotCapture source)
        => source with
        {
            CaptureId = "invalid-handle-controlled-01",
            Scenario = AllocationHotspotScenario.InvalidHandle,
            Origin = AllocationEvidenceOrigin.ControlledProjection,
            HandleState = AllocationHandleState.InvalidOrExpired,
            AttributedAllocationTicks = 0,
            AttributedEstimatedBytes = 0,
            Stacks = [],
            Limitations =
            [
                "Controlled projection preserves live raw totals and type ranks while modeling an invalid or expired retained handle; stack coverage is unavailable, not zero.",
            ],
        };

    private static bool IsWorkloadType(string typeName)
        => typeName is "System.Byte[]" or "System.Char[]" or "System.Int32[]";

    private static string ScenarioName(AllocationHotspotScenario scenario)
        => scenario switch
        {
            AllocationHotspotScenario.StableHotspot => "stable-hotspot",
            AllocationHotspotScenario.ChangedMix => "changed-mix",
            AllocationHotspotScenario.RepeatedAa => "repeated-aa",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
}
