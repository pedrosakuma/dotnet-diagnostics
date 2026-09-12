using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Evidence;
using FluentAssertions;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace DotnetDiagnostics.Core.Tests;

[Collection("LiveProcess")]
public sealed class GcDumpKnownGraphTests
{
    private const string RootType = "GcDumpKnownRoot";
    private const string BranchType = "GcDumpKnownBranch";
    private const string LeafType = "GcDumpKnownLeaf";
    private const string PressureType = "GcDumpPressureNode";
    private const string ByteArrayType = "System.Byte[]";

    [Fact(Timeout = 90_000)]
    public async Task NormalControl_RawOracleValidatesKnownSubgraph_AndProductAggregates()
    {
        await using var sample = await StartSampleAsync();
        using var http = new HttpClient { BaseAddress = new Uri(sample.BaseUrl) };
        var fixture = await ResetAsync(http);

        var trial = await CaptureAsync(sample, snapshotTopTypes: 4_096, timeout: TimeSpan.FromSeconds(30));
        try
        {
            var raw = GcDumpRawGraphOracle.Read(trial.TracePath);
            var after = await GetStatusAsync(http);

            raw.BudgetExceeded.Should().BeFalse(raw.InconclusiveReason);
            raw.GetTypeCount(RootType).Should().Be(fixture.RootCount);
            raw.GetTypeCount(BranchType).Should().Be(fixture.BranchCount);
            raw.GetTypeCount(LeafType).Should().Be(fixture.LeafCount);
            raw.CountEdges(RootType, BranchType).Should().Be(fixture.ExpectedRootBranchEdges);
            raw.CountEdges(BranchType, LeafType).Should().Be(fixture.ExpectedBranchLeafEdges);
            raw.CountRootReachableNodes(RootType).Should().Be(fixture.RootCount);

            AssertProductTypeCount(trial.Snapshot, RootType, fixture.RootCount);
            AssertProductTypeCount(trial.Snapshot, BranchType, fixture.BranchCount);
            AssertProductTypeCount(trial.Snapshot, LeafType, fixture.LeafCount);
            AssertCleanCompletionWithUnobservableLoss(trial.Snapshot);

            WriteTrial("normal-control", WithGcDeltas(before: fixture, after), trial.Snapshot, raw);
        }
        finally
        {
            DeleteTrialArtifacts(trial.ArtifactRoot);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task ProjectionLimited_ProductOmissionDoesNotChangeRawKnownGraph()
    {
        await using var sample = await StartSampleAsync();
        using var http = new HttpClient { BaseAddress = new Uri(sample.BaseUrl) };
        var fixture = await ResetAsync(http);

        var trial = await CaptureAsync(sample, snapshotTopTypes: 1, timeout: TimeSpan.FromSeconds(30));
        try
        {
            var raw = GcDumpRawGraphOracle.Read(trial.TracePath);
            var after = await GetStatusAsync(http);

            raw.BudgetExceeded.Should().BeFalse(raw.InconclusiveReason);
            raw.GetTypeCount(RootType).Should().Be(fixture.RootCount);
            raw.GetTypeCount(BranchType).Should().Be(fixture.BranchCount);
            raw.GetTypeCount(LeafType).Should().Be(fixture.LeafCount);
            raw.CountEdges(RootType, BranchType).Should().Be(fixture.ExpectedRootBranchEdges);
            raw.CountEdges(BranchType, LeafType).Should().Be(fixture.ExpectedBranchLeafEdges);
            raw.CountRootReachableNodes(RootType).Should().Be(fixture.RootCount);
            trial.Snapshot.Quality!.Limitations.Should().Contain(l =>
                l.Category == EvidenceLimitationCategory.OutputProjection
                && l.Scope == "snapshot-top-types"
                && l.AffectedCount > 0);
            trial.Snapshot.TopTypesByBytes.Select(t => t.TypeFullName)
                .Concat(trial.Snapshot.TopTypesByInstances.Select(t => t.TypeFullName))
                .Distinct(StringComparer.Ordinal)
                .Should().HaveCountLessThan(3);

            WriteTrial("projection-limited", WithGcDeltas(before: fixture, after), trial.Snapshot, raw);
        }
        finally
        {
            DeleteTrialArtifacts(trial.ArtifactRoot);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Timeout_IsExplicitAndDoesNotPublishACompleteTrace()
    {
        await using var sample = await StartSampleAsync();
        using var http = new HttpClient { BaseAddress = new Uri(sample.BaseUrl) };
        var fixture = await ResetAsync(http);
        var artifactRoot = CreateArtifactRoot("timeout");
        var requestedTimeout = TimeSpan.FromMilliseconds(100);
        var timer = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var collector = new GcDumpHeapSnapshotCollector(artifactRoot: new TestArtifactRootProvider(artifactRoot));
            try
            {
                var snapshot = await collector.CollectAsync(
                    sample.ProcessId,
                    new GcDumpOptions(
                        TopTypes: 20,
                        SnapshotTopTypes: 200,
                        Timeout: requestedTimeout,
                        ExportTrace: true),
                    CancellationToken.None);

                snapshot.GcDumpStatus.Should().NotBeNull();
                snapshot.GcDumpStatus!.TimedOut.Should().BeTrue();
                snapshot.GcDumpStatus.TraceExportCompleted.Should().BeFalse();
                snapshot.TracePath.Should().BeNull();
                snapshot.Quality!.Limitations.Should().Contain(l =>
                    l.Category == EvidenceLimitationCategory.CaptureWindow
                    && l.Scope == "timeout");
                WriteTrial("timeout", fixture, snapshot, raw: null);
            }
            catch (IOException exception) when (TimeoutFailureClassifier.IsTimeoutAssociated(
                exception,
                timer.Elapsed,
                requestedTimeout))
            {
                var partialTraceFilePresent = Directory
                    .EnumerateFiles(artifactRoot, "*.nettrace", SearchOption.AllDirectories)
                    .Any();
                partialTraceFilePresent.Should().BeFalse();
                Console.WriteLine("GCDUMP_KNOWN_GRAPH_TRIAL " + JsonSerializer.Serialize(new
                {
                    trial = "timeout",
                    fixture,
                    outcome = "aborted",
                    requestedTimeoutMilliseconds = requestedTimeout.TotalMilliseconds,
                    elapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
                    exception = exception.GetType().FullName,
                    innerException = exception.InnerException?.GetType().FullName,
                    socketError = (exception.InnerException as SocketException)?.SocketErrorCode.ToString(),
                    exception.Message,
                    tracePublished = false,
                    partialTraceFilePresent,
                }));
            }
        }
        finally
        {
            DeleteTrialArtifacts(artifactRoot);
        }
    }

    [Theory]
    [InlineData(SocketError.ConnectionAborted, 100, 100, true)]
    [InlineData(SocketError.OperationAborted, 101, 100, true)]
    [InlineData(SocketError.ConnectionReset, 100, 100, false)]
    [InlineData(SocketError.ConnectionAborted, 99, 100, false)]
    [InlineData(SocketError.ConnectionAborted, 5_101, 100, false)]
    public void TimeoutFailureClassifier_RequiresAbortSubtypeAndElapsedBudget(
        SocketError socketError,
        int elapsedMilliseconds,
        int timeoutMilliseconds,
        bool expected)
    {
        var exception = new IOException(
            "transport failed",
            new SocketException((int)socketError));

        TimeoutFailureClassifier.IsTimeoutAssociated(
                exception,
                TimeSpan.FromMilliseconds(elapsedMilliseconds),
                TimeSpan.FromMilliseconds(timeoutMilliseconds))
            .Should().Be(expected);
    }

    [Fact]
    public void TimeoutFailureClassifier_RejectsUnrelatedIoFailure()
    {
        TimeoutFailureClassifier.IsTimeoutAssociated(
                new IOException("disk failed"),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(100))
            .Should().BeFalse();
    }

    [Fact(Timeout = 15_000)]
    public async Task PreCancelledCapture_AbortsWithoutPublishingTrace_AndOwnedProcessRemainsHealthy()
    {
        await using var sample = await StartSampleAsync();
        using var http = new HttpClient { BaseAddress = new Uri(sample.BaseUrl) };
        var fixture = await ResetAsync(http);
        var artifactRoot = CreateArtifactRoot("cancelled");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            var collector = new GcDumpHeapSnapshotCollector(artifactRoot: new TestArtifactRootProvider(artifactRoot));
            var act = async () => await collector.CollectAsync(
                sample.ProcessId,
                new GcDumpOptions(Timeout: TimeSpan.FromSeconds(5), ExportTrace: true),
                cancellation.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
            sample.IsRunning.Should().BeTrue();
            (await GetStatusAsync(http)).RootCount.Should().Be(fixture.RootCount);
            var partialTraceFilePresent = Directory
                .EnumerateFiles(artifactRoot, "*.nettrace", SearchOption.AllDirectories)
                .Any();
            partialTraceFilePresent.Should().BeFalse();
            Console.WriteLine("GCDUMP_KNOWN_GRAPH_TRIAL " + JsonSerializer.Serialize(new
            {
                trial = "pre-cancelled",
                fixture,
                outcome = "cancelled",
                targetStillRunning = sample.IsRunning,
                tracePublished = false,
                partialTraceFilePresent,
            }));
        }
        finally
        {
            DeleteTrialArtifacts(artifactRoot);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task RetainedHeapPressure_IsPresentThroughoutCapture_AndKnownSubgraphRemainsExact()
    {
        const int pressureCount = 80_000;
        const int payloadBytes = 512;
        await using var sample = await StartSampleAsync();
        using var http = new HttpClient { BaseAddress = new Uri(sample.BaseUrl) };
        var before = await ResetAsync(http);
        using var pressureResponse = await http.PostAsync(
            $"/gcdump-known-graph/pressure/prepare?objectCount={pressureCount}&payloadBytes={payloadBytes}",
            content: null);
        pressureResponse.EnsureSuccessStatusCode();
        var prepared = (await pressureResponse.Content.ReadFromJsonAsync<KnownGraphStatus>())!;
        var captureRequestedAtUtc = DateTimeOffset.UtcNow;

        if (!prepared.PressureReady
            || prepared.PressureCount != pressureCount
            || prepared.PressureReadyAtUtc is null
            || prepared.PressureReadyAtUtc > captureRequestedAtUtc)
        {
            WriteInconclusivePressureTrial(before, prepared, captureRequestedAtUtc);
            throw new InvalidOperationException("The retained pressure condition was not established before capture.");
        }

        var trial = await CaptureAsync(sample, snapshotTopTypes: 4_096, timeout: TimeSpan.FromSeconds(30));
        var captureCompletedAtUtc = DateTimeOffset.UtcNow;
        try
        {
            var raw = GcDumpRawGraphOracle.Read(trial.TracePath);
            var after = await GetStatusAsync(http);

            raw.BudgetExceeded.Should().BeFalse(raw.InconclusiveReason);
            trial.Snapshot.Quality!.Conclusions.RetainedExplicitPositiveEvidence.Should()
                .Be(EvidenceConclusionSupport.Supported);
            raw.GetTypeCount(RootType).Should().Be(before.RootCount);
            raw.GetTypeCount(BranchType).Should().Be(before.BranchCount);
            raw.GetTypeCount(LeafType).Should().Be(before.LeafCount);
            raw.CountEdges(RootType, BranchType).Should().Be(before.ExpectedRootBranchEdges);
            raw.CountEdges(BranchType, LeafType).Should().Be(before.ExpectedBranchLeafEdges);
            raw.CountRootReachableNodes(RootType).Should().Be(before.RootCount);
            raw.GraphWindowEstablished.Should().BeTrue();
            raw.FirstNodeRelativeMilliseconds!.Value.Should()
                .BeGreaterThanOrEqualTo(raw.GcStartRelativeMilliseconds!.Value);
            raw.LastNodeRelativeMilliseconds!.Value.Should()
                .BeGreaterThanOrEqualTo(raw.FirstNodeRelativeMilliseconds.Value);
            raw.GcStopRelativeMilliseconds!.Value.Should()
                .BeGreaterThanOrEqualTo(raw.LastNodeRelativeMilliseconds.Value);
            raw.GetTypeCount(PressureType).Should().Be(pressureCount);
            raw.CountEdges(PressureType, ByteArrayType).Should().Be(pressureCount);
            AssertProductTypeCount(trial.Snapshot, PressureType, pressureCount);
            after.PressureReady.Should().BeTrue();
            after.PressureCount.Should().Be(pressureCount);
            after.PressureReadyAtUtc.Should().Be(prepared.PressureReadyAtUtc);

            WriteTrial(
                "retained-heap-pressure",
                WithGcDeltas(prepared, after),
                trial.Snapshot,
                raw,
                new
                {
                    condition = "established",
                    pressureCount,
                    payloadBytes,
                    prepared.PressureReadyAtUtc,
                    captureRequestedAtUtc,
                    captureCompletedAtUtc,
                    preparationGcDeltas = WithGcDeltas(before, prepared),
                    retainedThroughCapture = after.PressureReady
                        && after.PressureCount == pressureCount
                        && after.PressureReadyAtUtc == prepared.PressureReadyAtUtc,
                });
        }
        finally
        {
            DeleteTrialArtifacts(trial.ArtifactRoot);
        }
    }

    private static async Task<LiveSampleProcess> StartSampleAsync()
        => await LiveSampleProcess.StartPublishedAsync("CoreClrSample", new LiveSampleOptions
        {
            HarvestListeningUrl = true,
            WaitForHttpReady = true,
            ReadinessPath = "/weatherforecast",
        });

    private static async Task<KnownGraphStatus> ResetAsync(HttpClient http)
    {
        using var response = await http.PostAsync("/gcdump-known-graph/reset", content: null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<KnownGraphStatus>())!;
    }

    private static async Task<KnownGraphStatus> GetStatusAsync(HttpClient http)
        => (await http.GetFromJsonAsync<KnownGraphStatus>("/gcdump-known-graph/status"))!;

    private static KnownGraphStatus WithGcDeltas(KnownGraphStatus before, KnownGraphStatus after)
        => after with
        {
            Gen0Collections = after.Gen0Collections - before.Gen0Collections,
            Gen1Collections = after.Gen1Collections - before.Gen1Collections,
            Gen2Collections = after.Gen2Collections - before.Gen2Collections,
        };

    private static async Task<CaptureTrial> CaptureAsync(
        LiveSampleProcess sample,
        int snapshotTopTypes,
        TimeSpan timeout)
    {
        var artifactRoot = CreateArtifactRoot(Guid.NewGuid().ToString("N"));
        var collector = new GcDumpHeapSnapshotCollector(artifactRoot: new TestArtifactRootProvider(artifactRoot));
        var snapshot = await collector.CollectAsync(
            sample.ProcessId,
            new GcDumpOptions(
                TopTypes: Math.Min(20, snapshotTopTypes),
                SnapshotTopTypes: snapshotTopTypes,
                Timeout: timeout,
                ExportTrace: true),
            CancellationToken.None);

        snapshot.GcDumpStatus.Should().NotBeNull();
        snapshot.GcDumpStatus!.TraceExportCompleted.Should().BeTrue(
            "the raw oracle requires the exact EventPipe stream consumed by the production aggregator");
        snapshot.TracePath.Should().NotBeNull();
        return new CaptureTrial(snapshot, Path.Combine(artifactRoot, snapshot.TracePath!), artifactRoot);
    }

    private static void AssertProductTypeCount(HeapSnapshotArtifact snapshot, string typeName, long expected)
        => snapshot.TopTypesByInstances.Should().ContainSingle(t =>
            t.TypeFullName == typeName && t.InstanceCount == expected);

    private static void AssertCleanCompletionWithUnobservableLoss(HeapSnapshotArtifact snapshot)
    {
        snapshot.GcDumpStatus!.GcStopObserved.Should().BeTrue();
        snapshot.GcDumpStatus.EventStreamCompleted.Should().BeTrue();
        snapshot.GcDumpStatus.TimedOut.Should().BeFalse();
        snapshot.GcDumpStatus.ReaderFailed.Should().BeFalse();
        snapshot.Quality!.Limitations.Should().Contain(l =>
            l.Category == EvidenceLimitationCategory.MechanismUnobservable
            && l.Scope == "eventpipe-loss");
    }

    private static string CreateArtifactRoot(string suffix)
        => Path.Combine(AppContext.BaseDirectory, "test-artifacts", nameof(GcDumpKnownGraphTests), suffix);

    private static void DeleteTrialArtifacts(string artifactRoot)
    {
        if (Directory.Exists(artifactRoot))
        {
            Directory.Delete(artifactRoot, recursive: true);
        }
    }

    private static void WriteTrial(
        string name,
        KnownGraphStatus fixture,
        HeapSnapshotArtifact snapshot,
        GcDumpRawGraphResult? raw,
        object? control = null)
    {
        var report = new
        {
            trial = name,
            fixture,
            product = new
            {
                snapshot.Heap.TotalBytes,
                walkDurationMilliseconds = snapshot.WalkDuration.TotalMilliseconds,
                snapshot.GcDumpStatus,
                snapshot.Quality,
                projectedTypeRowsByBytes = snapshot.TopTypesByBytes.Count,
                projectedTypeRowsByInstances = snapshot.TopTypesByInstances.Count,
                fixtureAggregates = snapshot.TopTypesByInstances
                    .Where(t => t.TypeFullName is RootType or BranchType or LeafType or "GcDumpPressureNode")
                    .Select(t => new { t.TypeFullName, t.InstanceCount, t.TotalBytes })
                    .ToArray(),
            },
            raw = raw is null ? null : new
            {
                nodeCount = raw.Nodes.Count,
                edgeCount = raw.Edges.Count,
                rootEdgeCount = raw.Roots.Count,
                typeCount = raw.TypeNames.Count,
                fixtureCounts = new
                {
                    roots = raw.GetTypeCount(RootType),
                    branches = raw.GetTypeCount(BranchType),
                    leaves = raw.GetTypeCount(LeafType),
                    pressureNodes = raw.GetTypeCount("GcDumpPressureNode"),
                },
                fixtureEdges = new
                {
                    rootToBranch = raw.CountEdges(RootType, BranchType),
                    branchToLeaf = raw.CountEdges(BranchType, LeafType),
                    directlyRootedKnownRoots = raw.CountRootedNodes(RootType),
                    rootReachableKnownRoots = raw.CountRootReachableNodes(RootType),
                },
                rootKindsReachingKnownRoots = raw.GetRootKindsReachingType(RootType),
                graphDumpWindow = new
                {
                    raw.GcStartRelativeMilliseconds,
                    raw.FirstNodeRelativeMilliseconds,
                    raw.LastNodeRelativeMilliseconds,
                    raw.GcStopRelativeMilliseconds,
                    raw.GraphWindowEstablished,
                },
                raw.InconclusiveReason,
            },
            control,
            rootPolicy = "The fixture roots are held in two direct private static fields. The oracle verifies captured paths from root records to both objects and reports runtime root kinds, but does not infer unreported root categories or universal root completeness.",
        };
        Console.WriteLine("GCDUMP_KNOWN_GRAPH_TRIAL " + JsonSerializer.Serialize(report));
    }

    private static void WriteInconclusivePressureTrial(
        KnownGraphStatus before,
        KnownGraphStatus prepared,
        DateTimeOffset captureRequestedAtUtc)
        => Console.WriteLine("GCDUMP_KNOWN_GRAPH_TRIAL " + JsonSerializer.Serialize(new
        {
            trial = "retained-heap-pressure",
            outcome = "inconclusive",
            reason = "The retained pressure workload was not ready before capture; the stress cell was not evaluated.",
            before,
            prepared,
            captureRequestedAtUtc,
        }));

    private sealed record CaptureTrial(HeapSnapshotArtifact Snapshot, string TracePath, string ArtifactRoot);

    private sealed record KnownGraphStatus(
        int RootCount,
        int BranchCount,
        int LeafCount,
        int ExpectedRootBranchEdges,
        int ExpectedBranchLeafEdges,
        int PressureCount,
        bool PressureReady,
        DateTimeOffset ResetAtUtc,
        DateTimeOffset? PressureReadyAtUtc,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections);
}

internal static class TimeoutFailureClassifier
{
    private static readonly TimeSpan AbortGrace = TimeSpan.FromSeconds(5);

    public static bool IsTimeoutAssociated(
        IOException exception,
        TimeSpan elapsed,
        TimeSpan requestedTimeout)
        => elapsed >= requestedTimeout
            && elapsed <= requestedTimeout + AbortGrace
            && exception.InnerException is SocketException
            {
                SocketErrorCode: SocketError.ConnectionAborted or SocketError.OperationAborted,
            };
}

internal static class GcDumpRawGraphOracle
{
    private sealed record Node(ulong Address, int EdgeCount);

    public static GcDumpRawGraphResult Read(string tracePath)
    {
        var typeNames = new Dictionary<ulong, string>();
        var nodes = new Dictionary<ulong, ulong>();
        var nodeOrder = new List<Node>();
        var edgeTargets = new List<ulong>();
        var roots = new List<GcDumpRawRoot>();
        var gcNumber = -1;
        double? gcStartRelativeMilliseconds = null;
        double? firstNodeRelativeMilliseconds = null;
        double? lastNodeRelativeMilliseconds = null;
        double? gcStopRelativeMilliseconds = null;
        string? inconclusive = null;

        using var source = new EventPipeEventSource(tracePath);
        source.Clr.GCStart += data =>
        {
            if (gcNumber < 0 && data.Depth == 2 && data.Type != GCType.BackgroundGC)
            {
                gcNumber = data.Count;
                gcStartRelativeMilliseconds = data.TimeStampRelativeMSec;
            }
        };
        source.Clr.GCStop += data =>
        {
            if (data.Count == gcNumber)
            {
                gcStopRelativeMilliseconds = data.TimeStampRelativeMSec;
            }
        };
        source.Clr.TypeBulkType += data =>
        {
            for (var i = 0; i < data.Count; i++)
            {
                if (typeNames.Count >= 4_096)
                {
                    inconclusive ??= "type budget 4096 exceeded";
                    continue;
                }
                var value = data.Values(i);
                typeNames[value.TypeID] = value.TypeName;
            }
        };
        source.Clr.GCBulkNode += data =>
        {
            firstNodeRelativeMilliseconds ??= data.TimeStampRelativeMSec;
            lastNodeRelativeMilliseconds = data.TimeStampRelativeMSec;
            for (var i = 0; i < data.Count; i++)
            {
                var value = data.Values(i);
                if (nodes.Count >= 250_000)
                {
                    inconclusive ??= "node budget 250000 exceeded";
                    continue;
                }
                nodes[value.Address] = value.TypeID;
                nodeOrder.Add(new Node(value.Address, checked((int)value.EdgeCount)));
            }
        };
        source.Clr.GCBulkEdge += data =>
        {
            for (var i = 0; i < data.Count; i++)
            {
                if (edgeTargets.Count < 500_000)
                {
                    edgeTargets.Add(data.Values(i).Target);
                }
                else
                {
                    inconclusive ??= "edge budget 500000 exceeded";
                }
            }
        };
        source.Clr.GCBulkRootEdge += data =>
        {
            for (var i = 0; i < data.Count; i++)
            {
                if (roots.Count < 100_000)
                {
                    var value = data.Values(i);
                    if ((value.GCRootFlag & GCRootFlags.WeakRef) == 0)
                    {
                        roots.Add(new GcDumpRawRoot(
                            value.RootedNodeAddress,
                            value.GCRootKind.ToString()));
                    }
                }
                else
                {
                    inconclusive ??= "root-edge budget 100000 exceeded";
                }
            }
        };
        source.Process();

        var edges = new List<GcDumpRawEdge>(Math.Min(edgeTargets.Count, 500_000));
        var edgeIndex = 0;
        foreach (var node in nodeOrder)
        {
            for (var i = 0; i < node.EdgeCount; i++)
            {
                if (edgeIndex >= edgeTargets.Count)
                {
                    inconclusive ??= $"{nodeOrder.Sum(item => item.EdgeCount) - edgeIndex} declared object edge(s) had no matching edge record";
                    break;
                }
                edges.Add(new GcDumpRawEdge(node.Address, edgeTargets[edgeIndex++]));
            }
        }
        if (edgeIndex < edgeTargets.Count)
        {
            inconclusive ??= $"{edgeTargets.Count - edgeIndex} edge record(s) had no matching source node";
        }

        return new GcDumpRawGraphResult(
            typeNames,
            nodes,
            edges,
            roots,
            gcStartRelativeMilliseconds,
            firstNodeRelativeMilliseconds,
            lastNodeRelativeMilliseconds,
            gcStopRelativeMilliseconds,
            inconclusive);
    }

}

internal sealed record GcDumpRawEdge(ulong Source, ulong Target);

internal sealed record GcDumpRawRoot(ulong Target, string Kind);

internal sealed record GcDumpRawGraphResult(
    IReadOnlyDictionary<ulong, string> TypeNames,
    IReadOnlyDictionary<ulong, ulong> Nodes,
    IReadOnlyList<GcDumpRawEdge> Edges,
    IReadOnlyList<GcDumpRawRoot> Roots,
    double? GcStartRelativeMilliseconds,
    double? FirstNodeRelativeMilliseconds,
    double? LastNodeRelativeMilliseconds,
    double? GcStopRelativeMilliseconds,
    string? InconclusiveReason)
{
    public bool BudgetExceeded => InconclusiveReason is not null;
    public bool GraphWindowEstablished
        => GcStartRelativeMilliseconds is not null
            && FirstNodeRelativeMilliseconds is not null
            && LastNodeRelativeMilliseconds is not null
            && GcStopRelativeMilliseconds is not null;

    public int GetTypeCount(string typeName)
    {
        var typeIds = TypeNames.Where(pair => pair.Value == typeName).Select(pair => pair.Key).ToHashSet();
        return Nodes.Count(pair => typeIds.Contains(pair.Value));
    }

    public int CountEdges(string sourceTypeName, string targetTypeName)
    {
        var sourceTypes = TypeNames.Where(pair => pair.Value == sourceTypeName).Select(pair => pair.Key).ToHashSet();
        var targetTypes = TypeNames.Where(pair => pair.Value == targetTypeName).Select(pair => pair.Key).ToHashSet();
        var sourceAddresses = Nodes.Where(pair => sourceTypes.Contains(pair.Value)).Select(pair => pair.Key).ToHashSet();
        var targetAddresses = Nodes.Where(pair => targetTypes.Contains(pair.Value)).Select(pair => pair.Key).ToHashSet();
        return Edges.Count(edge => sourceAddresses.Contains(edge.Source) && targetAddresses.Contains(edge.Target));
    }

    public int CountRootedNodes(string typeName)
    {
        var typeIds = TypeNames.Where(pair => pair.Value == typeName).Select(pair => pair.Key).ToHashSet();
        var addresses = Nodes.Where(pair => typeIds.Contains(pair.Value)).Select(pair => pair.Key).ToHashSet();
        return Roots.Select(root => root.Target).Distinct().Count(addresses.Contains);
    }

    public int CountRootReachableNodes(string typeName)
        => GetRootReachability(typeName).ReachableTargets.Count;

    public IReadOnlyList<string> GetRootKindsReachingType(string typeName)
        => GetRootReachability(typeName).RootKinds;

    private (HashSet<ulong> ReachableTargets, IReadOnlyList<string> RootKinds) GetRootReachability(string typeName)
    {
        var typeIds = TypeNames.Where(pair => pair.Value == typeName).Select(pair => pair.Key).ToHashSet();
        var targets = Nodes.Where(pair => typeIds.Contains(pair.Value)).Select(pair => pair.Key).ToHashSet();
        var adjacency = Edges
            .GroupBy(edge => edge.Source)
            .ToDictionary(group => group.Key, group => group.Select(edge => edge.Target).ToArray());
        var reached = new HashSet<ulong>();
        var kinds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var root in Roots)
        {
            var visited = new HashSet<ulong>();
            var pending = new Queue<ulong>();
            pending.Enqueue(root.Target);
            while (pending.Count > 0 && visited.Count < 250_000)
            {
                var address = pending.Dequeue();
                if (!visited.Add(address))
                {
                    continue;
                }
                if (targets.Contains(address))
                {
                    reached.Add(address);
                    kinds.Add(root.Kind);
                }
                if (adjacency.TryGetValue(address, out var children))
                {
                    foreach (var child in children)
                    {
                        pending.Enqueue(child);
                    }
                }
            }
        }

        return (reached, kinds.OrderBy(kind => kind, StringComparer.Ordinal).ToArray());
    }
}
