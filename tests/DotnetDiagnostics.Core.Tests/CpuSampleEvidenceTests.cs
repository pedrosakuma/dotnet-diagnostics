using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetDiagnostics.Core.Comparison;
using DotnetDiagnostics.Core.CpuSampling;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class CpuSampleEvidenceTests
{
    [Fact]
    public void WeightedAllocationTree_PreservesByteWeightsSeparatelyFromTicks()
    {
        var bytes = new CallTreeBuilder(3);
        var ticks = new CallTreeBuilder(3);
        List<(string Key, string Module, string Display)> first = [("App!A", "App", "A")];
        List<(string Key, string Module, string Display)> second = [("App!B", "App", "B")];
        bytes.AddStack(first, "App!A", weight: 100);
        bytes.AddStack(second, "App!B", weight: 900);
        ticks.AddStack(first, "App!A");
        ticks.AddStack(second, "App!B");
        var tree = bytes.Build();
        tree.InclusiveSamples.Should().Be(1000);
        tree.Children[0].Frame.Method.Should().Be("B");
        tree.Children[0].ExclusiveSamples.Should().Be(900);
        tree.Children[0].SelfSamples.Should().BeNull();
        ticks.Build().InclusiveSamples.Should().Be(2);
        var overflow = () => bytes.AddStack([("App!C", "App", "C")], "App!C", weight: 1);
        overflow.Should().Throw<SampleLimitExceededException>().WithMessage("*MaxNodes=3*No partial profile*");
        var negative = () => bytes.AddStack(first, "App!A", weight: -1);
        negative.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task AllocationTraceLimit_RejectsBeforeWritingPastCap()
    {
        using var input = new ZeroStream(EventPipeAllocationSampler.MaxTraceBytes + 1);
        using var output = new CountingStream();
        var capture = () => EventPipeAllocationSampler.CopyBoundedTraceAsync(input, output, CancellationToken.None);
        await capture.Should().ThrowAsync<SampleLimitExceededException>().WithMessage("*MaxTraceBytes=*No partial profile*");
        output.Written.Should().Be(EventPipeAllocationSampler.MaxTraceBytes);
        using var exact = new ZeroStream(EventPipeAllocationSampler.MaxTraceBytes);
        await EventPipeAllocationSampler.CopyBoundedTraceAsync(exact, Stream.Null, CancellationToken.None);
    }

    private sealed class ZeroStream(long remaining) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = (int)Math.Min(remaining, buffer.Length);
            remaining -= read;
            return ValueTask.FromResult(read);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CountingStream : Stream
    {
        public long Written { get; private set; }
        public override bool CanRead => false;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => Written;
        public override long Position { get => Written; set => throw new NotSupportedException(); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Written += buffer.Length;
            return ValueTask.CompletedTask;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("MyApp.PrivateWaitWrapper()", 0, 0, 1)]
    [InlineData("[unknown]", 0, 0, 1)]
    [InlineData("0x00007f1234567890", 0, 0, 1)]
    [InlineData("Interop.Sys.Read(int32,byte*,int32)", 0, 0, 1)]
    [InlineData("System.Threading.Monitor.Wait(System.Object)", 0, 1, 0)]
    public void EventPipeLeafClassification_NeverManufacturesOnCpuEvidence(
        string frame,
        long onCpu,
        long heuristicWait,
        long unknown)
    {
        EventPipeCpuSampler.ClassifyLeafEvidence(frame)
            .Should().Be(new SelfSampleBreakdown(onCpu, heuristicWait, unknown));
    }

    [Fact]
    public void NativeCpuEvidence_ExplicitlyRepresentsOsOnCpuObservations()
    {
        CpuSampleEvidence.LinuxPerfOnCpu.Kind.Should().Be(CpuSampleEvidenceKind.OsOnCpuSamples);
        new SelfSampleBreakdown(42, 0).UnknownSamples.Should().Be(0);
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, 0, 1)]
    public void CallTreeBuilder_PreservesEveryEvidenceBucket(long onCpu, long wait, long unknown)
    {
        var builder = new CallTreeBuilder();
        List<(string Key, string Module, string Display)> stack =
            [("MyApp!Wrapper", "MyApp", "MyApp.Wrapper()")];
        var observation = new SelfSampleBreakdown(onCpu, wait, unknown);
        builder.AddStack(stack, "MyApp!Wrapper", observation);
        builder.AddStack(stack, "MyApp!Wrapper", observation);

        var tree = builder.Build();

        tree.InclusiveSamples.Should().Be(2);
        var leaf = tree.Children.Should().ContainSingle().Subject;
        leaf.ExclusiveSamples.Should().Be(2);
        leaf.SelfSamples.Should().Be(new SelfSampleBreakdown(onCpu * 2, wait * 2, unknown * 2));
        CpuSampleAnalytics.TotalSelfSamples(tree).Should().Be(leaf.SelfSamples);
    }

    [Fact]
    public void EventPipeTopMethods_PreservesUnknownCountsWithoutCpuClaim()
    {
        var builder = new CallTreeBuilder();
        List<(string Key, string Module, string Display)> stack =
            [("MyApp!Wrapper", "MyApp.dll", "MyApp.PrivateWaitWrapper()")];
        for (var i = 0; i < 25; i++)
        {
            builder.AddStack(stack, "MyApp!Wrapper", new SelfSampleBreakdown(0, 0, 1));
        }
        var artifact = new CpuSampleTraceArtifact(
            123,
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromSeconds(5),
            25,
            builder.Build())
        {
            Evidence = CpuSampleEvidence.EventPipeSampleProfiler,
        };

        var result = CpuSampleQueryDispatcher.RenderTopMethods(
            artifact,
            "cpu-1",
            sortBy: "running",
            topN: 1);

        result.Data!.EvidenceBackend.Should().Be(CpuSampleBackend.EventPipeSampleProfiler);
        result.Data.EvidenceKind.Should().Be(CpuSampleEvidenceKind.StackFrequencyWithHeuristicWaits);
        result.Data.SelfSamples.Should().Be(new SelfSampleBreakdown(0, 0, 25));
        result.Data.Methods[0].SelfSamples.Should().Be(new SelfSampleBreakdown(0, 0, 25));
        result.Summary.Should().Contain("does not establish on-CPU state");
        result.Summary.Should().NotContain("25 running");
    }

    [Fact]
    public void LegacyArtifact_DeserializesWithUnknownEvidenceAndPreservedOldCounts()
    {
        var artifact = OnCpuArtifact(10);
        var node = JsonNode.Parse(JsonSerializer.Serialize(artifact))!.AsObject();
        node.Remove("Evidence");
        RemoveUnknownSamples(node);

        var legacy = JsonSerializer.Deserialize<CpuSampleTraceArtifact>(node.ToJsonString());

        legacy.Should().NotBeNull();
        legacy!.Evidence.Should().BeNull();
        legacy.SelfSamples.Should().Be(new SelfSampleBreakdown(10, 0, 0));
    }

    [Fact]
    public void CpuComparison_RejectsMixedEvidenceSemantics()
    {
        var projector = new CpuSampleComparableProjector();
        var onCpu = projector.Project(OnCpuArtifact(10), "on-cpu");
        var eventPipe = projector.Project(EventPipeArtifact(10), "eventpipe");

        var diff = SnapshotDiffer.Compare([onCpu, eventPipe]);

        diff.Verdict.Should().Be("incomparable");
        diff.Notes.Should().ContainSingle(note => note.Contains("incompatible evidence semantics", StringComparison.Ordinal));
    }

    [Fact]
    public void EventPipeComparison_IsInconclusiveAndUsesNeutralFrequencyMetrics()
    {
        var projector = new CpuSampleComparableProjector();
        var baseline = projector.Project(EventPipeArtifact(10), "before");
        var current = projector.Project(EventPipeArtifact(20), "after");

        var diff = SnapshotDiffer.Compare([baseline, current]);

        diff.Verdict.Should().Be("inconclusive");
        baseline.Rows.Single().Metrics.Single(metric => metric.Definition.Name == "exclusivePercent")
            .Definition.Role.Should().Be(MetricRole.Context);
        baseline.Metrics.Should().Contain(metric => metric.Definition.Name == "unknownStateSelfSamples");
        baseline.Metrics.Should().NotContain(metric => metric.Definition.Name == "runningSelfPercent");
    }

    private static CpuSampleTraceArtifact OnCpuArtifact(long samples)
        => Artifact(samples, new SelfSampleBreakdown(samples, 0), CpuSampleEvidence.LinuxPerfOnCpu);

    private static CpuSampleTraceArtifact EventPipeArtifact(long samples)
        => Artifact(samples, new SelfSampleBreakdown(0, 0, samples), CpuSampleEvidence.EventPipeSampleProfiler);

    private static CpuSampleTraceArtifact Artifact(
        long samples,
        SelfSampleBreakdown self,
        CpuSampleEvidence evidence)
    {
        var leaf = new CallTreeNode(new SampledFrame("MyApp.dll", "MyApp.Work()"), samples, samples, [])
        {
            SelfSamples = self,
        };
        return new CpuSampleTraceArtifact(
            123,
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromSeconds(5),
            samples,
            new CallTreeNode(new SampledFrame(string.Empty, "<root>"), samples, 0, [leaf]))
        {
            Evidence = evidence,
            SelfSamples = self,
        };
    }

    private static void RemoveUnknownSamples(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            obj.Remove("UnknownSamples");
            foreach (var value in obj.Select(static property => property.Value))
            {
                RemoveUnknownSamples(value);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var value in array)
            {
                RemoveUnknownSamples(value);
            }
        }
    }
}
