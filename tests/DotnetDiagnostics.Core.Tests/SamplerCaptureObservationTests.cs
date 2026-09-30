using System.Text;
using System.Text.Json;
using DotnetDiagnostics.Core.CaptureRecording;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Memory;
using DotnetDiagnostics.Core.MethodParameters;
using DotnetDiagnostics.Core.NativeAlloc;
using DotnetDiagnostics.Core.OffCpu;
using DotnetDiagnostics.Core.Security;

namespace DotnetDiagnostics.Core.Tests;

public sealed class SamplerCaptureObservationTests
{
    private const string PerfText = """
        worker 4242/4243 [002] 123.456: cycles:
            7f123 Namespace.类型.Method+0x1 (/app/模块.so)
            7f456 Root+0x2 (/app/root.so)

        worker 4242/4243 [002] 123.457: cycles:
            7f123 Namespace.类型.Method+0x1 (/app/模块.so)
            7f456 Root+0x2 (/app/root.so)

        """;

    [Fact]
    public async Task PerfParser_RecordsEachSampleBeforeTopN_WithHonestClocksAndDefaultParity()
    {
        using var baselineReader = new StringReader(PerfText);
        var baseline = await PerfNativeAotCpuSampler.AggregateAsync(baselineReader, 0, 1);
        var sink = new ObservationTestSink();
        using (CaptureRecordingContext.Enter(sink))
        {
            using var reader = new StringReader(PerfText);
            var recorded = await PerfNativeAotCpuSampler.AggregateAsync(reader, 0, 1);
            Assert.Equal(baseline.Total, recorded.Total);
            Assert.Equal(baseline.Hotspots, recorded.Hotspots);
        }
        Assert.Equal(2, sink.Rows.Count);
        var row = sink.Rows[0];
        Assert.Null(row.Timestamp);
        Assert.Equal(4243, row.ThreadId);
        Assert.Equal(123.456, Field(row, "sourceSeconds").Number);
        Assert.Equal(1, Field(row, "weight").Integer);
        Assert.Contains("类型", row.Name);
        using var frames = JsonDocument.Parse(Field(row, "stack").Text!);
        Assert.Equal(2, frames.RootElement.GetArrayLength());
        Assert.Contains("类型", frames.RootElement[0].GetProperty("method").GetString());
        Assert.Equal("/app/root.so", frames.RootElement[1].GetProperty("module").GetString());
        Assert.Null(sink.Losses.Single().Count);
    }

    [Fact]
    public async Task NativePerf_RejectingSinkDoesNotChangeAggregationOrInventBytesAndWaits()
    {
        var sink = new ObservationTestSink { Accept = false };
        using var scope = CaptureRecordingContext.Enter(sink);
        using var reader = new StringReader(PerfText);
        var aggregate = await PerfNativeAotCpuSampler.AggregateAsync(reader, 0, 1,
            observationCategory: "sample.native-lock-contention.perf", samplePeriod: 5000);
        Assert.Equal(2, aggregate.Total);
        Assert.Equal(2, sink.Rows.Count);
        Assert.All(sink.Rows, row =>
        {
            Assert.Equal(5000, Field(row, "samplePeriod").Integer);
            Assert.Equal(CaptureObservationValueKind.Null, Field(row, "waitMicroseconds").Kind);
            Assert.Equal(CaptureObservationValueKind.Null, Field(row, "allocatedBytes").Kind);
            Assert.Equal("sampled-mutex-call-not-proven-contention", Field(row, "evidence").Text);
        });
    }

    [Fact]
    public void Builder_CapturesInvocationSinkAndBoundedIdentityFrames()
    {
        var sink = new ObservationTestSink();
        PerfScriptAggregationBuilder builder;
        using (CaptureRecordingContext.Enter(sink)) builder = new PerfScriptAggregationBuilder();
        var identity = new MethodIdentity("Method", 0, ModuleName: "模块", TypeFullName: "类型");
        builder.AddSample(new PerfSample(0, Enumerable.Range(0, 160)
            .Select(_ => new PerfFrame("模块", "类型.Method", Identity: identity)).ToArray()));
        var row = Assert.Single(sink.Rows);
        Assert.True(Field(row, "stackTruncated").Boolean);
        Assert.Null(row.Timestamp);
        Assert.Null(row.ThreadId);
        using var stack = JsonDocument.Parse(Field(row, "stack").Text!);
        Assert.Equal(SamplerObservationProjection.MaximumFrames, stack.RootElement.GetArrayLength());
        Assert.Equal("类型", stack.RootElement[0].GetProperty("identity").GetProperty("TypeFullName").GetString());
    }

    [Fact]
    public void AllocationPath_PreservesTickBytesNotActualObjectCount_AndMissingStack()
    {
        var sink = new ObservationTestSink();
        EventPipeAllocationSampler.EmitAllocationObservation(sink, 17, 250.5, "类型[]", 102401,
            HeapKind.Large, []);
        var row = Assert.Single(sink.Rows);
        Assert.Null(row.Timestamp);
        Assert.Equal(.2505, Field(row, "sourceSeconds").Number);
        Assert.Equal(102401, Field(row, "weight").Integer);
        Assert.Equal("allocation-tick-estimated-bytes", Field(row, "weightUnit").Text);
        Assert.Equal(CaptureObservationValueKind.Null, Field(row, "actualAllocationCount").Kind);
        Assert.Equal("[]", Field(row, "stack").Text);
    }

    [Fact]
    public void OffCpuBuilder_RecordsIntervalsBeforeGrouping()
    {
        var sink = new ObservationTestSink();
        using var scope = CaptureRecordingContext.Enter(sink);
        var builder = OffCpuAggregator.CreateBuilder();
        builder.AddSpan(new OffCpuSpan(5, "线程", 123, "S", [new OffCpuFrame("libc", "futex")], true, 42.5, "futex"));
        builder.AddSpan(new OffCpuSpan(5, "线程", 456, "S", [new OffCpuFrame("libc", "futex")]));
        Assert.Equal(2, sink.Rows.Count);
        Assert.Equal(123, Field(sink.Rows[0], "weight").Integer);
        Assert.True(Field(sink.Rows[0], "censoredLowerBound").Boolean);
        Assert.Equal("futex", Field(sink.Rows[0], "syscall").Text);
        Assert.Equal(CaptureObservationValueKind.Null, Field(sink.Rows[1], "sourceSeconds").Kind);
    }

    [Fact]
    public async Task OffCpuParser_EmitsMatchedIntervalWithSourceClockBeforeAggregation()
    {
        const string script = """
            target  1000 [001] 1.000000: sched:sched_switch: prev_comm=target prev_pid=1000 prev_prio=120 prev_state=S ==> next_comm=swapper/1 next_pid=0 next_prio=120
                7f123 pthread_cond_wait+0x80 (/usr/lib/libc.so.6)

            swapper 0 [001] 1.250000: sched:sched_switch: prev_comm=swapper/1 prev_pid=0 prev_prio=120 prev_state=R ==> next_comm=target next_pid=1000 next_prio=120

            """;
        var sink = new ObservationTestSink();
        using var scope = CaptureRecordingContext.Enter(sink);
        using var reader = new StringReader(script);
        await PerfSchedOffCpuSampler.AggregateScriptAsync(reader, 1000, DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(1), 1, [1000]);
        var row = Assert.Single(sink.Rows);
        Assert.Null(row.Timestamp);
        Assert.Equal(1000, row.ThreadId);
        Assert.Equal(250000, Field(row, "weight").Integer);
        Assert.Equal(1, Field(row, "sourceSeconds").Number);
        Assert.Equal("perf-monotonic-seconds", Field(row, "sourceClock").Text);
    }

    [Fact]
    public void ParameterObserver_RejectingSinkPreservesExistingArtifact()
    {
        var method = new ResolvedMethodIdentity("m", "id", "T", "M", 0, 1, []);
        var redactor = new SensitiveDataRedactor();
        var rejecting = new ObservationTestSink { Accept = false };
        var recorded = new MethodParameterCaptureCollector.ParameterCaptureObserver(redactor, [method], 1, null, rejecting);
        var baseline = new MethodParameterCaptureCollector.ParameterCaptureObserver(redactor, [method], 1, null, null);
        foreach (var observer in new[] { recorded, baseline })
        {
            observer.BeginInvocation(1, method);
            observer.AddParameter(1, "value", "System.String", "文字");
            observer.FlushPending(1);
        }
        var time = DateTimeOffset.UtcNow;
        var actual = recorded.BuildArtifact(42, time, TimeSpan.FromSeconds(1), "10", [], [method], 1, 1);
        var expected = baseline.BuildArtifact(42, time, TimeSpan.FromSeconds(1), "10", [], [method], 1, 1);
        Assert.Equal(expected.CaptureCount, actual.CaptureCount);
        Assert.Equal(expected.DroppedCount, actual.DroppedCount);
        Assert.Equal(JsonSerializer.Serialize(expected.Events[0].Parameters), JsonSerializer.Serialize(actual.Events[0].Parameters));
        Assert.Single(rejecting.Rows);
    }

    [Fact]
    public void NativeStackPath_PreservesRecordedEventsWithoutByteEstimates()
    {
        var sink = new ObservationTestSink();
        using var scope = CaptureRecordingContext.Enter(sink);
        var result = NativeAllocStackAggregator.Aggregate(
            new IReadOnlyList<(string Module, string Method)>[] { [("ntdll", "VirtualAlloc")], [("ntdll", "VirtualAlloc")] }, 1);
        Assert.Equal(2, result.TotalSampledAllocations);
        Assert.Equal(2, sink.Rows.Count);
        Assert.All(sink.Rows, row => Assert.Equal(CaptureObservationValueKind.Null, Field(row, "allocatedBytes").Kind));
    }

    [Fact]
    public void ParameterObserver_OnlyEmitsAcceptedAllowlistedRedactedAndValueCappedInvocations()
    {
        var sink = new ObservationTestSink();
        var method = new ResolvedMethodIdentity("模块", Guid.NewGuid().ToString(), "类型", "Work", 0, 0x06000001, []);
        var redactor = new SensitiveDataRedactor(new SecurityOptions { RedactionPatterns = ["private-token"] });
        var observer = new MethodParameterCaptureCollector.ParameterCaptureObserver(redactor, [method], 1, null, sink);
        observer.BeginInvocation(1, method);
        observer.AddParameter(1, "secret", "System.String", "private-token");
        observer.AddParameter(1, "large", "System.String", string.Concat(Enumerable.Repeat("文字🙂", 2000)));
        observer.BeginInvocation(2, method); // Pending rows reserve the privacy budget.
        observer.AddParameter(2, "forbidden", "System.String", "not-admitted");
        observer.FlushPending(1);
        observer.BeginInvocation(3, method);
        observer.AddParameter(3, "forbidden", "System.String", "after-limit");
        observer.FlushPending(3);
        var row = Assert.Single(sink.Rows);
        using var invocation = JsonDocument.Parse(Field(row, "invocation").Text!);
        var parameters = invocation.RootElement.GetProperty("Parameters");
        Assert.Equal(2, parameters.GetArrayLength());
        Assert.True(parameters[0].GetProperty("Redacted").GetBoolean());
        Assert.DoesNotContain("private-token", Field(row, "invocation").Text);
        Assert.True(parameters[1].GetProperty("Truncated").GetBoolean());
        Assert.True(Encoding.UTF8.GetByteCount(parameters[1].GetProperty("Value").GetString()!) <= 4096);
        Assert.Equal("observer-receipt-utc", Field(row, "sourceClock").Text);

        var unknown = new MethodParameterCaptureCollector.ParameterCaptureObserver(redactor, [method], 1, null, sink);
        unknown.BeginInvocation(9, method with { MetadataToken = 0 });
        unknown.AddParameter(9, "value", "System.String", "not-allowlisted");
        unknown.FlushPending(9);
        Assert.Single(sink.Rows);
    }

    internal static CaptureObservationField Field(CaptureObservation row, string name)
        => Assert.Single(row.Fields, f => f.Name == name);
}

internal sealed class ObservationTestSink : ICaptureObservationSink
{
    internal List<CaptureObservation> Rows { get; } = [];
    internal List<(string Source, long? Count)> Losses { get; } = [];
    internal bool Accept { get; init; } = true;
    public bool TryAppend(CaptureObservation observation) { Rows.Add(observation); return Accept; }
    public void ReportSourceLoss(string source, long? count) => Losses.Add((source, count));
    public void ArtifactRegistered(DiagnosticHandle handle, object artifact) { }
}
