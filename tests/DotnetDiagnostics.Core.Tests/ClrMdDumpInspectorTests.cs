using DotnetDiagnostics.Core.Dump;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class ClrMdDumpInspectorTests
{
    [Fact]
    public void StaticFieldTopNAccumulator_RetainsLargestRowsOnly()
    {
        var accumulator = new ClrMdDumpInspector.StaticFieldTopNAccumulator(3);

        accumulator.Add(new StaticFieldStat("A", null, "F1", 1, 0x10, "System.String", 10, 1));
        accumulator.Add(new StaticFieldStat("B", null, "F2", 2, 0x20, "System.String", 50, 1));
        accumulator.Add(new StaticFieldStat("C", null, "F3", 3, 0x30, "System.String", 20, 1));
        accumulator.Add(new StaticFieldStat("D", null, "F4", 4, 0x40, "System.String", 40, 1));
        accumulator.Add(new StaticFieldStat("E", null, "F5", 5, 0x50, "System.String", 30, 1));

        accumulator.ToArray()
            .Select(static stat => stat.DirectlyReferencedBytes)
            .Should()
            .Equal(50L, 40L, 30L);
    }

    [Fact]
    public void DumpInspectionOptions_VerifyHeap_DefaultsToFalse()
    {
        // Off-by-default regression guard (#1119): ClrHeap.VerifyHeap() is a second, more
        // expensive full-heap walk and must never run unless explicitly opted into.
        var options = new DumpInspectionOptions();

        options.VerifyHeap.Should().BeFalse();
    }

    [Fact]
    public void HeapSnapshotArtifact_HeapIntegrity_DefaultsToNull_WhenNotCaptured()
    {
        var artifact = new HeapSnapshotArtifact(
            Origin: HeapSnapshotOrigin.Dump,
            ProcessId: 123,
            CapturedAt: DateTimeOffset.UtcNow,
            WalkDuration: TimeSpan.FromMilliseconds(1),
            Runtime: new DumpRuntimeInfo("CoreCLR", "10.0.0", "X64", IsServerGC: false, HeapCount: 1),
            Heap: new DumpHeapSummary(0, 0, 0, 0, 0, 0, 0),
            TopTypesByBytes: Array.Empty<TypeStat>(),
            TopTypesByInstances: Array.Empty<TypeStat>());

        artifact.HeapIntegrity.Should().BeNull();
    }
}
