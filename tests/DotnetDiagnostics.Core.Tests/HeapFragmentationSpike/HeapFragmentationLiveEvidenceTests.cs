using System.Diagnostics;
using System.Globalization;
using FluentAssertions;
using Microsoft.Diagnostics.Runtime;
using Xunit.Abstractions;

namespace DotnetDiagnostics.Core.Tests.HeapFragmentationSpike;

internal enum LiveLohTrialClassification
{
    Matched,
    ControlShapeUnavailable,
    SeparatedShapeUnavailable,
    MarkersNotCoLocated,
    FreeTotalsUnmatched,
}

internal sealed record LiveLohMeasurement(
    string Scenario,
    int ProcessId,
    ulong RegionStart,
    ulong RegionEnd,
    long RegionBytes,
    HeapLayoutEvidenceState EvidenceState,
    long ObservedFreeBytes,
    long ObservedFreeRangeCount,
    long ObservedMaterialFreeRangeCount,
    long LargestObservedFreeBlockBytes,
    IReadOnlyList<FreeRangeProjection> TopFreeRanges,
    int RetentionCapacity,
    int RetentionStorageSlots,
    long TopKSelectionComparisonCount);

internal sealed record LiveLohTrial(
    LiveLohTrialClassification Classification,
    string Reason,
    LiveLohMeasurement Control,
    LiveLohMeasurement Separated);

[Collection("LiveProcess")]
public sealed class HeapFragmentationLiveEvidenceTests(ITestOutputHelper output)
{
    private const int TopK = 4;
    private const int MaterialFreeRangeMinimumBytes =
        HeapFragmentationLiveFixtureProcess.HolePayloadBytes / 2;

    [Fact(Timeout = 120_000)]
    public async Task FreshProcessLohShapes_ExposeObservedLargestHoleAndBoundedTopK()
    {
        var control = await CaptureAsync("tail-free");
        var separated = await CaptureAsync("separated");
        var trial = Classify(control, separated);

        output.WriteLine(Format(control));
        output.WriteLine(Format(separated));
        output.WriteLine($"classification={trial.Classification}; reason={trial.Reason}");

        control.RetentionStorageSlots.Should().Be(TopK);
        separated.RetentionStorageSlots.Should().Be(TopK);
        control.TopFreeRanges.Should().HaveCountLessThanOrEqualTo(TopK);
        separated.TopFreeRanges.Should().HaveCountLessThanOrEqualTo(TopK);

        trial.Classification.Should().Be(
            LiveLohTrialClassification.Matched,
            $"the guarded live fixture must preserve usable evidence; {trial.Reason}");

        control.EvidenceState.Should().Be(HeapLayoutEvidenceState.Indeterminate);
        separated.EvidenceState.Should().Be(HeapLayoutEvidenceState.Indeterminate);
        control.ObservedMaterialFreeRangeCount.Should().Be(1);
        separated.ObservedMaterialFreeRangeCount.Should().BeGreaterThan(1);
        control.LargestObservedFreeBlockBytes.Should()
            .BeGreaterThan(separated.LargestObservedFreeBlockBytes);
        control.TopFreeRanges.Count(IsMaterial).Should().Be(1);
        separated.TopFreeRanges.Count(IsMaterial).Should().BeGreaterThan(1);
        control.TopFreeRanges[0].Length.Should()
            .BeGreaterThan(separated.TopFreeRanges[0].Length);
    }

    private static LiveLohTrial Classify(
        LiveLohMeasurement control,
        LiveLohMeasurement separated)
    {
        if (control.RegionStart == 0 || separated.RegionStart == 0)
        {
            return new LiveLohTrial(
                LiveLohTrialClassification.MarkersNotCoLocated,
                "The guard markers were not observed in one LOH segment, so the intended bounded region is unavailable.",
                control,
                separated);
        }

        if (control.ObservedMaterialFreeRangeCount != 1)
        {
            return new LiveLohTrial(
                LiveLohTrialClassification.ControlShapeUnavailable,
                $"The tail-free control exposed {control.ObservedMaterialFreeRangeCount} material free runs instead of one.",
                control,
                separated);
        }

        if (separated.ObservedMaterialFreeRangeCount < 2)
        {
            return new LiveLohTrial(
                LiveLohTrialClassification.SeparatedShapeUnavailable,
                $"The separated fixture exposed {separated.ObservedMaterialFreeRangeCount} material free runs; runtime layout/trim did not preserve separation.",
                control,
                separated);
        }

        var difference = Math.Abs(control.ObservedFreeBytes - separated.ObservedFreeBytes);
        var tolerance = Math.Max(4_096, Math.Max(control.ObservedFreeBytes, separated.ObservedFreeBytes) / 100);
        if (difference > tolerance)
        {
            return new LiveLohTrial(
                LiveLohTrialClassification.FreeTotalsUnmatched,
                $"Observed free totals differ by {difference:N0} bytes, above the {tolerance:N0}-byte matching tolerance.",
                control,
                separated);
        }

        return new LiveLohTrial(
            LiveLohTrialClassification.Matched,
            $"Observed free totals differ by {difference:N0} bytes within the {tolerance:N0}-byte tolerance.",
            control,
            separated);
    }

    private static async Task<LiveLohMeasurement> CaptureAsync(string scenario)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(typeof(HeapFragmentationLiveEvidenceTests).Assembly.Location);
        start.ArgumentList.Add("--heap-fragmentation-loh-fixture");
        start.ArgumentList.Add(scenario);
        start.Environment["DOTNET_gcServer"] = "0";
        start.Environment["DOTNET_TieredCompilation"] = "0";

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"LOH fixture '{scenario}' did not start.");
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            var ready = await process.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(20));
            var (leftData, rightData) = ParseReadyLine(ready, scenario);
            var measurement = Observe(process.Id, scenario, leftData, rightData, TopK);

            await process.StandardInput.WriteLineAsync("release");
            await process.StandardInput.FlushAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            var errors = await stderr;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"LOH fixture '{scenario}' exited {process.ExitCode}: {errors}");
            }

            return measurement;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    private static (ulong LeftData, ulong RightData) ParseReadyLine(string? line, string scenario)
    {
        var parts = line?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts is not ["loh-ready", var left, var right]
            || !ulong.TryParse(left, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var leftData)
            || !ulong.TryParse(right, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rightData))
        {
            throw new InvalidOperationException(
                $"LOH fixture '{scenario}' emitted an invalid ready line: '{line ?? "<eof>"}'.");
        }

        return (leftData, rightData);
    }

    private static LiveLohMeasurement Observe(
        int processId,
        string scenario,
        ulong leftData,
        ulong rightData,
        int topK)
    {
        using var target = DataTarget.AttachToProcess(processId, suspend: true);
        var clrInfo = target.ClrVersions.FirstOrDefault()
            ?? throw new InvalidOperationException($"LOH fixture '{scenario}' exposes no CLR runtime.");
        using var runtime = clrInfo.CreateRuntime();
        var heap = runtime.Heap;
        var markers = FindMarkers(heap, leftData, rightData);
        if (markers is null)
        {
            return UnavailableMeasurement(scenario, processId, topK);
        }

        var segment = heap.Segments.FirstOrDefault(candidate =>
            markers.Value.LeftStart >= candidate.Start
            && markers.Value.LeftStart < candidate.End);
        if (segment is null
            || segment.Kind != GCSegmentKind.Large
            || markers.Value.RightStart < segment.Start
            || markers.Value.RightStart >= segment.End
            || markers.Value.LeftEnd >= markers.Value.RightStart)
        {
            return UnavailableMeasurement(scenario, processId, topK);
        }

        var retainer = new BoundedFreeRangeRetainer(topK);
        long observedFreeBytes = 0;
        long observedMaterialFreeRangeCount = 0;
        foreach (var obj in segment.EnumerateObjects(carefully: true))
        {
            var objectEnd = checked(obj.Address + obj.Size);
            if (!obj.IsFree
                || obj.Address < markers.Value.LeftEnd
                || objectEnd > markers.Value.RightStart)
            {
                continue;
            }

            var size = checked((long)obj.Size);
            observedFreeBytes = checked(observedFreeBytes + size);
            if (size >= MaterialFreeRangeMinimumBytes)
            {
                observedMaterialFreeRangeCount++;
            }

            retainer.Observe(new FreeRangeProjection(obj.Address, size));
        }

        return new LiveLohMeasurement(
            scenario,
            processId,
            markers.Value.LeftEnd,
            markers.Value.RightStart,
            checked((long)(markers.Value.RightStart - markers.Value.LeftEnd)),
            HeapLayoutEvidenceState.Indeterminate,
            observedFreeBytes,
            retainer.ObservedCount,
            observedMaterialFreeRangeCount,
            retainer.LargestObserved?.Length ?? 0,
            retainer.Snapshot(),
            topK,
            retainer.StorageSlotCount,
            retainer.SelectionComparisonCount);
    }

    private static (ulong LeftStart, ulong LeftEnd, ulong RightStart)? FindMarkers(
        ClrHeap heap,
        ulong leftData,
        ulong rightData)
    {
        (ulong Start, ulong End)? left = null;
        (ulong Start, ulong End)? right = null;
        foreach (var obj in heap.EnumerateObjects(carefully: true))
        {
            if (!string.Equals(obj.Type?.Name, "System.Byte[]", StringComparison.Ordinal))
            {
                continue;
            }

            var end = checked(obj.Address + obj.Size);
            if (obj.Address <= leftData && leftData < end)
            {
                left = (obj.Address, end);
            }

            if (obj.Address <= rightData && rightData < end)
            {
                right = (obj.Address, end);
            }

            if (left.HasValue && right.HasValue)
            {
                return (left.Value.Start, left.Value.End, right.Value.Start);
            }
        }

        return null;
    }

    private static LiveLohMeasurement UnavailableMeasurement(string scenario, int processId, int topK)
    {
        return new LiveLohMeasurement(
            scenario,
            processId,
            RegionStart: 0,
            RegionEnd: 0,
            RegionBytes: 0,
            HeapLayoutEvidenceState.Indeterminate,
            ObservedFreeBytes: 0,
            ObservedFreeRangeCount: 0,
            ObservedMaterialFreeRangeCount: 0,
            LargestObservedFreeBlockBytes: 0,
            TopFreeRanges: Array.Empty<FreeRangeProjection>(),
            topK,
            RetentionStorageSlots: topK,
            TopKSelectionComparisonCount: 0);
    }

    private static string Format(LiveLohMeasurement measurement)
    {
        var top = string.Join(
            ", ",
            measurement.TopFreeRanges.Select(range => $"0x{range.Start:x}+{range.Length}"));
        return
            $"scenario={measurement.Scenario}; pid={measurement.ProcessId}; " +
            $"region=0x{measurement.RegionStart:x}-0x{measurement.RegionEnd:x} ({measurement.RegionBytes} bytes); " +
            $"evidence={measurement.EvidenceState}; freeBytes={measurement.ObservedFreeBytes}; " +
            $"freeRanges={measurement.ObservedFreeRangeCount}; " +
            $"materialFreeRanges={measurement.ObservedMaterialFreeRangeCount}; " +
            $"largestFree={measurement.LargestObservedFreeBlockBytes}; " +
            $"topK=[{top}]; topKSlots={measurement.RetentionStorageSlots}; " +
            $"topKComparisons={measurement.TopKSelectionComparisonCount}";
    }

    private static bool IsMaterial(FreeRangeProjection range) =>
        range.Length >= MaterialFreeRangeMinimumBytes;
}
