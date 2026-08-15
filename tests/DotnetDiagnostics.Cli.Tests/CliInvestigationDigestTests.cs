using DotnetDiagnostics.Cli;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

/// <summary>
/// Coverage for the session-scoped cross-collector "investigation digest" (issue #827): reuses the
/// host-neutral <see cref="InvestigationDigestBuilder"/> the MCP <c>collect_batch</c> tool calls
/// (issue #825) to print the same correlation once both a <c>cpu-sample</c> and an
/// <c>allocation-sample</c> handle exist for the session's target pid — without a new multi-kind
/// <c>collect</c> verb.
/// </summary>
public sealed class CliInvestigationDigestTests
{
    private const int Pid = 4242;

    [Fact]
    public void TryBuild_BothHandlesPresent_ReturnsDigest()
    {
        var store = new MemoryDiagnosticHandleStore();
        store.Register(Pid, "cpu-sample", CpuTrace(), TimeSpan.FromMinutes(10));
        store.Register(Pid, "allocation-sample", AllocationArtifact(), TimeSpan.FromMinutes(10));

        var digest = CliInvestigationDigestFormatter.TryBuild(store, Pid);

        digest.Should().NotBeNull();
        digest!.TopCpuSelfTime.Should().NotBeNullOrEmpty();
        digest.TopAllocationTypes.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void TryBuild_OnlyCpuHandlePresent_ReturnsNull()
    {
        var store = new MemoryDiagnosticHandleStore();
        store.Register(Pid, "cpu-sample", CpuTrace(), TimeSpan.FromMinutes(10));

        CliInvestigationDigestFormatter.TryBuild(store, Pid).Should().BeNull(
            "the digest is only worth surfacing once it correlates both collectors");
    }

    [Fact]
    public void TryBuild_OnlyAllocationHandlePresent_ReturnsNull()
    {
        var store = new MemoryDiagnosticHandleStore();
        store.Register(Pid, "allocation-sample", AllocationArtifact(), TimeSpan.FromMinutes(10));

        CliInvestigationDigestFormatter.TryBuild(store, Pid).Should().BeNull();
    }

    [Fact]
    public void TryBuild_NeitherHandlePresent_ReturnsNull()
    {
        var store = new MemoryDiagnosticHandleStore();

        CliInvestigationDigestFormatter.TryBuild(store, Pid).Should().BeNull();
    }

    [Fact]
    public void TryBuild_HandlesFromDifferentPid_ReturnsNull()
    {
        var store = new MemoryDiagnosticHandleStore();
        store.Register(Pid, "cpu-sample", CpuTrace(), TimeSpan.FromMinutes(10));
        store.Register(Pid + 1, "allocation-sample", AllocationArtifact(), TimeSpan.FromMinutes(10));

        CliInvestigationDigestFormatter.TryBuild(store, Pid).Should().BeNull();
    }

    [Fact]
    public void Render_IncludesCpuAndAllocationSections()
    {
        var digest = InvestigationDigestBuilder.Build(CpuTrace(), AllocationArtifact().Summary);

        var lines = CliInvestigationDigestFormatter.Render(digest!);

        lines.Should().Contain(l => l.Contains("investigation digest", StringComparison.Ordinal));
        lines.Should().Contain(l => l.Contains("top cpu self-time", StringComparison.Ordinal));
        lines.Should().Contain(l => l.Contains("top allocation types", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SessionRepl_TryPrintInvestigationDigestAsync_BothPresent_PrintsDigest()
    {
        var store = new MemoryDiagnosticHandleStore();
        store.Register(Pid, "cpu-sample", CpuTrace(), TimeSpan.FromMinutes(10));
        var allocationHandle = store.Register(Pid, "allocation-sample", AllocationArtifact(), TimeSpan.FromMinutes(10));
        var stdout = new StringWriter();

        await SessionRepl.TryPrintInvestigationDigestAsync(store, allocationHandle.Id, Pid, stdout);

        stdout.ToString().Should().Contain("investigation digest");
    }

    [Fact]
    public async Task SessionRepl_TryPrintInvestigationDigestAsync_OnlyOneKindPresent_PrintsNothing()
    {
        var store = new MemoryDiagnosticHandleStore();
        var cpuHandle = store.Register(Pid, "cpu-sample", CpuTrace(), TimeSpan.FromMinutes(10));
        var stdout = new StringWriter();

        await SessionRepl.TryPrintInvestigationDigestAsync(store, cpuHandle.Id, Pid, stdout);

        stdout.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task SessionRepl_TryPrintInvestigationDigestAsync_UnrelatedHandleKind_PrintsNothing()
    {
        var store = new MemoryDiagnosticHandleStore();
        store.Register(Pid, "cpu-sample", CpuTrace(), TimeSpan.FromMinutes(10));
        store.Register(Pid, "allocation-sample", AllocationArtifact(), TimeSpan.FromMinutes(10));
        var unrelatedHandle = store.Register(Pid, "process-dump", new object(), TimeSpan.FromMinutes(10));
        var stdout = new StringWriter();

        await SessionRepl.TryPrintInvestigationDigestAsync(store, unrelatedHandle.Id, Pid, stdout);

        stdout.ToString().Should().BeEmpty();
    }

    private static CpuSampleTraceArtifact CpuTrace()
    {
        var leafA = new CallTreeNode(new SampledFrame("App.dll", "LeafA"), 40, 40, Array.Empty<CallTreeNode>());
        var leafB = new CallTreeNode(new SampledFrame("App.dll", "LeafB"), 60, 60, Array.Empty<CallTreeNode>());
        var root = new CallTreeNode(new SampledFrame("App.dll", "Root"), 100, 0, new[] { leafA, leafB });
        return new CpuSampleTraceArtifact(Pid, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5), 100, root);
    }

    private static AllocationSampleArtifact AllocationArtifact()
    {
        var summary = new AllocationSample(
            Pid,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(5),
            TotalEvents: 10,
            TotalBytes: 4096,
            TopByBytes: [new AllocatedType("MyApp.Widget", 3000, 6, HeapKind.Small)],
            TopByCount: [new AllocatedType("MyApp.Widget", 3000, 6, HeapKind.Small)])
        {
            TopBySite = [new AllocationSite(new SampledFrame("MyApp.dll", "MyApp.Worker.Allocate"), 3000, 6, HeapKind.Small)],
        };
        return new AllocationSampleArtifact(summary, CpuTrace());
    }
}
