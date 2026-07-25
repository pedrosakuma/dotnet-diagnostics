using System.Text.Json;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Memory;
using DotnetDiagnostics.Core.Threads;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnostics.Core.Tests;

public class InvestigationMemoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 18, 22, 0, 0, TimeSpan.Zero);

    private static InvestigationSummaryExporter NewExporter(IProvenanceCollector? prov = null, int seed = 1)
    {
        var counter = seed;
        return new InvestigationSummaryExporter(
            prov ?? new FixedProvenance(),
            clock: new FixedClock(T0),
            idFactory: () => $"inv-test-{counter++}");
    }

    private static CpuSampleTraceArtifact ArtifactFor(params (string module, string method, long incl, long excl)[] frames)
        => ArtifactFor(totalSamples: 1000, frames);

    private static CpuSampleTraceArtifact ArtifactFor(long totalSamples, params (string module, string method, long incl, long excl)[] frames)
    {
        var children = frames.Select(f =>
            new CallTreeNode(new SampledFrame(f.module, f.method), f.incl, f.excl, Array.Empty<CallTreeNode>())).ToArray();
        // Match the synthetic root sentinel produced by EventPipeCpuSampler.CallTreeBuilder.
        var root = new CallTreeNode(new SampledFrame(string.Empty, "<root>"), totalSamples, 0, children);
        return new CpuSampleTraceArtifact(1234, T0, TimeSpan.FromSeconds(10), totalSamples, root);
    }

    private static CpuSampleTraceArtifact ClassifiedArtifactFor(
        long totalSamples,
        params (string module, string method, long incl, long excl, long running, long waiting)[] frames)
    {
        var children = frames.Select(f =>
            new CallTreeNode(new SampledFrame(f.module, f.method), f.incl, f.excl, Array.Empty<CallTreeNode>())
            {
                SelfSamples = new SelfSampleBreakdown(f.running, f.waiting),
            }).ToArray();
        var root = new CallTreeNode(new SampledFrame(string.Empty, "<root>"), totalSamples, 0, children);
        return new CpuSampleTraceArtifact(1234, T0, TimeSpan.FromSeconds(10), totalSamples, root)
        {
            SelfSamples = new SelfSampleBreakdown(
                frames.Sum(static frame => frame.running),
                frames.Sum(static frame => frame.waiting)),
        };
    }

    [Fact]
    public void Export_ProducesV1Schema_AndStableSymbolRefs()
    {
        var artifact = ArtifactFor(("MyApp.dll", "MyApp.HotPath.DoWork", 100, 80), ("MyApp.dll", "MyApp.Cold", 10, 5));
        var exporter = NewExporter();

        var exported = exporter.Export(new ExportRequest("h-1", artifact, TopHotspots: 5, BuildAssemblyName: "MyApp"));

        exported.Summary.Schema.Should().Be(InvestigationSummary.SchemaV1);
        exported.Summary.InvestigationId.Should().Be("inv-test-1");
        exported.Summary.CreatedAt.Should().Be(T0);
        exported.Summary.Provenance.Build!.AssemblyName.Should().Be("MyApp");
        exported.Summary.Findings.TopHotspots.Should().HaveCount(2);
        exported.Summary.Findings.TopHotspots[0].Symbol.Should().Be(new SymbolRef("MyApp.dll", "MyApp.HotPath.DoWork"));
        exported.Summary.Findings.TopHotspots[0].InclusivePercent.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Export_JsonRoundtripsIntoSameSummary()
    {
        var artifact = ArtifactFor(("M.dll", "M.A", 10, 10));
        var exporter = NewExporter();

        var exported = exporter.Export(new ExportRequest("h-1", artifact, Format: SummaryFormat.Json));
        var back = JsonSerializer.Deserialize<InvestigationSummary>(exported.Rendered);

        back.Should().NotBeNull();
        back!.InvestigationId.Should().Be(exported.Summary.InvestigationId);
        back.Findings.TopHotspots[0].Symbol.MethodFullName.Should().Be("M.A");
        back.Findings.KeyMetrics.Should().BeNull();
        exported.Rendered.Should().NotContain("\"Evidence\"",
            "CPU-only summaries retain the original v1 JSON shape");
    }

    [Fact]
    public void Export_PreservesRunningAndWaitingSelfSamples()
    {
        var artifact = ClassifiedArtifactFor(
            100,
            ("System.Private.CoreLib.dll", "System.Threading.ManualResetEventSlim.Wait", 60, 60, 0, 60),
            ("MyApp.dll", "MyApp.Worker.Run", 40, 40, 40, 0));

        var summary = NewExporter().Export(new ExportRequest("h", artifact)).Summary;

        summary.Findings.TopHotspots
            .Single(h => h.Symbol.MethodFullName.EndsWith(".Wait", StringComparison.Ordinal))
            .SelfSamples.Should().Be(new SelfSampleBreakdown(0, 60));
        summary.Findings.TopHotspots
            .Single(h => h.Symbol.MethodFullName.EndsWith(".Run", StringComparison.Ordinal))
            .SelfSamples.Should().Be(new SelfSampleBreakdown(40, 0));
    }

    [Fact]
    public void Export_MarkdownIncludesProvenanceAndHotspots()
    {
        var artifact = ArtifactFor(("App.dll", "App.Service.Process", 100, 50));
        var exporter = NewExporter(new FixedProvenance(
            container: new ContainerProvenance("ghcr.io/me/app:v2", "prod", "app-7c-xy", "node-1")));

        var exported = exporter.Export(new ExportRequest("h-1", artifact, Format: SummaryFormat.Markdown,
            TargetsFix: new InvestigationFixTarget(CommitSha: "abc123", PullRequestUrl: "https://github.com/x/y/pull/42")));

        exported.Rendered.Should().Contain("# Investigation `inv-test-1`")
            .And.Contain("App.Service.Process")
            .And.Contain("ghcr.io/me/app:v2")
            .And.Contain("https://github.com/x/y/pull/42");
    }

    [Fact]
    public void Export_SyncOverAsyncBeforeAfter_UsesQueueBlockingStacksAndThroughputWithoutFixedCpu()
    {
        var exporter = NewExporter();
        var beforeCounters = CounterArtifact(queueLength: 236, throughput: 0);
        var beforeThreads = BlockingThreadArtifact(queueLength: 236, blockedThreadCount: 4);

        var before = exporter.Export(new ExportRequest(
            Evidence:
            [
                new InvestigationEvidenceInput("counters-before", "counters", beforeCounters),
                new InvestigationEvidenceInput("threads-before", "thread-snapshot", beforeThreads),
            ],
            Notes: "Sync-over-async suspected from queue growth plus blocking stacks."));

        var after = exporter.Export(new ExportRequest(
            Evidence:
            [
                new InvestigationEvidenceInput(
                    "counters-after",
                    "counters",
                    CounterArtifact(queueLength: 0, throughput: 50)),
            ],
            PreviousInvestigationId: before.Summary.InvestigationId,
            Notes: "Queue drained and request throughput recovered after the fix."));

        before.Summary.Findings.TopHotspots.Should().BeEmpty();
        before.Summary.Findings.KeyMetrics.Should().Contain(new Dictionary<string, double>
        {
            ["threadpool-queue-length"] = 236,
            ["requests-per-second"] = 0,
        });
        before.Summary.Evidence.Should().HaveCount(2);
        before.Summary.Evidence![0].SourceTool.Should().Be("collect_events");
        before.Summary.Evidence[0].Origin.Should().Be("live");
        before.Summary.Evidence[1].SourceTool.Should().Be("collect_thread_snapshot");
        before.Summary.Evidence[1].Findings.Should().ContainSingle(finding =>
            finding.Category == "blocking-stack"
            && finding.Count == 4
            && finding.Summary.Contains("TaskAwaiter.GetResult", StringComparison.Ordinal)
            && finding.Summary.Contains("ManualResetEventSlim.Wait", StringComparison.Ordinal));

        after.Summary.Findings.TotalSamples.Should().Be(0);
        after.Summary.Findings.TopHotspots.Should().BeEmpty();
        after.Summary.Findings.KeyMetrics.Should().Contain(new Dictionary<string, double>
        {
            ["threadpool-queue-length"] = 0,
            ["requests-per-second"] = 50,
        });
        after.Summary.PreviousInvestigationId.Should().Be(before.Summary.InvestigationId);
        after.Summary.Evidence.Should().ContainSingle()
            .Which.Handle.Should().Be("counters-after");

        var diff = new SummaryComparer().Compare(before.Summary, after.Summary);
        diff.Verdict.Should().Be("improvement");
        diff.KeyMetricDeltas.Should().Contain(delta =>
            delta.Name == "threadpool-queue-length" && delta.Outcome == "improved");
        diff.KeyMetricDeltas.Should().Contain(delta =>
            delta.Name == "requests-per-second" && delta.Outcome == "improved");
    }

    [Fact]
    public void Export_GcEvidence_ProjectsPauseMetricsAndProvenance()
    {
        var gc = new GcSummary(
            ProcessId: 1234,
            StartedAt: T0,
            Duration: TimeSpan.FromSeconds(5),
            TotalCollections: 3,
            TotalPauseTime: TimeSpan.FromMilliseconds(12),
            MaxPauseTime: TimeSpan.FromMilliseconds(7),
            Generations: [new GenerationStats(0, 2), new GenerationStats(2, 1)],
            Events: []);

        var exported = NewExporter().Export(new ExportRequest(
            Evidence: [new InvestigationEvidenceInput("gc-before", "gc-events", gc)]));

        exported.Summary.Findings.KeyMetrics.Should().Contain(new Dictionary<string, double>
        {
            ["gc-total-collections"] = 3,
            ["gc-total-pause-ms"] = 12,
            ["gc-max-pause-ms"] = 7,
        });
        exported.Summary.Evidence.Should().ContainSingle()
            .Which.SourceKind.Should().Be("gc");
    }

    [Fact]
    public void Compare_NoChange_ReturnsNoRegressionVerdict()
    {
        var artifact = ArtifactFor(("M.dll", "M.A", 100, 80));
        var exporter = NewExporter();
        var s1 = exporter.Export(new ExportRequest("h", artifact)).Summary;
        var s2 = exporter.Export(new ExportRequest("h", artifact)).Summary;

        var diff = new SummaryComparer().Compare(s1, s2);

        diff.Verdict.Should().Be("no_regression");
        diff.NewHotspots.Should().BeEmpty();
        diff.RemovedHotspots.Should().BeEmpty();
        diff.ChangedHotspots.Should().BeEmpty();
    }

    [Fact]
    public void Compare_NewHotspot_FlagsRegression()
    {
        var exporter = NewExporter();
        var baseline = exporter.Export(new ExportRequest("h", ArtifactFor(("M.dll", "M.A", 100, 80)))).Summary;
        var current = exporter.Export(new ExportRequest("h", ArtifactFor(
            ("M.dll", "M.A", 100, 80),
            ("M.dll", "M.NewlyHot", 60, 60)))).Summary;

        var diff = new SummaryComparer().Compare(baseline, current);

        diff.Verdict.Should().Be("regression_new_hotspot");
        diff.NewHotspots.Should().ContainSingle()
            .Which.Symbol.MethodFullName.Should().Be("M.NewlyHot");
    }

    [Fact]
    public void Compare_RemovedHotspotOnly_IsImprovement()
    {
        var exporter = NewExporter();
        var baseline = exporter.Export(new ExportRequest("h", ArtifactFor(
            ("M.dll", "M.A", 100, 80),
            ("M.dll", "M.GoneSoon", 60, 60)))).Summary;
        var current = exporter.Export(new ExportRequest("h", ArtifactFor(("M.dll", "M.A", 100, 80)))).Summary;

        var diff = new SummaryComparer().Compare(baseline, current);

        diff.Verdict.Should().Be("improvement");
        diff.RemovedHotspots.Should().ContainSingle()
            .Which.Symbol.MethodFullName.Should().Be("M.GoneSoon");
    }

    [Fact]
    public void Compare_RemovedBlockingHotspotAndImprovedSymptoms_DoesNotRegressForNewRunningLeader()
    {
        var exporter = NewExporter();
        var baseline = exporter.Export(new ExportRequest("before", ClassifiedArtifactFor(
            1000,
            ("System.Private.CoreLib.dll", "System.Threading.ManualResetEventSlim.Wait", 515, 515, 0, 515),
            ("MyApp.dll", "MyApp.Endpoint.SyncOverAsync", 320, 0, 0, 0)))).Summary;
        var current = exporter.Export(new ExportRequest("after", ClassifiedArtifactFor(
            1000,
            ("System.Net.Sockets.dll", "System.Net.Sockets.SocketAsyncEngine.EventLoop", 300, 300, 300, 0),
            ("MyApp.dll", "MyApp.Endpoint.SyncOverAsyncFixed", 240, 0, 0, 0)))).Summary;
        baseline = baseline with
        {
            Findings = baseline.Findings with
            {
                KeyMetrics = new Dictionary<string, double>
                {
                    ["threadpool-queue-length"] = 236,
                    ["threadpool-thread-count"] = 141,
                    ["requests-completed"] = 0,
                    ["request-throughput"] = 0,
                },
            },
        };
        current = current with
        {
            Findings = current.Findings with
            {
                KeyMetrics = new Dictionary<string, double>
                {
                    ["threadpool-queue-length"] = 0,
                    ["threadpool-thread-count"] = 22,
                    ["requests-completed"] = 50,
                    ["request-throughput"] = 16.45,
                },
            },
        };

        var diff = new SummaryComparer().Compare(baseline, current);

        diff.Verdict.Should().Be("improvement");
        diff.Verdict.Should().NotBe("regression_new_hotspot");
        diff.RemovedHotspots.Should().Contain(h =>
            h.Symbol.MethodFullName.EndsWith(".Wait", StringComparison.Ordinal)
            && h.BaselineSelfSamples == new SelfSampleBreakdown(0, 515));
        diff.NewHotspots.Should().Contain(h =>
            h.Symbol.MethodFullName.EndsWith(".EventLoop", StringComparison.Ordinal)
            && h.CurrentSelfSamples == new SelfSampleBreakdown(300, 0));
        diff.KeyMetricDeltas.Should().OnlyContain(delta => delta.Outcome == "improved");
    }

    [Fact]
    public void Compare_UnclassifiedHotspotTurnoverWithoutComparableSymptoms_IsIncomparable()
    {
        var exporter = NewExporter();
        var baseline = exporter.Export(new ExportRequest("before", ArtifactFor(("M.dll", "M.Blocked", 60, 60)))).Summary;
        var current = exporter.Export(new ExportRequest("after", ArtifactFor(("M.dll", "M.NewLeader", 70, 70)))).Summary;

        var diff = new SummaryComparer().Compare(baseline, current);

        diff.Verdict.Should().Be("incomparable");
    }

    [Fact]
    public void Compare_ConflictingComparableSymptoms_IsMixed()
    {
        var exporter = NewExporter();
        var artifact = ClassifiedArtifactFor(100, ("M.dll", "M.Run", 50, 50, 50, 0));
        var baseline = exporter.Export(new ExportRequest("before", artifact)).Summary;
        var current = exporter.Export(new ExportRequest("after", artifact)).Summary;
        baseline = baseline with
        {
            Findings = baseline.Findings with
            {
                KeyMetrics = new Dictionary<string, double>
                {
                    ["threadpool-queue-length"] = 100,
                    ["requests-completed"] = 50,
                },
            },
        };
        current = current with
        {
            Findings = current.Findings with
            {
                KeyMetrics = new Dictionary<string, double>
                {
                    ["threadpool-queue-length"] = 0,
                    ["requests-completed"] = 25,
                },
            },
        };

        var diff = new SummaryComparer().Compare(baseline, current);

        diff.Verdict.Should().Be("mixed");
        diff.KeyMetricDeltas.Should().Contain(delta => delta.Outcome == "improved");
        diff.KeyMetricDeltas.Should().Contain(delta => delta.Outcome == "regressed");
    }

    [Fact]
    public void Compare_UnchangedMetricPlusMissingVerdictMetric_IsIncomparable()
    {
        var exporter = NewExporter();
        var artifact = ClassifiedArtifactFor(100, ("M.dll", "M.Run", 50, 50, 50, 0));
        var baseline = exporter.Export(new ExportRequest("before", artifact)).Summary;
        var current = exporter.Export(new ExportRequest("after", artifact)).Summary;
        baseline = baseline with
        {
            Findings = baseline.Findings with
            {
                KeyMetrics = new Dictionary<string, double>
                {
                    ["threadpool-queue-length"] = 0,
                    ["requests-completed"] = 50,
                },
            },
        };
        current = current with
        {
            Findings = current.Findings with
            {
                KeyMetrics = new Dictionary<string, double>
                {
                    ["threadpool-queue-length"] = 0,
                },
            },
        };

        var diff = new SummaryComparer().Compare(baseline, current);

        diff.Verdict.Should().Be("incomparable");
        diff.KeyMetricDeltas.Should().Contain(delta =>
            delta.Name == "requests-completed" && delta.Outcome == "incomparable");
    }

    [Fact]
    public void Compare_MetricNamesMatchCaseAndPunctuationInsensitively()
    {
        var exporter = NewExporter();
        var artifact = ClassifiedArtifactFor(100, ("M.dll", "M.Run", 50, 50, 50, 0));
        var baseline = exporter.Export(new ExportRequest("before", artifact)).Summary;
        var current = exporter.Export(new ExportRequest("after", artifact)).Summary;
        baseline = baseline with
        {
            Findings = baseline.Findings with
            {
                KeyMetrics = new Dictionary<string, double>
                {
                    ["ThreadPool Queue Length"] = 236,
                },
            },
        };
        current = current with
        {
            Findings = current.Findings with
            {
                KeyMetrics = new Dictionary<string, double>
                {
                    ["threadpool-queue-length"] = 0,
                },
            },
        };

        var diff = new SummaryComparer().Compare(baseline, current);

        diff.Verdict.Should().Be("improvement");
        diff.KeyMetricDeltas.Should().ContainSingle()
            .Which.Should().Match<KeyMetricDelta>(delta =>
                delta.Name == "ThreadPool Queue Length"
                && delta.BaselineValue == 236
                && delta.CurrentValue == 0
                && delta.Outcome == "improved");
        diff.Notes.Should().NotContain(note => note.Contains("absent", StringComparison.Ordinal));
    }

    [Fact]
    public void Compare_CanonicalMetricCollision_KeepsOrdinalFirstName()
    {
        var exporter = NewExporter();
        var artifact = ClassifiedArtifactFor(100, ("M.dll", "M.Run", 50, 50, 50, 0));
        var baseline = exporter.Export(new ExportRequest("before", artifact)).Summary;
        var current = exporter.Export(new ExportRequest("after", artifact)).Summary;
        baseline = baseline with
        {
            Findings = baseline.Findings with
            {
                KeyMetrics = new Dictionary<string, double>
                {
                    ["threadpool-queue-length"] = 999,
                    ["ThreadPool Queue Length"] = 100,
                },
            },
        };
        current = current with
        {
            Findings = current.Findings with
            {
                KeyMetrics = new Dictionary<string, double>
                {
                    ["threadpool_queue_length"] = 50,
                },
            },
        };

        var diff = new SummaryComparer().Compare(baseline, current);

        diff.Verdict.Should().Be("improvement");
        diff.KeyMetricDeltas.Should().ContainSingle()
            .Which.BaselineValue.Should().Be(100);
        diff.Notes.Should().ContainSingle(note =>
            note.Contains("keeping 'ThreadPool Queue Length' deterministically", StringComparison.Ordinal));
    }

    [Fact]
    public void Compare_DetectsImageJumpInProvenance()
    {
        var artifact = ArtifactFor(("M.dll", "M.A", 100, 80));
        var oldProv = new FixedProvenance(container: new ContainerProvenance("ghcr.io/me/app:v1", "prod", "p1", "n1"));
        var newProv = new FixedProvenance(container: new ContainerProvenance("ghcr.io/me/app:v2", "prod", "p2", "n1"));
        var baseline = NewExporter(oldProv, seed: 1).Export(new ExportRequest("h", artifact)).Summary;
        var current = NewExporter(newProv, seed: 2).Export(new ExportRequest("h", artifact)).Summary;

        var diff = new SummaryComparer().Compare(baseline, current);

        diff.Verdict.Should().Be("no_regression_after_deploy");
        diff.Provenance.ImageChanged.Should().BeTrue();
        diff.Provenance.Summary.Should().Contain("v1").And.Contain("v2");
    }

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) { _now = now; }
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static CounterSnapshot CounterArtifact(int queueLength, double throughput)
        => new(
            ProcessId: 1234,
            StartedAt: T0,
            Duration: TimeSpan.FromSeconds(5),
            Counters:
            [
                new CounterValue(
                    "System.Runtime",
                    "threadpool-queue-length",
                    "ThreadPool Queue Length",
                    queueLength,
                    CounterKind.Mean),
                new CounterValue(
                    "Microsoft.AspNetCore.Hosting",
                    "requests-per-second",
                    "Requests / sec",
                    throughput,
                    CounterKind.Mean),
            ],
            Meters: [],
            Notes: []);

    private static ThreadSnapshotArtifact BlockingThreadArtifact(int queueLength, int blockedThreadCount)
    {
        var frames = new[]
        {
            new ManagedStackFrame(
                "Managed",
                "System.Runtime.CompilerServices.TaskAwaiter.GetResult",
                "System.Runtime.CompilerServices.TaskAwaiter",
                "System.Private.CoreLib.dll",
                0,
                0),
            new ManagedStackFrame(
                "Managed",
                "System.Threading.ManualResetEventSlim.Wait",
                "System.Threading.ManualResetEventSlim",
                "System.Private.CoreLib.dll",
                0,
                0),
            new ManagedStackFrame(
                "Managed",
                "Sample.SyncOverAsyncController.Get",
                "Sample.SyncOverAsyncController",
                "Sample.dll",
                0,
                0),
        };
        var threads = Enumerable.Range(1, blockedThreadCount)
            .Select(index => new ManagedThread(
                ManagedThreadId: index,
                OSThreadId: (uint)index,
                Address: (ulong)index,
                State: "Waiting",
                IsAlive: true,
                IsBackground: true,
                IsFinalizer: false,
                IsGc: false,
                IsThreadpoolWorker: true,
                LockCount: 0,
                CurrentExceptionType: null,
                TopFrameMethod: frames[0].DisplayName,
                Frames: frames)
            {
                IsLikelyBlocked = true,
                InferredWaitReason = "Task",
            })
            .ToArray();

        return new ThreadSnapshotArtifact(
            ThreadSnapshotOrigin.Live,
            ProcessId: 1234,
            CapturedAt: T0.AddSeconds(1),
            WalkDuration: TimeSpan.FromMilliseconds(25),
            RuntimeName: ".NET",
            RuntimeVersion: "10.0.0",
            Threads: threads,
            Locks: [])
        {
            ThreadPool = new ThreadPoolSnapshot(
                Initialized: true,
                UsingPortableThreadPool: true,
                UsingWindowsThreadPool: false,
                Workers: new ThreadPoolWorkerState(141, 141, 0, 0, 1, 32767),
                Iocp: new ThreadPoolIocpState(1, 1, 1, 1000),
                Queues: new ThreadPoolQueueState(queueLength, [], []),
                PendingWorkItems: queueLength),
        };
    }

    private sealed class FixedProvenance : IProvenanceCollector
    {
        private readonly ContainerProvenance? _container;
        public FixedProvenance(ContainerProvenance? container = null) { _container = container; }

        public InvestigationProvenance Collect(int processId, string? buildAssemblyName = null)
            => new(Hostname: "test-host")
            {
                Build = buildAssemblyName is null ? null : new BuildProvenance(buildAssemblyName, null, null, null, null),
                Container = _container,
            };
    }
}
