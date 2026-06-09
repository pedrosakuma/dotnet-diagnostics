using DotnetDiagnostics.BenchmarkDotNet;
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
}
