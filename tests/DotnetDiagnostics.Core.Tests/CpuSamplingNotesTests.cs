using DotnetDiagnostics.Core.CpuSampling;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class CpuSamplingNotesTests
{
    [Fact]
    public void EventPipe_ReportsPerThreadRateAndShortCallCaveat()
    {
        var notes = CpuSamplingNotes.ForEventPipe(5000, TimeSpan.FromSeconds(4), sampledThreads: 2, threadsSaturated: false);

        notes.Should().HaveCount(2);
        notes[0].Should().Contain("625 samples/s per sampled thread")
            .And.Contain("5,000 samples").And.Contain("2 thread(s)");
        notes[1].Should().Contain("under about 100 µs").And.Contain("10x or more")
            .And.Contain("--cpu-backend os").And.Contain("Stopwatch");
        string.Join(' ', notes).Should().NotContainAny("safepoint", "suspension", "Windows", "jitter");
    }

    [Fact]
    public void EventPipe_SaturatedThreadCount_MarksRateAsBound()
    {
        var notes = CpuSamplingNotes.ForEventPipe(4096, TimeSpan.FromSeconds(1), CpuSamplingNotes.MaxTrackedThreads, threadsSaturated: true);

        notes[0].Should().Contain("at most 1 samples/s").And.Contain("at least 4,096 thread(s)");
    }

    [Fact]
    public void EventPipe_WithoutSamples_OmitsRateButKeepsCaveat()
    {
        var notes = CpuSamplingNotes.ForEventPipe(0, TimeSpan.FromSeconds(3), 0, false);

        notes.Should().ContainSingle().Which.Should().Contain("--cpu-backend os");
    }

    [Fact]
    public void OsBackend_StatesFixedFrequencyAndHasNoShortCallCaveat()
    {
        var notes = CpuSamplingNotes.ForOsBackend(990, TimeSpan.FromSeconds(5), sampledThreads: 2, threadsSaturated: false, configuredHz: 99);

        notes[0].Should().Contain("fixed 99 Hz");
        notes[1].Should().Contain("99 samples/s per sampled thread");
        string.Join(' ', notes).Should().NotContainAny("100 µs", "10x", "Stopwatch", "under-counts");
    }

    [Fact]
    public void ThreadTracker_CountsDistinctIdsAndBoundsMemory()
    {
        var tracker = new CpuSamplingNotes.ThreadTracker();
        for (var i = 0; i < CpuSamplingNotes.MaxTrackedThreads; i++)
        {
            tracker.Add(i);
            tracker.Add(i);
        }

        tracker.Count.Should().Be(CpuSamplingNotes.MaxTrackedThreads);
        tracker.Saturated.Should().BeFalse();
        tracker.Add(0);
        tracker.Saturated.Should().BeFalse("an already tracked id does not saturate the tracker");
        tracker.Add(int.MaxValue);
        tracker.Saturated.Should().BeTrue();
        tracker.Count.Should().Be(CpuSamplingNotes.MaxTrackedThreads);
    }

    [Fact]
    public async Task PerfAggregation_CountsDistinctThreadsWithoutRecordingContext()
    {
        const string text = """
            worker 4242/4243 [002] 123.456: cycles:
                7f123 A+0x1 (/app/a.so)

            worker 4242/4243 [002] 123.457: cycles:
                7f123 A+0x1 (/app/a.so)

            worker 4242/4244 [001] 123.458: cycles:
                7f123 A+0x1 (/app/a.so)

            """;
        using var reader = new StringReader(text);

        var result = await PerfNativeAotCpuSampler.AggregateAsync(reader, 0, 1);

        result.Total.Should().Be(3);
        result.SampledThreads.Should().Be(2);
        result.SampledThreadsSaturated.Should().BeFalse();
    }

    [Fact]
    public async Task PerfAggregation_CountsThreadsFromDefaultShapedHeaders()
    {
        const string text = """
            worker-thread  90001 [001] 123.456: cycles:
                7f123 A+0x1 (/app/a.so)

            worker-thread  90001 [001] 123.457: cycles:
                7f123 A+0x1 (/app/a.so)

            worker-thread  90002 [002] 123.458: cycles:
                7f123 A+0x1 (/app/a.so)

            """;
        using var reader = new StringReader(text);

        var result = await PerfNativeAotCpuSampler.AggregateAsync(reader, 0, 1);

        result.Total.Should().Be(3);
        result.SampledThreads.Should().Be(2);
    }
}
