using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

public sealed class HeapSnapshotSecurityGateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryStructArray_FieldsRespectSensitivePolicy(bool emit)
    {
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(123, "heap-snapshot", SnapshotWithStrings() with { Origin = HeapSnapshotOrigin.Dump }, TimeSpan.FromMinutes(10));
        var inspector = new StubInspector
        {
            ObjectInspection = new HeapObjectInspection(0x1234, "Row[]", 64, "Small", "Gen0")
            {
                IsArray = true, ArrayLength = 11, ArrayStartIndex = 0, ArrayRequestedCount = 8,
                ArraySample = [new HeapArrayElement(0, "Row", "<value-type size=40>")
                {
                    Fields = [new HeapObjectField("Nested.Value", "System.String", "hello, world") { ObjectAddress = 0x5678 }],
                }],
            },
        };
        var options = new SecurityOptions { AllowSensitiveHeapValues = emit };
        var result = await DiagnosticTools.QueryHeapSnapshot(store, inspector, new SensitiveDataRedactor(options),
            new SensitiveValueGate(options), TestPrincipalAccessors.Root, handle.Id,
            view: "object", address: "0x1234", includeSensitiveValues: emit);
        result.Error.Should().BeNull();
        var inspection = result.Data!.ObjectDetails!;
        inspection.ArrayStartIndex.Should().Be(0);
        inspection.ArrayRequestedCount.Should().Be(8);
        var field = inspection.ArraySample![0].Fields![0];
        field.Value.Should().Be(emit ? "hello, world" : SensitiveDataRedactor.MetadataOnlyPlaceholder);
        field.ObjectAddress.Should().Be(0x5678);
    }

    [Fact]
    public async Task QueryDuplicateStrings_Default_RedactsToMetadataOnly()
    {
        var store = new MemoryDiagnosticHandleStore();
        var artifact = SnapshotWithStrings(new DuplicateStringStat("Authorization: Bearer eyJabcdefghij.payload.signature", 50, 10, 10_240, false));
        var handle = store.Register(123, "heap-snapshot", artifact, TimeSpan.FromMinutes(10));
        var inspector = NoopInspector();

        var result = await DiagnosticTools.QueryHeapSnapshot(
            store, inspector, new SensitiveDataRedactor(null), new SensitiveValueGate(null),
            TestPrincipalAccessors.Root,
            handle.Id, view: "duplicate-strings", topN: 10);

        result.Error.Should().BeNull();
        result.Data!.DuplicateStrings.Should().HaveCount(1);
        result.Data.DuplicateStrings![0].Preview.Should().Be(SensitiveDataRedactor.MetadataOnlyPlaceholder);
    }

    [Fact]
    public async Task QueryDuplicateStrings_OptedIn_AppliesRedactorButReturnsBenignContent()
    {
        var store = new MemoryDiagnosticHandleStore();
        var artifact = SnapshotWithStrings(
            new DuplicateStringStat("Authorization: Bearer eyJabcdefghij.payload.signature", 50, 10, 10_240, false),
            new DuplicateStringStat("hello, world", 12, 5, 256, false));
        var handle = store.Register(123, "heap-snapshot", artifact, TimeSpan.FromMinutes(10));
        var inspector = NoopInspector();
        var options = new SecurityOptions { AllowSensitiveHeapValues = true };

        var result = await DiagnosticTools.QueryHeapSnapshot(
            store, inspector, new SensitiveDataRedactor(options), new SensitiveValueGate(options),
            TestPrincipalAccessors.Root,
            handle.Id, view: "duplicate-strings", topN: 10, includeSensitiveValues: true);

        result.Error.Should().BeNull();
        result.Data!.DuplicateStrings.Should().HaveCount(2);
        result.Data.DuplicateStrings![0].Preview.Should().Contain(SensitiveDataRedactor.RedactedPlaceholder);
        result.Data.DuplicateStrings[0].Preview.Should().NotContain("eyJabcdefghij");
        result.Data.DuplicateStrings[1].Preview.Should().Be("hello, world");
    }

    private static HeapSnapshotArtifact SnapshotWithStrings(params DuplicateStringStat[] strings) => new HeapSnapshotArtifact(
        Origin: HeapSnapshotOrigin.Live,
        ProcessId: 123,
        CapturedAt: DateTimeOffset.UtcNow,
        WalkDuration: TimeSpan.FromMilliseconds(50),
        Runtime: new DumpRuntimeInfo("CoreCLR", "10.0.0", "X64", IsServerGC: false, HeapCount: 1),
        Heap: new DumpHeapSummary(1024, 0, 0, 1024, 0, 0, 1024),
        TopTypesByBytes: [],
        TopTypesByInstances: [])
    {
        DuplicateStrings = strings,
    };

    private static IDumpInspector NoopInspector() => new StubInspector();

    private sealed class StubInspector : IDumpInspector
    {
        public HeapObjectInspection? ObjectInspection { get; init; }
        public Task<HeapSnapshotArtifact> InspectAsync(string dumpFilePath, DumpInspectionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<HeapSnapshotArtifact> InspectLiveAsync(int processId, DumpInspectionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<HeapObjectInspection> InspectObjectAsync(HeapSnapshotArtifact snapshot, ulong address, CancellationToken cancellationToken = default)
            => Task.FromResult(ObjectInspection ?? throw new NotSupportedException());
        public Task<HeapGcRootInspection> InspectGcRootAsync(HeapSnapshotArtifact snapshot, ulong address, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<HeapObjectSizeInspection> InspectObjectSizeAsync(HeapSnapshotArtifact snapshot, ulong address, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
