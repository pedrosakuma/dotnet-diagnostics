using System.Diagnostics;
using DotnetDiagnostics.Core.Dump;
using FluentAssertions;
using Xunit.Abstractions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class ObservedFreeBlockAggregationTests(ITestOutputHelper output)
{
    private const int HighBlockCount = 100_000;

    [Fact]
    public void TopK_IsDeterministicBoundedAndAppliedAtInsertion()
    {
        var aggregation = new ObservedFreeBlockAggregation(capacity: 3);

        aggregation.Observe(0x5000, 40_000);
        aggregation.Observe(0x3000, 80_000);
        aggregation.Observe(0x4000, 80_000);
        aggregation.Observe(0x1000, 80_000);
        aggregation.Observe(0x2000, 20_000);
        aggregation.Observe(0x6000, 100_000);

        aggregation.Snapshot().Should().Equal(
            new ObservedFreeBlockRange(0x6000, 0x6000 + 100_000, 100_000),
            new ObservedFreeBlockRange(0x1000, 0x1000 + 80_000, 80_000),
            new ObservedFreeBlockRange(0x3000, 0x3000 + 80_000, 80_000));
        aggregation.ObservedCount.Should().Be(6);
        aggregation.RetainedCount.Should().Be(3);
        aggregation.OmittedCount.Should().Be(3);
        aggregation.LargestObservedBytes.Should().Be(100_000);
        aggregation.Capacity.Should().Be(3);
    }

    [Fact]
    public void MaterialThreshold_ExcludesPaddingWithoutChangingClrFreeObjectCount()
    {
        var aggregation = new ObservedFreeBlockAggregation();
        aggregation.Observe(0x1000, ObservedFreeBlockAggregation.MaterialBlockMinimumBytes - 1);
        aggregation.Observe(0x2000, ObservedFreeBlockAggregation.MaterialBlockMinimumBytes);

        var segment = aggregation.ApplyTo(Segment(freeObjectCount: 17), unresolvedObjectCount: 0);

        segment.FreeObjectCount.Should().Be(17,
            "the CLR compatibility aggregate is not reinterpreted as a material hole count");
        segment.ObservedMaterialFreeBlockCount.Should().Be(1);
        segment.LargestObservedMaterialFreeBlockBytes.Should()
            .Be(ObservedFreeBlockAggregation.MaterialBlockMinimumBytes);
        segment.TopObservedMaterialFreeBlocks.Should().ContainSingle();
    }

    [Theory]
    [InlineData(0, true, ObservedFreeBlockEvidenceState.Complete)]
    [InlineData(0, false, ObservedFreeBlockEvidenceState.Indeterminate)]
    [InlineData(2, false, ObservedFreeBlockEvidenceState.Partial)]
    public void EvidenceState_RequiresExplicitCompletion(
        long unresolvedObjects,
        bool completionEstablished,
        ObservedFreeBlockEvidenceState expected)
    {
        var aggregation = new ObservedFreeBlockAggregation();
        aggregation.Observe(0x1000, 16_384);

        var segment = aggregation.ApplyTo(
            Segment(freeObjectCount: 1),
            unresolvedObjects,
            completionEstablished);

        segment.FreeBlockEvidenceState.Should().Be(expected);
        segment.FreeBlockEvidenceLimitations.Should().NotBeNullOrEmpty();
        segment.FreeBlockEvidenceLimitations.Should().Contain(
            limitation => limitation.Contains("do not establish allocation success", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HighBlockCount_HasBoundedDiagnosticHostAllocationRssAndElapsed()
    {
        if (Environment.GetCommandLineArgs() is [_, "--observed-free-block-geometry"])
        {
            RunGeometryWorker();
            return;
        }

        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(typeof(ObservedFreeBlockAggregationTests).Assembly.Location);
        start.ArgumentList.Add("--observed-free-block-geometry");

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Observed free-block geometry worker did not start.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        await process.WaitForExitAsync(deadline.Token);
        var report = await stdout;
        var errors = await stderr;
        output.WriteLine(report);
        output.WriteLine(errors);

        process.ExitCode.Should().Be(0, $"geometry worker failed: {errors}\n{report}");
        report.Should().Contain($"observed={HighBlockCount}")
            .And.Contain($"retained={ObservedFreeBlockAggregation.TopRangeLimit}")
            .And.Contain($"omitted={HighBlockCount - ObservedFreeBlockAggregation.TopRangeLimit}");
    }

    internal static int RunGeometryWorker()
    {
        try
        {
            for (var index = 0; index < 2_000; index++)
            {
                _ = checked(index + ObservedFreeBlockAggregation.MaterialBlockMinimumBytes);
            }

            using var process = Process.GetCurrentProcess();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            process.Refresh();

            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var rssBefore = process.WorkingSet64;
            var watch = Stopwatch.StartNew();
            var aggregation = new ObservedFreeBlockAggregation();
            for (var index = 0; index < HighBlockCount; index++)
            {
                aggregation.Observe(
                    checked(0x1000UL + (ulong)index * 0x20_000UL),
                    checked(ObservedFreeBlockAggregation.MaterialBlockMinimumBytes + index));
            }

            var retained = aggregation.Snapshot();
            watch.Stop();
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            process.Refresh();
            var rssDelta = Math.Max(0, process.WorkingSet64 - rssBefore);

            if (aggregation.ObservedCount != HighBlockCount
                || aggregation.RetainedCount != ObservedFreeBlockAggregation.TopRangeLimit
                || aggregation.OmittedCount != HighBlockCount - ObservedFreeBlockAggregation.TopRangeLimit)
            {
                throw new InvalidOperationException("Bounded top-K accounting mismatch.");
            }

            if (retained.Count != ObservedFreeBlockAggregation.TopRangeLimit)
            {
                throw new InvalidOperationException("Retained range storage exceeded its configured cap.");
            }

            if (allocated > 256 * 1024)
            {
                throw new InvalidOperationException(
                    $"Projection allocated {allocated:N0} bytes; expected <= 262,144 bytes.");
            }

            if (rssDelta > 16 * 1024 * 1024)
            {
                throw new InvalidOperationException(
                    $"Projection increased diagnostic-host RSS by {rssDelta:N0} bytes; expected <= 16 MiB.");
            }

            if (watch.Elapsed > TimeSpan.FromSeconds(2))
            {
                throw new InvalidOperationException(
                    $"Projection took {watch.Elapsed.TotalMilliseconds:N1} ms; expected <= 2,000 ms.");
            }

            Console.WriteLine(
                $"observed={aggregation.ObservedCount}; retained={aggregation.RetainedCount}; " +
                $"omitted={aggregation.OmittedCount}; allocatedBytes={allocated}; " +
                $"rssDeltaBytes={rssDelta}; elapsedMs={watch.Elapsed.TotalMilliseconds:F3}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static SegmentStat Segment(long freeObjectCount) =>
        new(0, "Generation2", "Gen2", 0x1000, 0x2000, 4096, 4096, 0, 2048, 2048, 10, freeObjectCount)
        {
            FreePercent = 50,
        };
}
