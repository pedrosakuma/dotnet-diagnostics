using DotnetDiagnostics.BenchmarkDotNet;
using DotnetDiagnostics.Core.CpuSampling;
using FluentAssertions;

namespace DotnetDiagnostics.BenchmarkDotNet.Tests;

public class DotnetDiagnosticsReportExporterTests
{
    [Fact]
    public void EmptyEntries_RenderPlaceholder()
    {
        var md = DotnetDiagnosticsReportExporter.BuildMarkdown(Array.Empty<BenchmarkDiagnosticEntry>());

        md.Should().Contain("biggest offenders");
        md.Should().Contain("No diagnostic captures were recorded");
    }

    [Fact]
    public void GroupsByBenchmark_AndRendersRows()
    {
        var entries = new[]
        {
            new BenchmarkDiagnosticEntry("Workload.AllocateLots", "gc", false, "12 GCs, 3 gen2", "12 GCs, 3 gen2", "/x/a.gc.json"),
            new BenchmarkDiagnosticEntry("Workload.AllocateLots", "counters", false, "cpu 40%", "cpu 40%", "/x/a.counters.json"),
            new BenchmarkDiagnosticEntry("Workload.LockStorm", "contention", true, "collect failed", "PermissionDenied: ptrace blocked", "/x/b.contention.json"),
        };

        var md = DotnetDiagnosticsReportExporter.BuildMarkdown(entries);

        md.Should().Contain("## Workload.AllocateLots");
        md.Should().Contain("## Workload.LockStorm");
        md.Should().Contain("| gc | ok | 12 GCs, 3 gen2 | `a.gc.json` |");
        md.Should().Contain("⚠ error");
        md.Should().Contain("`b.contention.json`");
    }

    [Fact]
    public void Headline_PipeCharactersAreEscaped()
    {
        var entries = new[]
        {
            new BenchmarkDiagnosticEntry("B", "gc", false, "s", "a | b | c", "/x/b.gc.json"),
        };

        var md = DotnetDiagnosticsReportExporter.BuildMarkdown(entries);

        md.Should().Contain("a \\| b \\| c");
    }

    [Fact]
    public void Digest_CpuAndAllocationBothPresent_RendersCorrelatedSection()
    {
        // Issue #827: when a benchmark carries both [DiagnosticKind("cpu")] and
        // [DiagnosticKind("allocation")], the exported report should include the same
        // cross-collector correlation the MCP collect_batch tool computes (issue #825), reusing
        // InvestigationDigestBuilder rather than reimplementing it.
        var entries = new[]
        {
            new BenchmarkDiagnosticEntry("Workload.Combined", "cpu", false, "cpu ok", "cpu ok", "/x/c.cpu.json"),
            new BenchmarkDiagnosticEntry("Workload.Combined", "allocation", false, "alloc ok", "alloc ok", "/x/c.allocation.json"),
        };
        var digest = new InvestigationDigest(
            TopCpuSelfTime:
            [
                new MethodSampleStat("MyApp.Worker.Crunch", "MyApp.dll", "MyApp", 300, 300, 75d, 75d, Identity: null),
            ],
            TopCpuWaitCategories: null,
            HotPathLeaf: new HotPathFrame("MyApp.Worker.Crunch", "MyApp.dll", 300, 300, 75d, 100d, Identity: null),
            HotPathDepth: 2,
            TopAllocationTypes:
            [
                new AllocatedType("MyApp.Widget", 3000, 6, HeapKind.Small),
            ],
            TopAllocationCallsites:
            [
                new AllocationSite(new SampledFrame("MyApp.dll", "MyApp.Worker.Allocate"), 3000, 6, HeapKind.Small),
            ]);
        var digests = new Dictionary<string, InvestigationDigest>(StringComparer.Ordinal)
        {
            ["Workload.Combined"] = digest,
        };

        var md = DotnetDiagnosticsReportExporter.BuildMarkdown(entries, digests);

        md.Should().Contain("### Cross-collector investigation digest (cpu + allocation)");
        md.Should().Contain("MyApp.Worker.Crunch");
        md.Should().Contain("MyApp.Widget");
        md.Should().Contain("MyApp.Worker.Allocate");
    }

    [Fact]
    public void Digest_Absent_OmitsCorrelatedSection()
    {
        var entries = new[]
        {
            new BenchmarkDiagnosticEntry("Workload.CpuOnly", "cpu", false, "cpu ok", "cpu ok", "/x/c.cpu.json"),
        };

        var md = DotnetDiagnosticsReportExporter.BuildMarkdown(entries);

        md.Should().NotContain("Cross-collector investigation digest");
    }
}
