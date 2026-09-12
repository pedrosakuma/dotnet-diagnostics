using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Evidence;
using DotnetDiagnostics.Mcp.Resources;
using FluentAssertions;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

public sealed class HeapSnapshotResourceTests
{
    [Fact]
    public void ReadSnapshot_PreservesGcDumpQualityAndCompletionStatus()
    {
        var quality = new EvidenceQuality(
            EvidenceQuality.SchemaV1,
            [new EvidenceLimitation(EvidenceLimitationCategory.MechanismUnobservable, "eventpipe-loss", null, "Loss is unavailable.")],
            new EvidenceConclusionPolicy(
                EvidenceConclusionSupport.Supported,
                EvidenceConclusionSupport.Inconclusive,
                EvidenceConclusionSupport.Inconclusive));
        var snapshot = new HeapSnapshotArtifact(
            HeapSnapshotOrigin.GcDump,
            123,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(1),
            new DumpRuntimeInfo("CoreCLR", "10.0.0", "X64", false, 0),
            new DumpHeapSummary(100, 0, 0, 0, 0, 0, 100),
            [new TypeStat("System.String", null, 2, 100, 100)],
            [new TypeStat("System.String", null, 2, 100, 100)])
        {
            Quality = quality,
            GcDumpStatus = new GcDumpCaptureStatus(true, true, false, false, true, true),
            TracePath = "traces/capture.nettrace",
        };
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(123, "heap-snapshot", snapshot, TimeSpan.FromMinutes(10));

        var json = HeapSnapshotResources.ReadSnapshot(store, handle.Id);

        json.Should().Contain("\"Quality\"");
        json.Should().Contain("\"MechanismUnobservable\"");
        json.Should().Contain("\"GcDumpStatus\"");
        json.Should().Contain("\"GcStopObserved\":true");
        json.Should().Contain("\"TracePath\":\"traces/capture.nettrace\"");
    }
}
