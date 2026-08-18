using System.Diagnostics;
using System.IO;
using System.Linq;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Memory;
using DotnetDiagnostics.Core.OffCpu;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Unit tests for the NativeAOT CPU sampling fallback. The parser is fully unit-tested
/// against fixed <c>perf script</c> output; the live <c>perf record</c> path is exercised
/// only in environments with kernel permission (skipped here).
/// </summary>
public class PerfScriptParserTests
{
    private const string DefaultLineEnding = "\n";
    private const string DefaultPidSpacing = "  ";
    public static TheoryData<string, string> CrossOsFilterCases => new()
    {
        { "\n", "  " },
        { "\r\n", "  " },
        { "\r\n", "      " },
    };

    private static string TwoSamplesFromPid1 => CreateTwoSamplesFromPid1(DefaultLineEnding, DefaultPidSpacing);

    [Fact]
    public void FormatPerfFileSize_UsesPerfAcceptedMiBSuffix_ForExactMiBCounts()
    {
        PerfNativeAotCpuSampler.FormatPerfFileSize(512L * 1024 * 1024)
            .Should().Be("512M");
    }

    [Fact]
    public void FormatPerfFileSize_FallsBackToRawBytes_ForNonMiBCounts()
    {
        PerfNativeAotCpuSampler.FormatPerfFileSize(12345)
            .Should().Be("12345");
    }

    [Fact]
    public async Task BoundedProcessExecution_InternalDeadline_ThrowsTimeout()
    {
        using var process = new Process();

        var act = async () => await BoundedProcessExecution.RunAsync(
            process,
            TimeSpan.FromMilliseconds(20),
            "test operation",
            async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 0;
            },
            CancellationToken.None);

        (await act.Should().ThrowAsync<TimeoutException>())
            .Which.Message.Should().Contain("test operation");
    }

    [Fact]
    public async Task BoundedProcessExecution_ClientCancellation_RemainsCancellation()
    {
        using var process = new Process();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = async () => await BoundedProcessExecution.RunAsync(
            process,
            TimeSpan.FromSeconds(1),
            "test operation",
            async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 0;
            },
            cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Parser_AcceptsStandardPerfScriptShape()
    {
        var samples = PerfScriptParser.Parse(TwoSamplesFromPid1);

        samples.Should().HaveCount(3);
        samples[0].ProcessId.Should().Be(1);
        samples[0].Frames.Should().HaveCount(3);
        samples[0].Frames[0].Module.Should().Be("/app/NativeAotSample");
        samples[0].Frames[0].Symbol.Should().Be("NativeAotSample::HotPath");
        samples[0].Frames[2].Module.Should().Be("/lib/libc.so.6");
        samples[0].Frames[2].Symbol.Should().Be("__libc_start_main");
    }

    [Theory]
    [MemberData(nameof(CrossOsFilterCases))]
    public void Parser_FiltersByProcessIdWhenRequested(string lineEnding, string pidSpacing)
    {
        var samples = PerfScriptParser.Parse(CreateTwoSamplesFromPid1(lineEnding, pidSpacing), processId: 1);

        samples.Should().HaveCount(2);
        samples.Should().OnlyContain(s => s.ProcessId == 1);
    }

    [Fact]
    public void Aggregate_RanksHotspots_AndProducesCallTree()
    {
        var (total, hotspots, root, _, _) = PerfNativeAotCpuSampler.Aggregate(
            CreateTwoSamplesFromPid1("\r\n", "      "),
            processId: 1,
            topN: 5);

        total.Should().Be(2);
        hotspots.Should().NotBeEmpty();

        // HotPath appears in both samples → highest inclusive count, exclusive = 0
        // (always called from another frame). __libc_start_main is the root of both stacks.
        var hot = hotspots.Single(h => h.Frame.Method.Contains("HotPath", StringComparison.Ordinal));
        hot.InclusiveSamples.Should().Be(2);
        hot.Identity.Should().BeNull("native frames do not carry a managed (mvid, token) handoff");

        // Tree root is synthetic; first real frame is __libc_start_main (the deepest caller
        // in both stacks) because perf prints leaf→root and we reverse for tree traversal.
        root.Children.Should().NotBeEmpty();
        var firstRealFrame = root.Children[0];
        firstRealFrame.Frame.Method.Should().Be("__libc_start_main");
        firstRealFrame.InclusiveSamples.Should().Be(2);
    }


    [Fact]
    public async Task AggregateAsync_MatchesBufferedAggregate()
    {
        var script = CreateTwoSamplesFromPid1("\n", "  ");
        var buffered = PerfNativeAotCpuSampler.Aggregate(script, processId: 1, topN: 5);
        using var reader = new StringReader(script);
        var streamed = await PerfNativeAotCpuSampler.AggregateAsync(reader, processId: 1, topN: 5);

        streamed.Truncated.Should().BeFalse();
        streamed.Total.Should().Be(buffered.Total);
        streamed.Hotspots.Should().BeEquivalentTo(buffered.Hotspots);
        streamed.Root.Should().BeEquivalentTo(buffered.Root);
        streamed.SymbolSource.Should().Be(buffered.SymbolSource);
        streamed.Identities.Should().BeEquivalentTo(buffered.Identities);
    }

    [Fact]
    public async Task AggregateAsync_StopsAtSampleBudget_AndMarksResultTruncated()
    {
        using var reader = new StringReader(CreateTwoSamplesFromPid1("\n", "  "));
        var streamed = await PerfNativeAotCpuSampler.AggregateAsync(reader, processId: 1, topN: 5, sampleBudget: 1);

        streamed.Truncated.Should().BeTrue();
        streamed.Total.Should().Be(1);
        streamed.Hotspots.Should().NotBeEmpty();
    }

    [Fact]
    public void Parser_SkipsCommentLines_AndOrphanFrames()
    {
        const string output = """
            # ========
            # captured on: host
            # ========

                            ffff00000000 OrphanFrame+0x0 (/lib/orphan.so)

            sample-target  1 [001] 12345.0: cpu-clock:
                            ffff11110000 RealFrame+0x0 (/app/RealMod)

            """;

        var samples = PerfScriptParser.Parse(output);
        samples.Should().HaveCount(1);
        samples[0].Frames.Single().Symbol.Should().Be("RealFrame");
    }

    [Fact]
    public void Parser_WithoutProcessIdFilter_KeepsAllSamples()
    {
        // Regression: previously the perf-sampler path passed the target PID to Parse(),
        // which filtered out every sample whose header TID differed from the PID. Now the
        // sampler trusts perf record -p PID and passes processId=0; Parse() must accept
        // every sample regardless of TID.
        const string output = """
            worker-thread  90001 [001] 12345.6: cpu-clock:
                            ffff00000001 worker_a (/lib/libfoo.so)

            gc-thread      90002 [002] 12345.7: cpu-clock:
                            ffff00000002 gc_b (/lib/libfoo.so)

            main           42    [000] 12345.8: cpu-clock:
                            ffff00000003 main_c (/lib/libfoo.so)

            """;

        var samples = PerfScriptParser.Parse(output, processId: 0);
        samples.Should().HaveCount(3);
        samples.Select(s => s.Frames[0].Symbol).Should().BeEquivalentTo(["worker_a", "gc_b", "main_c"]);
    }

    [Fact]
    public void Parser_HandlesFramesWithoutModule()
    {
        const string output = """
            sample-target  7 [000] 1.0: cpu-clock:
                            ffff11110000 [unknown]

            """;

        var samples = PerfScriptParser.Parse(output);
        samples.Should().HaveCount(1);
        samples[0].Frames[0].Symbol.Should().Be("[unknown]");
        samples[0].Frames[0].Module.Should().BeEmpty();
    }

    [Fact]
    public void Parser_PreservesAddressAndMemfdDoublemapperModule()
    {
        const string output = """
            sample-target  7 [000] 1.0: cpu-clock:
                            7ab2e757f784 [unknown] (/memfd:doublemapper (deleted))

            """;

        var samples = PerfScriptParser.Parse(output);

        samples.Should().ContainSingle();
        var frame = samples[0].Frames.Single();
        frame.Address.Should().Be(0x7ab2e757f784);
        frame.Symbol.Should().Be("[unknown]");
        frame.Module.Should().Be("/memfd:doublemapper (deleted)");
    }

    [Fact]
    public void Aggregate_WithJitMap_ResolvesDoublemapperUnknownByAddress()
    {
        const string output = """
            sample-target  7 [000] 1.0: cpu-clock:
                            7ab2e757f784 [unknown] (/memfd:doublemapper (deleted))
                            7f1234560000 __libc_start_main+0x80 (/lib/libc.so.6)

            """;
        var identity = CreateIdentity("MemfdProof.Program", "<<Main>$>g__HotLoop|0_0", token: 0x06000042);
        var jitMap = new JitMapResult(
            "/tmp/perf-7.map",
            [new JitMapRange(0x7ab2e757f760, 0x32, identity, "MemfdProof.Program.<<Main>$>g__HotLoop|0_0")],
            MethodCount: 1);

        var (_, hotspots, root, _, identities) = PerfNativeAotCpuSampler.Aggregate(
            output,
            processId: 0,
            topN: 10,
            jitMap: jitMap);

        var managed = hotspots.Single(h => h.Frame.Method.Contains("HotLoop", StringComparison.Ordinal));
        managed.Frame.Module.Should().Be("MemfdProof.dll");
        managed.Identity.Should().Be(identity);
        identities.Should().ContainKey(new SymbolRef("MemfdProof.dll", "MemfdProof.Program.<<Main>$>g__HotLoop|0_0"));

        var stamped = CallTreeIdentityProjector.Stamp(root, identities);
        stamped.Children.SelectMany(c => c.Children).Should().Contain(n => n.Identity == identity);

        hotspots.Single(h => h.Frame.Method.Contains("libc_start_main", StringComparison.Ordinal))
            .Identity.Should().BeNull("native frames must not inherit managed identity");
    }

    [Fact]
    public void Aggregate_WithJitMap_KeepsSameDisplayOverloadsIdentityDistinct()
    {
        const string output = """
            sample-target  7 [000] 1.0: cpu-clock:
                            1008 [unknown] (/memfd:doublemapper (deleted))

            sample-target  7 [000] 1.1: cpu-clock:
                            2008 [unknown] (/memfd:doublemapper (deleted))

            """;
        var first = CreateIdentity("Overload.Program", "Foo", token: 0x06000051);
        var second = CreateIdentity("Overload.Program", "Foo", token: 0x06000052);
        var jitMap = new JitMapResult(
            "/tmp/perf-7.map",
            [
                new JitMapRange(0x1000, 0x20, first, "Overload.Program.Foo"),
                new JitMapRange(0x2000, 0x20, second, "Overload.Program.Foo"),
            ],
            MethodCount: 2);

        var (_, hotspots, root, _, identities) = PerfNativeAotCpuSampler.Aggregate(
            output,
            processId: 0,
            topN: 10,
            jitMap: jitMap);

        var overloads = hotspots
            .Where(h => h.Frame is { Module: "MemfdProof.dll", Method: "Overload.Program.Foo" })
            .ToList();
        overloads.Should().HaveCount(2);
        overloads.Select(h => h.Identity).Should().BeEquivalentTo([first, second]);

        var stamped = CallTreeIdentityProjector.Stamp(root, identities);
        stamped.Children
            .Where(n => n.Frame is { Module: "MemfdProof.dll", Method: "Overload.Program.Foo" })
            .Select(n => n.Identity)
            .Should()
            .BeEquivalentTo([first, second]);
    }

    [Fact]
    public void Aggregate_WithoutJitMap_KeepsDoublemapperUnknownHonest()
    {
        const string output = """
            sample-target  7 [000] 1.0: cpu-clock:
                            7ab2e757f784 [unknown] (/memfd:doublemapper (deleted))

            """;

        var (_, hotspots, root, _, identities) = PerfNativeAotCpuSampler.Aggregate(output, processId: 0, topN: 10);

        hotspots.Should().ContainSingle();
        hotspots[0].Frame.Module.Should().Be("/memfd:doublemapper (deleted)");
        hotspots[0].Frame.Method.Should().Be("[unknown]");
        hotspots[0].Identity.Should().BeNull();
        identities.Should().BeEmpty();
        root.Children.Single().Frame.Method.Should().Be("[unknown]");
    }

    [Fact]
    public void JitMapResolveFrame_UsesHalfOpenBoundaries()
    {
        var identity = CreateIdentity("Boundary.Program", "HotLoop", token: 0x06000043);
        var jitMap = new JitMapResult(
            "/tmp/perf-42.map",
            [new JitMapRange(0x1000, 0x20, identity, "Boundary.Program.HotLoop")],
            MethodCount: 1);

        jitMap.Resolve(0x0fff).Should().BeNull();
        jitMap.Resolve(0x1000).Should().Be(identity);
        jitMap.Resolve(0x101f).Should().Be(identity);
        jitMap.Resolve(0x1020).Should().BeNull();
    }

    [Fact]
    public void JitMapResolveFrame_DeterministicallyPrefersGreatestStartThenSmallestRange()
    {
        var wide = CreateIdentity("Overlap.Program", "Wide", token: 0x06000044);
        var narrow = CreateIdentity("Overlap.Program", "Narrow", token: 0x06000045);
        var sameStartSmall = CreateIdentity("Overlap.Program", "SameStartSmall", token: 0x06000046);
        var sameStartLarge = CreateIdentity("Overlap.Program", "SameStartLarge", token: 0x06000047);
        var jitMap = new JitMapResult(
            "/tmp/perf-42.map",
            [
                new JitMapRange(0x1000, 0x100, wide, "Overlap.Program.Wide"),
                new JitMapRange(0x1080, 0x10, narrow, "Overlap.Program.Narrow"),
                new JitMapRange(0x1100, 0x80, sameStartLarge, "Overlap.Program.SameStartLarge"),
                new JitMapRange(0x1100, 0x20, sameStartSmall, "Overlap.Program.SameStartSmall"),
            ],
            MethodCount: 4);

        jitMap.Resolve(0x1088).Should().Be(narrow);
        jitMap.Resolve(0x1095).Should().Be(wide);
        jitMap.Resolve(0x1110).Should().Be(sameStartSmall);
        jitMap.Resolve(0x1170).Should().Be(sameStartLarge);
        jitMap.Resolve(0x2000).Should().BeNull();
    }

    [Fact]
    public void Aggregate_ReportsSymbolSource_SoCpuSampleCanCarryIt()
    {
        // Regression for #35: the aggregate SymbolSource computed during NativeAOT
        // aggregation must be propagated all the way to the primary CpuSample record
        // — see PerfNativeAotCpuSampler.SampleAsync. This test just locks in that the
        // value is non-Unknown for a typical mangled-frame trace so consumers don't
        // have to drill into the trace artifact to learn whether demangling ran.
        const string mangledSample = """
            sample-target  1 [000] 1.0: cpu-clock:
                            ffffabcd11110000 S_P_CoreLib_System_Threading_Thread__ThreadEntryPoint (/app/NativeAotSample)
                            ffffabcd11110100 S_P_CoreLib_System_Threading_Thread__StartHelper (/app/NativeAotSample)
                            7f1234560000 __libc_start_main+0x80 (/lib/libc.so.6)

            """;

        var (_, _, _, symbolSource, _) = PerfNativeAotCpuSampler.Aggregate(mangledSample, processId: 0, topN: 5);

        symbolSource.Should().NotBe(NativeAotSymbolDemangler.SymbolSource.Unknown,
            "the aggregator must surface a concrete provenance so CpuSample.SymbolSource is informative");
    }

    // ---- #395: NativeAOT MethodIdentity via the ILC map file ----

    private const string AotMapSample = """
        <?xml version="1.0" encoding="utf-8"?>
        <ObjectNodes>
          <MethodCode Name="NativeAotSample_WeatherForecast__ToString" Length="167" Hash="bb" />
          <MethodCode Name="NativeAotSample_Program___Main__" Length="399" Hash="cc" />
        </ObjectNodes>
        """;

    private const string AotPerfSample = """
        sample-target  1 [000] 1.0: cpu-clock:
                        ffffabcd11110000 NativeAotSample_WeatherForecast__ToString+0x42 (/app/NativeAotSample)
                        ffffabcd11110100 NativeAotSample_Program___Main__+0x10 (/app/NativeAotSample)
                        7f1234560000 __libc_start_main+0x80 (/lib/libc.so.6)

        sample-target  1 [001] 1.1: cpu-clock:
                        ffffabcd11110000 NativeAotSample_WeatherForecast__ToString+0x10 (/app/NativeAotSample)
                        ffffabcd11110100 NativeAotSample_Program___Main__+0x10 (/app/NativeAotSample)
                        7f1234560000 __libc_start_main+0x80 (/lib/libc.so.6)

        """;

    private static NativeAotMethodMap LoadAotMap()
        => NativeAotMethodMap.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(AotMapSample)));

    private static MethodIdentity CreateIdentity(string typeFullName, string methodName, int token)
        => new(
            MethodName: methodName,
            GenericArity: 0,
            ModuleName: "MemfdProof.dll",
            ModulePath: "/app/MemfdProof.dll",
            ModuleVersionId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            MetadataToken: token,
            TypeFullName: typeFullName);

    [Fact]
    public void Aggregate_WithoutMap_EmitsNoMethodIdentities()
    {
        var (_, hotspots, _, _, identities) = PerfNativeAotCpuSampler.Aggregate(AotPerfSample, processId: 0, topN: 10);

        identities.Should().BeEmpty();
        hotspots.Should().OnlyContain(h => h.Identity == null);
    }

    [Fact]
    public void Aggregate_WithMap_EmitsNameBasedIdentityForManagedFramesOnly()
    {
        var (_, hotspots, _, _, identities) = PerfNativeAotCpuSampler.Aggregate(
            AotPerfSample, processId: 0, topN: 10,
            LoadAotMap(), moduleName: "NativeAotSample", modulePath: "/app/NativeAotSample");

        // Exactly the two MethodCode frames get an identity; __libc_start_main does not.
        identities.Should().HaveCount(2);

        var toString = hotspots.Single(h => h.Frame.Method.Contains("ToString", StringComparison.Ordinal));
        toString.Identity.Should().NotBeNull();
        toString.Identity!.TypeFullName.Should().Be("NativeAotSample.WeatherForecast");
        toString.Identity.MethodName.Should().Be("ToString");
        toString.Identity.ModuleName.Should().Be("NativeAotSample");
        toString.Identity.ModulePath.Should().Be("/app/NativeAotSample");

        // The AOT handoff is name-based only — no IL metadata token / MVID exists at runtime.
        toString.Identity.MetadataToken.Should().BeNull();
        toString.Identity.ModuleVersionId.Should().BeNull();

        var libc = hotspots.Single(h => h.Frame.Method.Contains("libc_start_main", StringComparison.Ordinal));
        libc.Identity.Should().BeNull("native frames are not managed method bodies");
    }

    [Fact]
    public void Aggregate_WithMap_IdentitiesAreKeyedSoTheCallTreeCanBeStamped()
    {
        var (_, _, root, _, identities) = PerfNativeAotCpuSampler.Aggregate(
            AotPerfSample, processId: 0, topN: 10,
            LoadAotMap(), moduleName: "NativeAotSample", modulePath: "/app/NativeAotSample");

        var stamped = CallTreeIdentityProjector.Stamp(root, identities);

        // Walk the tree and confirm the managed nodes carry their identity while libc does not.
        var stack = new Stack<CallTreeNode>();
        stack.Push(stamped);
        var stampedManaged = 0;
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.Frame.Method.Contains("ToString", StringComparison.Ordinal) ||
                node.Frame.Method.Contains("Main", StringComparison.Ordinal))
            {
                node.Identity.Should().NotBeNull();
                stampedManaged++;
            }
            else if (node.Frame.Method.Contains("libc_start_main", StringComparison.Ordinal))
            {
                node.Identity.Should().BeNull();
            }

            foreach (var child in node.Children)
            {
                stack.Push(child);
            }
        }

        stampedManaged.Should().Be(2);
    }

    private static string CreateTwoSamplesFromPid1(string lineEnding, string pidSpacing)
        => string.Join(lineEnding,
        [
            $"sample-target{pidSpacing}1 [001] 12345.678901: cpu-clock:",
            "                ffffabcd12340000 NativeAotSample::HotPath+0x42 (/app/NativeAotSample)",
            "                ffffabcd12340100 NativeAotSample::Main+0x10 (/app/NativeAotSample)",
            "                7f1234560000 __libc_start_main+0x80 (/lib/libc.so.6)",
            string.Empty,
            $"sample-target{pidSpacing}1 [002] 12345.679001: cpu-clock:",
            "                ffffabcd12340000 NativeAotSample::HotPath+0x42 (/app/NativeAotSample)",
            "                ffffabcd12340200 NativeAotSample::ColdPath+0x10 (/app/NativeAotSample)",
            "                7f1234560000 __libc_start_main+0x80 (/lib/libc.so.6)",
            string.Empty,
            $"other-proc{pidSpacing}2 [000] 12345.679500: cpu-clock:",
            "                ffffaaaa00000000 NoiseFunction+0x0 (/usr/lib/libfoo.so)",
            string.Empty,
        ]);
}
