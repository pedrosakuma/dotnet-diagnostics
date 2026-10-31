using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DotnetDiagnostics.Cli;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Evidence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotnetDiagnostics.Cli.Tests;

public sealed class CliStreamingHeapGrowthTests
{
    private static readonly DateTimeOffset BaselineTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Hello_AdvertisesVersionedHeapGrowthCapability()
    {
        var frames = await RunAsync(new MemoryDiagnosticHandleStore(),
            new { type = "hello", protocolVersion = 2 });

        var capability = frames.Should().ContainSingle().Subject.RootElement.GetProperty("heapGrowth");
        capability.GetProperty("version").GetInt32().Should().Be(1);
        capability.GetProperty("available").GetBoolean().Should().BeTrue();
        capability.GetProperty("storeCapacity").GetInt32()
            .Should().Be(DiagnosticHandleStoreOptions.DefaultMaxEntries);
        capability.GetProperty("minimumStoreCapacity").GetInt32().Should().Be(2);
        capability.GetProperty("queryView").GetString().Should().Be("growth");
        capability.GetProperty("handleKind").GetString().Should().Be("heap-snapshot");
        capability.GetProperty("baselineField").GetString().Should().Be("baselineHandle");
        capability.GetProperty("rankBy").EnumerateArray().Select(item => item.GetString())
            .Should().Equal("bytes", "instances");
        capability.GetProperty("requiresSameProcess").GetBoolean().Should().BeTrue();
        capability.GetProperty("requiresSameProvenance").GetBoolean().Should().BeTrue();
        capability.GetProperty("storage").GetString().Should().Be("in-memory");
        capability.GetProperty("boundedLifetime").GetBoolean().Should().BeTrue();
        capability.GetProperty("storePolicy").GetString().Should().Be("shared-configured-bounded");
    }

    [Fact]
    public async Task Hello_ReportsHeapGrowthUnavailableWhenTwoHandlesCannotCoexist()
    {
        var frames = await RunAsync(
            new MemoryDiagnosticHandleStore(maxEntries: 1),
            new { type = "hello", protocolVersion = 2 });

        var capability = frames.Should().ContainSingle().Subject.RootElement.GetProperty("heapGrowth");
        capability.GetProperty("version").GetInt32().Should().Be(1);
        capability.GetProperty("available").GetBoolean().Should().BeFalse();
        capability.GetProperty("storeCapacity").GetInt32().Should().Be(1);
        capability.GetProperty("minimumStoreCapacity").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Query_Growth_ReturnsCoreGrowthWithProvenanceRetentionAndTiming()
    {
        var (processId, baselineTime) = CurrentProcessCaptureIdentity();
        var store = new MemoryDiagnosticHandleStore();
        var baseline = HeapSnapshot(
            HeapSnapshotOrigin.Live,
            processId,
            baselineTime,
            null,
            ("Leaky.Cache", 1_000, 10));
        var current = HeapSnapshot(
            HeapSnapshotOrigin.Live,
            processId,
            baselineTime.AddSeconds(30),
            [
                new RetentionPath(
                    "Leaky.Cache",
                    0xDEAD,
                    [new RetentionFrame("Root.Holder", 0xBEEF) { RootKind = "StaticVar" }],
                    Truncated: false),
            ],
            ("Leaky.Cache", 6_000, 60),
            ("Stable.Type", 500, 5));
        var baselineHandle = Register(store, baseline);
        var currentHandle = Register(store, current);

        var frames = await RunAsync(store,
            new { type = "hello", protocolVersion = 2 },
            new
            {
                type = "query",
                requestId = "growth",
                handle = currentHandle.Id,
                baselineHandle = baselineHandle.Id,
                view = "growth",
                rankBy = "bytes",
                minDeltaPct = 0,
                topN = 10,
            });

        var response = frames[1].RootElement;
        response.GetProperty("type").GetString().Should().Be("query");
        response.GetProperty("handle").GetString().Should().Be(currentHandle.Id);
        response.GetProperty("baselineHandle").GetString().Should().Be(baselineHandle.Id);
        response.GetProperty("view").GetString().Should().Be("growth");
        var result = response.GetProperty("result");
        result.GetProperty("baselineHandle").GetString().Should().Be(baselineHandle.Id);
        result.GetProperty("currentHandle").GetString().Should().Be(currentHandle.Id);
        result.GetProperty("baselineOrigin").GetString().Should().Be("live");
        result.GetProperty("currentOrigin").GetString().Should().Be("live");
        result.GetProperty("processId").GetInt32().Should().Be(processId);
        result.GetProperty("baselineCapturedAt").GetDateTimeOffset().Should().Be(baselineTime);
        result.GetProperty("currentCapturedAt").GetDateTimeOffset().Should().Be(baselineTime.AddSeconds(30));
        result.GetProperty("elapsed").GetString().Should().Be("00:00:30");
        result.GetProperty("verdict").GetString().Should().Be("leak_suspected");
        var grower = result.GetProperty("growers").EnumerateArray()
            .Single(item => item.GetProperty("typeFullName").GetString() == "Leaky.Cache");
        grower.GetProperty("typeFullName").GetString().Should().Be("Leaky.Cache");
        grower.GetProperty("bytesDelta").GetInt64().Should().Be(5_000);
        grower.GetProperty("retentionPaths").GetArrayLength().Should().Be(1);
        grower.GetProperty("retentionPaths")[0].GetProperty("targetObjectAddress").GetString()
            .Should().Be("0xdead");
        grower.GetProperty("omittedRetentionPaths").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Query_Growth_WithExactType_ReturnsBoundedObjectiveEvidenceWithoutVerdictOrConcentration()
    {
        var (processId, baselineTime) = CurrentProcessCaptureIdentity();
        var store = new MemoryDiagnosticHandleStore();
        var primaryBaseline = Register(store, HeapSnapshot(
            HeapSnapshotOrigin.Dump, processId, baselineTime, null, ("Leaky.Cache", 1_000, 10)));
        var primaryCurrent = Register(store, HeapSnapshot(
            HeapSnapshotOrigin.Dump, processId, baselineTime.AddSeconds(1), null, ("Leaky.Cache", 2_000, 20)));
        var secondBaseline = Register(store, HeapSnapshot(
            HeapSnapshotOrigin.Dump, processId + 1, baselineTime.AddSeconds(2), null, ("Leaky.Cache", 2_000, 20)));
        var secondCurrent = Register(store, HeapSnapshot(
            HeapSnapshotOrigin.Dump, processId + 1, baselineTime.AddSeconds(3), null, ("Leaky.Cache", 3_200, 32)));
        var thirdBaseline = Register(store, HeapSnapshot(
            HeapSnapshotOrigin.Dump, processId + 2, baselineTime.AddSeconds(4), null, ("Leaky.Cache", 3_200, 32)));
        var thirdCurrent = Register(store, HeapSnapshot(
            HeapSnapshotOrigin.Dump, processId + 2, baselineTime.AddSeconds(5), null, ("Leaky.Cache", 4_600, 46)));

        var frames = await RunAsync(
            store,
            new { type = "hello", protocolVersion = 2 },
            new
            {
                type = "query",
                requestId = "objective",
                handle = primaryCurrent.Id,
                baselineHandle = primaryBaseline.Id,
                view = "growth",
                typeFilter = "Leaky.Cache",
                comparisonPairs = new object[]
                {
                    new
                    {
                        attemptId = "run-2",
                        baselineHandle = secondBaseline.Id,
                        currentHandle = secondCurrent.Id,
                        observedGen2Collections = 2,
                        allocation = new { sampledBytes = 50_000L, sampleEvents = 20L, lostOrDropped = 0L },
                    },
                    new
                    {
                        attemptId = "run-3",
                        baselineHandle = thirdBaseline.Id,
                        currentHandle = thirdCurrent.Id,
                        allocation = new { sampledBytes = 70_000L, sampleEvents = 30L, lostOrDropped = 0L },
                    },
                    new
                    {
                        attemptId = "run-4",
                        outcome = "failed",
                        failureReason = "target exited before current capture",
                    },
                },
            });

        var result = frames[1].RootElement.GetProperty("result");
        result.TryGetProperty("verdict", out _).Should().BeFalse();
        result.TryGetProperty("growers", out _).Should().BeFalse();
        var objective = result.GetProperty("objectiveRetainedGrowth");
        objective.GetProperty("schema").GetString()
            .Should().Be("dotnet-diagnostics/retained-growth-evidence/v1");
        objective.GetProperty("targetType").GetProperty("typeFullName").GetString()
            .Should().Be("Leaky.Cache");
        objective.GetProperty("counts").GetProperty("attempts").GetInt32().Should().Be(4);
        objective.GetProperty("counts").GetProperty("acceptedPairs").GetInt32().Should().Be(3);
        objective.GetProperty("counts").GetProperty("failedAttempts").GetInt32().Should().Be(1);
        objective.GetProperty("repeated").GetProperty("availability").GetString().Should().Be("available");
        objective.GetProperty("repeated").GetProperty("medianRetainedBytesDelta").GetDouble()
            .Should().Be(1_200);
        objective.GetProperty("allocationEvidence").GetProperty("availability").GetString()
            .Should().Be("available");
        objective.GetProperty("allocationEvidence").GetProperty("medianSampledBytes").GetDouble()
            .Should().Be(60_000);
        objective.GetProperty("pairs")[0].GetProperty("retainedBytesDelta").GetInt64()
            .Should().Be(1_000);
    }

    [Fact]
    public async Task Query_Growth_ComparisonPairsRequireObjectiveAndRemainBounded()
    {
        var (processId, baselineTime) = CurrentProcessCaptureIdentity();
        var store = new MemoryDiagnosticHandleStore();
        var baseline = Register(store, HeapSnapshot(
            HeapSnapshotOrigin.Dump, processId, baselineTime, null, ("Cache", 1_000, 10)));
        var current = Register(store, HeapSnapshot(
            HeapSnapshotOrigin.Dump, processId, baselineTime.AddSeconds(1), null, ("Cache", 2_000, 20)));
        var pair = new
        {
            attemptId = "extra",
            outcome = "failed",
            failureReason = "fixture",
        };

        var missingObjective = (await RunAsync(
            store,
            new { type = "hello", protocolVersion = 2 },
            new
            {
                type = "query",
                requestId = "missing-objective",
                handle = current.Id,
                baselineHandle = baseline.Id,
                view = "growth",
                comparisonPairs = new[] { pair },
            }))[1].RootElement;
        missingObjective.GetProperty("code").GetString().Should().Be("invalid_query");
        missingObjective.GetProperty("message").GetString().Should().Contain("typeFilter");

        var tooMany = (await RunAsync(
            store,
            new { type = "hello", protocolVersion = 2 },
            new
            {
                type = "query",
                requestId = "bounded",
                handle = current.Id,
                baselineHandle = baseline.Id,
                view = "growth",
                typeFilter = "Cache",
                comparisonPairs = Enumerable.Range(1, 8)
                    .Select(index => new
                    {
                        attemptId = $"failed-{index}",
                        outcome = "failed",
                        failureReason = "fixture",
                    })
                    .ToArray(),
            }))[1].RootElement;
        tooMany.GetProperty("code").GetString().Should().Be("invalid_query");
        tooMany.GetProperty("message").GetString().Should().Contain("at most 7");
    }

    [Fact]
    public async Task Query_Growth_GcDumpPreservesQualityAndMissingRetentionNote()
    {
        var (processId, baselineTime) = CurrentProcessCaptureIdentity();
        var quality = new EvidenceQuality(
            EvidenceQuality.SchemaV1,
            [
                new EvidenceLimitation(
                    EvidenceLimitationCategory.CaptureWindow,
                    "gcdump",
                    null,
                    "The EventPipe heap graph is a bounded observation."),
            ],
            new EvidenceConclusionPolicy(
                EvidenceConclusionSupport.Supported,
                EvidenceConclusionSupport.Inconclusive,
                EvidenceConclusionSupport.Inconclusive));
        var store = new MemoryDiagnosticHandleStore();
        var baselineHandle = Register(store, HeapSnapshot(
            HeapSnapshotOrigin.GcDump, processId, baselineTime, null, ("Cache", 1_000, 10)) with { Quality = quality });
        var currentHandle = Register(store, HeapSnapshot(
            HeapSnapshotOrigin.GcDump, processId, baselineTime.AddSeconds(5), null, ("Cache", 2_000, 20)) with { Quality = quality });

        var response = (await GrowthQueryAsync(store, currentHandle.Id, baselineHandle.Id)).RootElement;

        response.GetProperty("type").GetString().Should().Be("query");
        var result = response.GetProperty("result");
        result.GetProperty("baselineOrigin").GetString().Should().Be("gcDump");
        result.GetProperty("currentOrigin").GetString().Should().Be("gcDump");
        result.GetProperty("verdict").GetString().Should().Be("inconclusive");
        result.GetProperty("baselineQuality").GetProperty("limitations")[0].GetProperty("scope").GetString()
            .Should().Be("gcdump");
        result.GetProperty("currentQuality").GetProperty("conclusions")
            .GetProperty("regressionOrHealthyControl").GetString().Should().Be("inconclusive");
        result.GetProperty("notes").EnumerateArray().Select(note => note.GetString())
            .Should().Contain(note => note!.Contains("no retention paths", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HeapSnapshotOrigin.Live)]
    [InlineData(HeapSnapshotOrigin.GcDump)]
    public async Task Query_Growth_LiveOriginsRejectUnavailableTargetWithStructuredReason(
        HeapSnapshotOrigin origin)
    {
        const int unavailableProcessId = int.MaxValue;
        var store = new MemoryDiagnosticHandleStore();
        var baseline = Register(store, HeapSnapshot(
            origin,
            unavailableProcessId,
            BaselineTime,
            null,
            ("Cache", 1_000, 10)));
        var current = Register(store, HeapSnapshot(
            origin,
            unavailableProcessId,
            BaselineTime.AddSeconds(1),
            null,
            ("Cache", 2_000, 20)));

        var response = (await GrowthQueryAsync(store, current.Id, baseline.Id)).RootElement;

        response.GetProperty("type").GetString().Should().Be("error");
        response.GetProperty("requestId").GetString().Should().Be("growth");
        response.GetProperty("code").GetString().Should().Be("invalid_query");
        response.GetProperty("reason").GetString().Should().Be("target_process_unavailable");
    }

    [Theory]
    [InlineData(HeapSnapshotOrigin.Live)]
    [InlineData(HeapSnapshotOrigin.GcDump)]
    public async Task Query_Growth_LiveOriginsRejectPidReuseTimestampMismatch(
        HeapSnapshotOrigin origin)
    {
        using var process = Process.GetCurrentProcess();
        var processStartedAt = new DateTimeOffset(process.StartTime).ToUniversalTime();
        var store = new MemoryDiagnosticHandleStore();
        var baseline = Register(store, HeapSnapshot(
            origin,
            process.Id,
            processStartedAt.AddMinutes(-2),
            null,
            ("Cache", 1_000, 10)));
        var current = Register(store, HeapSnapshot(
            origin,
            process.Id,
            processStartedAt.AddMinutes(-1),
            null,
            ("Cache", 2_000, 20)));

        var response = (await GrowthQueryAsync(store, current.Id, baseline.Id)).RootElement;

        response.GetProperty("type").GetString().Should().Be("error");
        response.GetProperty("requestId").GetString().Should().Be("growth");
        response.GetProperty("code").GetString().Should().Be("invalid_query");
        response.GetProperty("reason").GetString().Should().Be("process_lifetime_mismatch");
    }

    [Fact]
    public async Task Query_Growth_DumpDoesNotRequireLiveTarget()
    {
        const int unavailableProcessId = int.MaxValue;
        var store = new MemoryDiagnosticHandleStore();
        var baseline = Register(store, HeapSnapshot(
            HeapSnapshotOrigin.Dump,
            unavailableProcessId,
            BaselineTime,
            null,
            ("Cache", 1_000, 10)));
        var current = Register(store, HeapSnapshot(
            HeapSnapshotOrigin.Dump,
            unavailableProcessId,
            BaselineTime.AddSeconds(1),
            null,
            ("Cache", 2_000, 20)));

        var response = (await GrowthQueryAsync(store, current.Id, baseline.Id)).RootElement;

        response.GetProperty("type").GetString().Should().Be("query");
        response.GetProperty("result").GetProperty("currentOrigin").GetString().Should().Be("dump");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Query_Growth_RejectsMissingOrEmptyBaseline(string? baselineHandle)
    {
        var store = new MemoryDiagnosticHandleStore();
        var current = Register(store, HeapSnapshot(
            HeapSnapshotOrigin.Live, 123, BaselineTime.AddSeconds(1), null, ("Cache", 2_000, 20)));
        var request = new Dictionary<string, object?>
        {
            ["type"] = "query",
            ["requestId"] = "growth",
            ["handle"] = current.Id,
            ["view"] = "growth",
            ["baselineHandle"] = baselineHandle,
        };

        var response = (await RunAsync(store,
            new { type = "hello", protocolVersion = 2 },
            request))[1].RootElement;

        response.GetProperty("code").GetString().Should().Be("invalid_query");
    }

    [Fact]
    public async Task Query_Growth_RejectsMalformedBaseline()
    {
        var store = new MemoryDiagnosticHandleStore();
        var current = Register(store, HeapSnapshot(
            HeapSnapshotOrigin.Live, 123, BaselineTime.AddSeconds(1), null, ("Cache", 2_000, 20)));

        var response = (await RunAsync(store,
            new { type = "hello", protocolVersion = 2 },
            new { type = "query", requestId = "growth", handle = current.Id, view = "growth", baselineHandle = 42 }))[1].RootElement;

        response.GetProperty("code").GetString().Should().Be("invalid_query");
        response.GetProperty("message").GetString().Should().Contain("baselineHandle");
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("expired")]
    [InlineData("capacity_evicted")]
    public async Task Query_Growth_PreservesBaselineUnavailableReason(string expectedStatus)
    {
        var clock = new MutableTimeProvider(BaselineTime);
        var store = new MemoryDiagnosticHandleStore(
            maxEntries: expectedStatus == "capacity_evicted" ? 1 : 2,
            clock: clock);
        var baseline = Register(store, HeapSnapshot(
            HeapSnapshotOrigin.Live, 123, BaselineTime, null, ("Cache", 1_000, 10)),
            expectedStatus == "expired" ? TimeSpan.FromSeconds(1) : TimeSpan.FromMinutes(10));
        if (expectedStatus == "expired")
        {
            clock.Advance(TimeSpan.FromSeconds(2));
        }

        var current = Register(store, HeapSnapshot(
            HeapSnapshotOrigin.Live, 123, BaselineTime.AddSeconds(3), null, ("Cache", 2_000, 20)));
        var requestedBaseline = expectedStatus == "unknown" ? "never-seen" : baseline.Id;

        var response = (await GrowthQueryAsync(store, current.Id, requestedBaseline)).RootElement;

        response.GetProperty("code").GetString().Should().Be("handle_unavailable");
        response.GetProperty("handleRole").GetString().Should().Be("baseline");
        response.GetProperty("status").GetString().Should().Be(expectedStatus);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("expired")]
    [InlineData("capacity_evicted")]
    public async Task Query_Growth_PreservesCurrentUnavailableReason(string expectedStatus)
    {
        var clock = new MutableTimeProvider(BaselineTime);
        var store = new MemoryDiagnosticHandleStore(
            maxEntries: expectedStatus == "capacity_evicted" ? 1 : 2,
            clock: clock);
        var current = Register(store, HeapSnapshot(
            HeapSnapshotOrigin.Live, 123, BaselineTime.AddSeconds(3), null, ("Cache", 2_000, 20)),
            expectedStatus == "expired" ? TimeSpan.FromSeconds(1) : TimeSpan.FromMinutes(10));
        if (expectedStatus == "expired")
        {
            clock.Advance(TimeSpan.FromSeconds(2));
        }

        var baseline = Register(store, HeapSnapshot(
            HeapSnapshotOrigin.Live, 123, BaselineTime, null, ("Cache", 1_000, 10)));
        var requestedCurrent = expectedStatus == "unknown" ? "never-seen" : current.Id;

        var response = (await GrowthQueryAsync(store, requestedCurrent, baseline.Id)).RootElement;

        response.GetProperty("code").GetString().Should().Be("handle_unavailable");
        response.GetProperty("handle").GetString().Should().Be(requestedCurrent);
        response.GetProperty("handleRole").GetString().Should().Be("current");
        response.GetProperty("status").GetString().Should().Be(expectedStatus);
    }

    [Fact]
    public async Task Query_Growth_RejectsWrongKindAndPayload()
    {
        var wrongKindStore = new MemoryDiagnosticHandleStore();
        var current = Register(wrongKindStore, HeapSnapshot(
            HeapSnapshotOrigin.Live, 123, BaselineTime.AddSeconds(1), null, ("Cache", 2_000, 20)));
        var wrongKind = wrongKindStore.Register(123, "cpu-sample", new object(), TimeSpan.FromMinutes(10));
        var wrongKindResponse = (await GrowthQueryAsync(wrongKindStore, current.Id, wrongKind.Id)).RootElement;
        wrongKindResponse.GetProperty("code").GetString().Should().Be("unsupported_handle_kind");
        wrongKindResponse.GetProperty("handleRole").GetString().Should().Be("baseline");
        wrongKindResponse.GetProperty("actualKind").GetString().Should().Be("cpu-sample");
        wrongKindResponse.GetProperty("requiredKind").GetString().Should().Be("heap-snapshot");

        var wrongCurrentKindStore = new MemoryDiagnosticHandleStore();
        var validBaselineForWrongCurrent = Register(wrongCurrentKindStore, HeapSnapshot(
            HeapSnapshotOrigin.Live, 123, BaselineTime, null, ("Cache", 1_000, 10)));
        var wrongCurrentKind = wrongCurrentKindStore.Register(
            123, "cpu-sample", new object(), TimeSpan.FromMinutes(10));
        var wrongCurrentKindResponse = (await GrowthQueryAsync(
            wrongCurrentKindStore, wrongCurrentKind.Id, validBaselineForWrongCurrent.Id)).RootElement;
        wrongCurrentKindResponse.GetProperty("code").GetString().Should().Be("unsupported_handle_kind");
        wrongCurrentKindResponse.GetProperty("handle").GetString().Should().Be(wrongCurrentKind.Id);
        wrongCurrentKindResponse.GetProperty("handleRole").GetString().Should().Be("current");
        wrongCurrentKindResponse.GetProperty("actualKind").GetString().Should().Be("cpu-sample");
        wrongCurrentKindResponse.GetProperty("requiredKind").GetString().Should().Be("heap-snapshot");

        var wrongPayloadStore = new MemoryDiagnosticHandleStore();
        var wrongPayload = wrongPayloadStore.Register(123, "heap-snapshot", new object(), TimeSpan.FromMinutes(10));
        var validBaseline = Register(wrongPayloadStore, HeapSnapshot(
            HeapSnapshotOrigin.Live, 123, BaselineTime, null, ("Cache", 1_000, 10)));
        var wrongPayloadResponse = (await GrowthQueryAsync(wrongPayloadStore, wrongPayload.Id, validBaseline.Id)).RootElement;
        wrongPayloadResponse.GetProperty("code").GetString().Should().Be("query_failed");
        wrongPayloadResponse.GetProperty("reason").GetString().Should().Be("invalid_payload");
    }

    [Fact]
    public async Task Query_Growth_RejectsParseableNonFiniteMinDeltaPct()
    {
        var store = new MemoryDiagnosticHandleStore();
        var baseline = Register(store, HeapSnapshot(
            HeapSnapshotOrigin.Live, 123, BaselineTime, null, ("Cache", 1_000, 10)));
        var current = Register(store, HeapSnapshot(
            HeapSnapshotOrigin.Live, 123, BaselineTime.AddSeconds(1), null, ("Cache", 2_000, 20)));
        var input = $$"""
            {"type":"hello","protocolVersion":2}
            {"type":"query","requestId":"growth","handle":"{{current.Id}}","baselineHandle":"{{baseline.Id}}","view":"growth","minDeltaPct":1e400}

            """;

        var response = (await RunRawAsync(store, input))[1].RootElement;

        response.GetProperty("code").GetString().Should().Be("invalid_query");
        response.GetProperty("message").GetString().Should().Contain("finite");
    }

    [Fact]
    public async Task Query_Growth_RejectsSameHandleDifferentPidProvenanceAndReversedChronology()
    {
        var store = new MemoryDiagnosticHandleStore();
        var baseline = Register(store, HeapSnapshot(
            HeapSnapshotOrigin.Live, 123, BaselineTime, null, ("Cache", 1_000, 10)));
        var differentPid = Register(store, HeapSnapshot(
            HeapSnapshotOrigin.Live, 456, BaselineTime.AddSeconds(1), null, ("Cache", 2_000, 20)));
        var differentOrigin = Register(store, HeapSnapshot(
            HeapSnapshotOrigin.GcDump, 123, BaselineTime.AddSeconds(1), null, ("Cache", 2_000, 20)));
        var earlierCurrent = Register(store, HeapSnapshot(
            HeapSnapshotOrigin.Live, 123, BaselineTime.AddSeconds(-1), null, ("Cache", 2_000, 20)));

        var same = (await GrowthQueryAsync(store, baseline.Id, baseline.Id)).RootElement;
        same.GetProperty("reason").GetString().Should().Be("same_handle");

        var pid = (await GrowthQueryAsync(store, differentPid.Id, baseline.Id)).RootElement;
        pid.GetProperty("reason").GetString().Should().Be("process_mismatch");

        var provenance = (await GrowthQueryAsync(store, differentOrigin.Id, baseline.Id)).RootElement;
        provenance.GetProperty("reason").GetString().Should().Be("provenance_mismatch");

        var chronology = (await GrowthQueryAsync(store, earlierCurrent.Id, baseline.Id)).RootElement;
        chronology.GetProperty("reason").GetString().Should().Be("reversed_chronology");
    }

    [Fact]
    public async Task Query_SingleHandleHeapView_RemainsWireCompatible()
    {
        var store = new MemoryDiagnosticHandleStore();
        var snapshot = HeapSnapshot(
            HeapSnapshotOrigin.Live, 123, BaselineTime, null, ("Cache", 1_000, 10)) with
        {
            RootsByKind = [new RootKindStat("StrongHandle", 2, 2, 1_000, 0, 0)],
        };
        var handle = Register(store, snapshot);

        var frames = await RunAsync(store,
            new { type = "hello", protocolVersion = 2 },
            new { type = "query", requestId = "roots", handle = handle.Id, view = "roots-by-kind" });

        var response = frames[1].RootElement;
        response.GetProperty("type").GetString().Should().Be("query");
        response.GetProperty("handle").GetString().Should().Be(handle.Id);
        response.GetProperty("view").GetString().Should().Be("roots-by-kind");
        response.TryGetProperty("baselineHandle", out _).Should().BeFalse();
        response.GetProperty("result").GetProperty("view").GetString().Should().Be("roots-by-kind");
    }

    [Fact]
    public async Task Query_SingleHandleUnavailable_RemainsUnknownHandle()
    {
        var response = (await RunAsync(
            new MemoryDiagnosticHandleStore(),
            new { type = "hello", protocolVersion = 2 },
            new { type = "query", requestId = "legacy", handle = "never-seen", view = "roots-by-kind" }))[1]
            .RootElement;

        response.GetProperty("code").GetString().Should().Be("unknown_handle");
        response.TryGetProperty("handleRole", out _).Should().BeFalse();
        response.TryGetProperty("status", out _).Should().BeFalse();
    }

    private static async Task<JsonDocument> GrowthQueryAsync(
        IDiagnosticHandleStore store,
        string currentHandle,
        string baselineHandle)
    {
        var frames = await RunAsync(store,
            new { type = "hello", protocolVersion = 2 },
            new
            {
                type = "query",
                requestId = "growth",
                handle = currentHandle,
                baselineHandle,
                view = "growth",
            });
        return frames[1];
    }

    private static DiagnosticHandle Register(
        MemoryDiagnosticHandleStore store,
        HeapSnapshotArtifact snapshot,
        TimeSpan? ttl = null) =>
        store.Register(
            snapshot.ProcessId,
            "heap-snapshot",
            snapshot,
            ttl ?? TimeSpan.FromMinutes(10),
            evictWhenProcessExits: snapshot.Origin != HeapSnapshotOrigin.Dump);

    private static (int ProcessId, DateTimeOffset CapturedAt) CurrentProcessCaptureIdentity()
    {
        using var process = Process.GetCurrentProcess();
        var processStartedAt = new DateTimeOffset(process.StartTime).ToUniversalTime();
        var capturedAt = DateTimeOffset.UtcNow;
        if (capturedAt <= processStartedAt)
        {
            capturedAt = processStartedAt.AddTicks(1);
        }

        return (process.Id, capturedAt);
    }

    private static HeapSnapshotArtifact HeapSnapshot(
        HeapSnapshotOrigin origin,
        int processId,
        DateTimeOffset capturedAt,
        IReadOnlyList<RetentionPath>? retentionPaths,
        params (string TypeName, long Bytes, long Instances)[] rows)
    {
        var stats = rows.Select(row => new TypeStat(
            row.TypeName,
            "Test.dll",
            row.Instances,
            row.Bytes,
            0,
            new TypeIdentity(row.TypeName) { ModuleName = "Test.dll" })).ToArray();
        return new HeapSnapshotArtifact(
            origin,
            processId,
            capturedAt,
            TimeSpan.FromMilliseconds(10),
            new DumpRuntimeInfo("CoreCLR", "10.0", "x64", IsServerGC: false, HeapCount: 1),
            new DumpHeapSummary(stats.Sum(stat => stat.TotalBytes), 0, 0, 0, 0, 0, 0),
            stats,
            stats)
        {
            RetentionPaths = retentionPaths,
        };
    }

    private static async Task<IReadOnlyList<JsonDocument>> RunAsync(
        IDiagnosticHandleStore store,
        params object[] requests)
    {
        var input = string.Join('\n', requests.Select(request => JsonSerializer.Serialize(request))) + "\n";
        return await RunRawAsync(store, input);
    }

    private static async Task<IReadOnlyList<JsonDocument>> RunRawAsync(
        IDiagnosticHandleStore store,
        string input)
    {
        using var stdin = new StringReader(input);
        var stdoutBuilder = new StringBuilder();
        await using var stdout = new StringWriter(stdoutBuilder);
        await using var stderr = new StringWriter();

        var exit = await CliStreamingProtocol.RunAsync(
            ["stream", "--protocol", "jsonl"],
            stdin,
            stdout,
            stderr,
            BuildHost,
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        exit.Should().Be(0);

        return stdoutBuilder.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line))
            .ToArray();

        IHost BuildHost()
        {
            var builder = Host.CreateApplicationBuilder(["--hostBuilder:reloadConfigOnChange=false"]);
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(store);
            return builder.Build();
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
