using System.Net.Http.Json;
using System.Runtime.InteropServices;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.TestSupport;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public static class GcPressureLiveHarness
{
    public static async Task<GcPressureCapture> CaptureAsync(
        GcPressureScenarioKind scenario,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        await using var sample = await LiveSampleProcess.StartPublishedAsync(
            "BadCodeSample",
            new LiveSampleOptions
            {
                HarvestListeningUrl = true,
                WaitForHttpReady = true,
                ReadinessPath = "/",
                DiagnosticTimeout = TimeSpan.FromSeconds(30),
                HttpTimeout = TimeSpan.FromSeconds(30),
                Environment = new Dictionary<string, string>
                {
                    ["DOTNET_gcServer"] = "0",
                    ["DOTNET_TieredCompilation"] = "1",
                    ["DOTNET_TC_QuickJit"] = "1",
                    ["DOTNET_TieredPGO"] = "1",
                },
            },
            timeout.Token).ConfigureAwait(false);
        using var http = new HttpClient
        {
            BaseAddress = new Uri(sample.BaseUrl),
            Timeout = TimeSpan.FromSeconds(30),
        };

        var windows = new List<GcPressureWindow>(GcPressureLiveBounds.WindowCount);
        var notes = new List<string>();
        for (var index = 0; index < GcPressureLiveBounds.WindowCount; index++)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var phase = Phase(scenario, index);
            var duration = TimeSpan.FromSeconds(GcPressureLiveBounds.WindowSeconds);
            var gcCap = scenario == GcPressureScenarioKind.CaptureLoss
                ? GcPressureLiveBounds.LossGcDetailCap
                : GcPressureLiveBounds.NormalGcDetailCap;
            var countersTask = new EventPipeCounterCollector().CollectAsync(
                sample.ProcessId,
                duration,
                intervalSeconds: 1,
                cancellationToken: timeout.Token);
            var gcTask = new EventPipeGcCollector().CollectAsync(
                sample.ProcessId,
                duration,
                maxEvents: gcCap,
                cancellationToken: timeout.Token);
            var workloadTask = DriveWindowAsync(
                scenario,
                index,
                http,
                timeout.Token);

            await Task.WhenAll(countersTask, gcTask, workloadTask).ConfigureAwait(false);
            var counters = countersTask.Result;
            var gc = gcTask.Result;
            var lost = gc.DroppedEvents
                + gc.DroppedHeapStats
                + (gc.Suspension?.DroppedIntervals ?? 0);
            var representation = lost > 0
                ? GcPressureRepresentation.Truncated
                : GcPressureRepresentation.Exact;
            var counterLoh = CounterMaximum(counters, "loh-size");
            var heapLoh = gc.HeapStats?.Count > 0
                ? gc.HeapStats.Max(sample => (double)sample.LohSizeBytes)
                : 0;
            var gen2 = gc.Generations.FirstOrDefault(generation => generation.Generation == 2)?.Count ?? 0;
            var observedSeconds = Math.Max(0.001, gc.Duration.TotalSeconds);
            var pauseDurations = gc.Suspension?.Intervals
                .Select(interval => interval.Duration.TotalMilliseconds)
                .Order()
                .ToArray() ?? [];
            var pauseP95 = pauseDurations.Length == 0 ? 0 : QuantileR7(pauseDurations, 0.95);
            var timeInGcPercent = gc.Suspension?.TotalSuspensionTime is { } suspension
                ? Math.Clamp(suspension.TotalSeconds * 100 / observedSeconds, 0, 100)
                : 0;
            windows.Add(new(
                index,
                phase,
                Math.Max(counterLoh, heapLoh),
                gen2 / observedSeconds,
                pauseP95,
                timeInGcPercent,
                SourceEvents: gc.TotalCollections
                    + (gc.HeapStats?.Count ?? 0)
                    + pauseDurations.Length
                    + counters.Counters.Count,
                LostOrDropped: lost,
                representation));
            notes.AddRange(counters.Notes.Select(note => $"window-{index}:counter:{note}"));
            notes.AddRange(ScenarioLiveRunner.DescribeGcQuality(gc).Select(note => $"window-{index}:{note}"));
        }

        return new(
            GcPressureCharacterizer.CurrentCaptureSchemaVersion,
            $"gc-pressure-{scenario.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}",
            scenario,
            Environment.Version.ToString(),
            OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "unsupported",
            RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            GcPressureLiveBounds.WindowSeconds,
            windows,
            notes.Take(20).ToArray());
    }

    private static GcPressureWindowPhase Phase(GcPressureScenarioKind scenario, int index)
    {
        if (index == 0)
        {
            return GcPressureWindowPhase.Baseline;
        }

        return scenario switch
        {
            GcPressureScenarioKind.StableHealthy => GcPressureWindowPhase.Cooldown,
            GcPressureScenarioKind.TransientAllocationBurst => index == 1
                ? GcPressureWindowPhase.Active
                : GcPressureWindowPhase.Cooldown,
            GcPressureScenarioKind.SustainedAllocationPressure => index <= 3
                ? GcPressureWindowPhase.Active
                : GcPressureWindowPhase.Cooldown,
            GcPressureScenarioKind.RetainedGrowth => index <= 3
                ? GcPressureWindowPhase.Active
                : GcPressureWindowPhase.Cooldown,
            GcPressureScenarioKind.CaptureLoss => index <= 3
                ? GcPressureWindowPhase.Active
                : GcPressureWindowPhase.Cooldown,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
    }

    private static async Task DriveWindowAsync(
        GcPressureScenarioKind scenario,
        int index,
        HttpClient http,
        CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(800), cancellationToken).ConfigureAwait(false);
        switch (scenario)
        {
            case GcPressureScenarioKind.StableHealthy:
                return;
            case GcPressureScenarioKind.TransientAllocationBurst when index == 1:
                await BurstAsync(http, requests: 1, count: 300, cancellationToken).ConfigureAwait(false);
                return;
            case GcPressureScenarioKind.SustainedAllocationPressure when index is >= 1 and <= 3:
                await BurstAsync(http, requests: 4, count: 500, cancellationToken).ConfigureAwait(false);
                return;
            case GcPressureScenarioKind.RetainedGrowth when index is >= 1 and <= 3:
                using (var response = await http.GetAsync("/leak?mb=16", cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    _ = await response.Content.ReadFromJsonAsync<RetainedResponse>(
                        cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidDataException("Retained-growth response was empty.");
                }
                await BurstAsync(http, requests: 1, count: 100, cancellationToken).ConfigureAwait(false);
                return;
            case GcPressureScenarioKind.CaptureLoss when index is >= 1 and <= 3:
                await BurstAsync(http, requests: 5, count: 500, cancellationToken).ConfigureAwait(false);
                return;
            default:
                return;
        }
    }

    private static async Task BurstAsync(
        HttpClient http,
        int requests,
        int count,
        CancellationToken cancellationToken)
    {
        await Task.WhenAll(Enumerable.Range(0, requests).Select(async _ =>
        {
            using var response = await http.GetAsync(
                $"/loh-alloc?count={count}",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        })).ConfigureAwait(false);
    }

    private static double CounterMaximum(CounterSnapshot snapshot, string name)
    {
        var source = snapshot.MaxCounters ?? snapshot.Counters;
        return source.FirstOrDefault(counter =>
            string.Equals(counter.Provider, "System.Runtime", StringComparison.Ordinal)
            && string.Equals(counter.Name, name, StringComparison.Ordinal))?.Value ?? 0;
    }

    private static double QuantileR7(double[] sortedValues, double probability)
    {
        if (sortedValues.Length == 1)
        {
            return sortedValues[0];
        }

        var position = (sortedValues.Length - 1) * probability;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        var fraction = position - lower;
        return sortedValues[lower] + fraction * (sortedValues[upper] - sortedValues[lower]);
    }

    private sealed record RetainedResponse(int AddedMb, int TotalBuffers);
}
