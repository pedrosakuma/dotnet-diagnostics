using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.EventSources;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Memory;
using DotnetDiagnostics.Core.MethodParameters;
using DotnetDiagnostics.Core.OffCpu;
using DotnetDiagnostics.Core.Threads;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed partial class CaptureArtifactCodecTests
{
    private const int Budget = 4 * 1024 * 1024;
    private const string Rich = "Módulo!A|B\0\"\\\r\n漢字🧪 e\u0301 <redacted:literal>";
    private static readonly DateTimeOffset At = new(2026, 9, 24, 12, 34, 56, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromTicks(12_345_678);

    [Theory]
    [MemberData(nameof(Snapshots))]
    public void AllRegisteredFamilies_RetainSnapshotAndExistingQueries(string kind, object artifact)
    {
        var encoded = CaptureArtifactCodec.Encode(kind, artifact, Budget);
        var restored = CaptureArtifactCodec.Decode(kind, CaptureArtifactCodec.FormatVersion, encoded, Budget);
        restored.GetType().Should().Be(artifact.GetType());
        restored.Should().BeEquivalentTo(artifact);
        CaptureArtifactCodec.Encode(kind, restored, Budget).Should().Equal(encoded);
        CaptureArtifactCodec.Encode(kind, artifact, encoded.Length).Should().Equal(encoded);
        Action wrongType = () => CaptureArtifactCodec.Encode(kind, new object(), Budget);
        wrongType.Should().Throw<ArgumentException>();
        CaptureArtifactCodec.GetSupportedSnapshotViews(kind, restored)
            .Should().Equal(CaptureArtifactCodec.GetSupportedSnapshotViews(kind, artifact));

        foreach (var view in CaptureArtifactCodec.GetSupportedSnapshotViews(kind, artifact))
        {
            var before = Query(kind, view, artifact);
            var after = Query(kind, view, restored);
            JsonSerializer.Serialize(after).Should().Be(JsonSerializer.Serialize(before), $"{kind}/{view} must use the same retained evidence");
        }
    }

    [Fact]
    public void RejectsUnknownKindWrongConcreteTypeAndFutureVersions()
    {
        var snapshot = Counters();
        Action unknown = () => CaptureArtifactCodec.Encode("CounterSnapshot", snapshot, Budget);
        unknown.Should().Throw<NotSupportedException>();
        Action wrong = () => CaptureArtifactCodec.Encode("counters", Cpu(), Budget);
        wrong.Should().Throw<ArgumentException>();
        Action views = () => CaptureArtifactCodec.GetSupportedSnapshotViews("counters", Cpu());
        views.Should().Throw<ArgumentException>();
        var bytes = CaptureArtifactCodec.Encode("counters", snapshot, Budget);
        Action future = () => CaptureArtifactCodec.Decode("counters", 2, bytes, Budget);
        future.Should().Throw<NotSupportedException>();
        Action previous = () => CaptureArtifactCodec.Decode("counters", 0, bytes, Budget);
        previous.Should().Throw<NotSupportedException>();
        Action wrongKind = () => CaptureArtifactCodec.Decode("exception-snapshot", 1, bytes, Budget);
        wrongKind.Should().Throw<JsonException>();
    }

    [Fact]
    public void ByteBoundsApplyDuringEncodingAndBeforeDecoding_ExactBoundarySucceeds()
    {
        var snapshot = Counters();
        var bytes = CaptureArtifactCodec.Encode("counters", snapshot, Budget);
        CaptureArtifactCodec.Encode("counters", snapshot, bytes.Length).Should().Equal(bytes);
        Action encode = () => CaptureArtifactCodec.Encode("counters", snapshot, bytes.Length - 1);
        encode.Should().Throw<InvalidDataException>();
        Action decode = () => CaptureArtifactCodec.Decode("counters", 1, bytes, bytes.Length - 1);
        decode.Should().Throw<InvalidDataException>();
        CaptureArtifactCodec.Decode("counters", 1, bytes, bytes.Length).Should().BeOfType<CounterSnapshot>();
        Action zero = () => CaptureArtifactCodec.Encode("counters", snapshot, 0);
        zero.Should().Throw<ArgumentOutOfRangeException>();

        // This producer throws if serialization ever consumes all rows; the byte cap must win.
        var huge = snapshot with { Counters = new CountingCounters() };
        Action bounded = () => CaptureArtifactCodec.Encode("counters", huge, 1024);
        bounded.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"kind\":\"counters\",\"snapshot\":{}}")]
    [InlineData("{\"kind\":\"counters\",\"snapshot\":null}")]
    [InlineData("{\"kind\":\"counters\",\"kind\":\"counters\",\"snapshot\":{}}")]
    [InlineData("{\"kind\":\"counters\",\"snapshot\":[]}")]
    [InlineData("{\"kind\":\"counters\",\"snapshot\":{\"ProcessId\":\"not-an-integer\"}}")]
    public void MalformedPayloadsFailExplicitly(string json)
    {
        Action decode = () => CaptureArtifactCodec.Decode("counters", 1, Encoding.UTF8.GetBytes(json), Budget);
        decode.Should().Throw<JsonException>();
    }

    [Fact]
    public void UnknownPropertiesTrailingDataAndExcessiveDepthFail()
    {
        var payload = JsonNode.Parse(CaptureArtifactCodec.Encode("counters", Counters(), Budget))!;
        payload["snapshot"]!["$type"] = "System.Diagnostics.Process";
        Action unknown = () => CaptureArtifactCodec.Decode("counters", 1, Encoding.UTF8.GetBytes(payload.ToJsonString()), Budget);
        unknown.Should().Throw<JsonException>();
        var bytes = CaptureArtifactCodec.Encode("counters", Counters(), Budget).Concat("{}"u8.ToArray()).ToArray();
        Action trailing = () => CaptureArtifactCodec.Decode("counters", 1, bytes, Budget);
        trailing.Should().Throw<JsonException>();
        var deep = Encoding.UTF8.GetBytes(new string('[', 65) + "0" + new string(']', 65));
        Action depth = () => CaptureArtifactCodec.Decode("counters", 1, deep, Budget);
        depth.Should().Throw<JsonException>();
    }

    [Theory]
    [InlineData("Root")]
    [InlineData("MethodIdentities")]
    [InlineData("ResolvedSources")]
    [InlineData("Notes")]
    public void NullRequiredFieldsCannotBecomeConstructorDefaults(string property)
    {
        var payload = JsonNode.Parse(CaptureArtifactCodec.Encode("cpu-sample", Cpu(), Budget))!;
        payload["snapshot"]![property] = null;
        Action decode = () => CaptureArtifactCodec.Decode("cpu-sample", 1, Encoding.UTF8.GetBytes(payload.ToJsonString()), Budget);
        decode.Should().Throw<JsonException>();
    }

    [Fact]
    public void EncodingInvalidRequiredNullsFailsRatherThanWritingUnreadableSnapshots()
    {
        Action collection = () => CaptureArtifactCodec.Encode("counters", Counters() with { Counters = null! }, Budget);
        collection.Should().Throw<JsonException>();
        Action frame = () => CaptureArtifactCodec.Encode("cpu-sample", Cpu() with { Root = null! }, Budget);
        frame.Should().Throw<JsonException>();
        var cpu = Cpu() with { Evidence = null, TracePath = null };
        var restored = (CpuSampleTraceArtifact)CaptureArtifactCodec.Decode("cpu-sample", 1,
            CaptureArtifactCodec.Encode("cpu-sample", cpu, Budget), Budget);
        restored.Evidence.Should().BeNull();
        restored.TracePath.Should().BeNull();
    }

    [Fact]
    public void InvalidStringsAndNonfiniteMeasurementsFailWithoutReplacement()
    {
        var snapshot = Counters() with { Notes = ["unpaired:\ud800"] };
        Action unicode = () => CaptureArtifactCodec.Encode("counters", snapshot, Budget);
        unicode.Should().Throw<JsonException>();
        snapshot = Counters() with { Counters = [Counters().Counters[0] with { Value = double.NaN }] };
        Action nonfinite = () => CaptureArtifactCodec.Encode("counters", snapshot, Budget);
        nonfinite.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TimestampOffsetsTicksCounterIntervalsAndLegacyNullsSurvive()
    {
        var snapshot = Counters() with { StartedAt = At.ToOffset(TimeSpan.FromHours(5.5)) };
        var restored = (CounterSnapshot)CaptureArtifactCodec.Decode("counters", 1,
            CaptureArtifactCodec.Encode("counters", snapshot, Budget), Budget);
        restored.StartedAt.Offset.Should().Be(snapshot.StartedAt.Offset);
        restored.StartedAt.UtcTicks.Should().Be(snapshot.StartedAt.UtcTicks);
        restored.Duration.Ticks.Should().Be(Window.Ticks);
        restored.Counters[1].IntervalSec.Should().Be(0.125);
        restored.Counters[1].DisplayRateTimeScale.Should().Be(TimeSpan.FromTicks(1001));
        restored.Meters[0].Tags[Rich].Should().BeNull();
        restored.Notes[0].Should().Be(Rich);
        var heap = Heap() with { Quality = null };
        ((HeapSnapshotArtifact)CaptureArtifactCodec.Decode("heap-snapshot", 1,
            CaptureArtifactCodec.Encode("heap-snapshot", heap, Budget), Budget)).Quality.Should().BeNull();
    }

    [Fact]
    public void Cpu_DeepTreeIsFlatOnDiskAndPreservesEveryNodeAndIdentity()
    {
        var trace = Cpu();
        var root = trace.Root;
        for (var i = 0; i < 4096; i++)
            root = new(new SampledFrame(Rich, $"deep-{i}"), 7, 0, [root], trace.Root.Identity);
        trace = trace with { Root = root };
        var restored = (CpuSampleTraceArtifact)CaptureArtifactCodec.Decode("cpu-sample", 1,
            CaptureArtifactCodec.Encode("cpu-sample", trace, Budget), Budget);
        var current = restored.Root;
        for (var i = 4095; i >= 0; i--)
        {
            current.Frame.Method.Should().Be($"deep-{i}");
            current.Identity.Should().BeEquivalentTo(trace.Root.Identity);
            current = current.Children.Single();
        }
        current.Children.Should().HaveCount(2);
        current.Children[0].Frame.Method.Should().Be("LeafA");
        current.Children[1].Frame.Method.Should().Be("LeafB");
        restored.MethodIdentities.Should().BeEquivalentTo(trace.MethodIdentities);
        restored.ResolvedSources.Should().BeEquivalentTo(trace.ResolvedSources);
        restored.TracePath.Should().Be(trace.TracePath);
    }

    [Fact]
    public void Cpu_RejectsCyclesAndInvalidParentRows()
    {
        var children = new List<CallTreeNode>();
        var root = new CallTreeNode(new("m", "cycle"), 1, 1, children);
        children.Add(root);
        Action cycle = () => CaptureArtifactCodec.Encode("cpu-sample", Cpu() with { Root = root }, Budget);
        cycle.Should().Throw<JsonException>();
        var payload = JsonNode.Parse(CaptureArtifactCodec.Encode("cpu-sample", Cpu(), Budget))!;
        payload["snapshot"]!["Root"]![1]!["Parent"] = 1;
        Action parent = () => CaptureArtifactCodec.Decode("cpu-sample", 1, Encoding.UTF8.GetBytes(payload.ToJsonString()), Budget);
        parent.Should().Throw<JsonException>();
    }

    [Fact]
    public void Heap_AdvertisesOnlyPopulatedSelfContainedViews()
    {
        var heap = Heap();
        var views = CaptureArtifactCodec.GetSupportedSnapshotViews("heap-snapshot", heap);
        views.Should().NotContain(["object", "gcroot", "objsize", "duplicate-strings"]);
        views.Should().Contain("top-types").And.Contain("retention-paths");
        CaptureArtifactCodec.GetSupportedSnapshotViews("heap-snapshot", heap with { Origin = HeapSnapshotOrigin.GcDump })
            .Should().Equal("top-types");
        CaptureArtifactCodec.GetSupportedSnapshotViews("heap-snapshot", heap with { RetentionPaths = null })
            .Should().NotContain("retention-paths");
    }

    [Fact]
    public void Thread_IgnoredPublicResponseRolesAndNestedFramesSurvive()
    {
        var original = Threads();
        var restored = (ThreadSnapshotArtifact)CaptureArtifactCodec.Decode("thread-snapshot", 1,
            CaptureArtifactCodec.Encode("thread-snapshot", original, Budget), Budget);
        restored.Should().BeEquivalentTo(original);
        restored.Threads[0].IsContendedLockOwner.Should().BeTrue();
        restored.Threads[0].IsLockWaiter.Should().BeTrue();
        restored.Threads[0].IsDeadlockCandidate.Should().BeTrue();
        restored.Threads[0].Frames[0].Identity!.ModuleVersionId.Should().Be(original.Threads[0].Frames[0].Identity!.ModuleVersionId);
    }

    private static object Query(string kind, string view, object artifact)
    {
        if (CpuSampleQueryDispatcher.ResolveTrace(artifact) is { } cpu)
        {
            return view switch
            {
                "call-tree" => CpuSampleQueryDispatcher.RenderCallTree(cpu, "stable", null, 8, 50),
                "top-methods" => CpuSampleQueryDispatcher.RenderTopMethods(cpu, "stable", "exclusive", 50),
                "by-module" => CpuSampleQueryDispatcher.RenderByModule(cpu, "stable", 50),
                "by-namespace" => CpuSampleQueryDispatcher.RenderByNamespace(cpu, "stable", 50),
                "hot-path" => CpuSampleQueryDispatcher.RenderHotPath(cpu, "stable", 80),
                "caller-callee" => CpuSampleQueryDispatcher.RenderCallerCallee(cpu, "stable", "LeafA", 50),
                "triage" => CpuSampleQueryDispatcher.RenderTriage(cpu, "stable", 50, 80),
                _ => throw new InvalidOperationException(view),
            };
        }
        return artifact switch
        {
            HeapSnapshotArtifact heap => HeapSnapshotQueryDispatcher.Dispatch(heap, "stable", view, 50, "bytes", null),
            ThreadSnapshotArtifact thread => ThreadSnapshotQueryDispatcher.Dispatch(thread, "stable", view, 1, 50, 3, 1),
            GcDatasSnapshot datas => GcDatasQueryDispatcher.Render(datas, "stable", view, 50),
            EventCatalogSnapshot catalog => EventCatalogQueryDispatcher.Render(catalog, "stable", view, 50),
            OffCpuSnapshotArtifact offCpu => OffCpuQueryDispatcher.Dispatch(offCpu, view, 50, 1),
            MethodParameterCaptureArtifact method => MethodParameterCaptureQueryDispatcher.Render(method, "stable", view, 50),
            _ => CollectionQueryDispatcher.Dispatch(kind, view, artifact, 50, null, "0123456789abcdef0123456789abcdef", null),
        };
    }

    private sealed class CountingCounters : IReadOnlyList<CounterValue>
    {
        public int Count => 1_000_000;
        public CounterValue this[int index] => throw new NotSupportedException();
        public IEnumerator<CounterValue> GetEnumerator()
        {
            for (var i = 0; i < 100; i++) yield return new("provider", Rich, Rich, 12.5, CounterKind.Mean, "ms");
            throw new InvalidOperationException("Byte bound was not applied during enumeration.");
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
