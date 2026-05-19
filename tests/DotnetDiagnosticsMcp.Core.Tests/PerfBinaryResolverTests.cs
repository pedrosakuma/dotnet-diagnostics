using DotnetDiagnosticsMcp.Core.CpuSampling;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Core.Tests;

public sealed class PerfBinaryResolverTests
{
    [Fact]
    public void Resolve_ReturnsConfiguredPath_WhenProbeSucceeds()
    {
        var probedPaths = new List<string>();
        var resolved = PerfBinaryResolver.Resolve(
            "perf",
            enumerateCandidates: static () => new[] { "/usr/lib/linux-tools-6.8.0-111/perf" },
            probe: p => { probedPaths.Add(p); return p == "perf"; });

        resolved.Should().Be("perf");
        probedPaths.Should().Equal("perf");
    }

    [Fact]
    public void Resolve_FallsBackToLinuxToolsCandidate_WhenConfiguredFails()
    {
        var candidate = "/usr/lib/linux-tools-6.8.0-111/perf";
        var probedPaths = new List<string>();

        var resolved = PerfBinaryResolver.Resolve(
            "perf",
            enumerateCandidates: () => new[] { candidate },
            probe: p => { probedPaths.Add(p); return p == candidate; });

        resolved.Should().Be(candidate);
        probedPaths.Should().Equal("perf", candidate);
    }

    [Fact]
    public void Resolve_TriesAllCandidates_AndReturnsNullWhenNoneWork()
    {
        var resolved = PerfBinaryResolver.Resolve(
            "perf",
            enumerateCandidates: static () => new[]
            {
                "/usr/lib/linux-tools-6.8.0-111/perf",
                "/usr/lib/linux-tools-5.15.0-1/perf",
            },
            probe: static _ => false);

        resolved.Should().BeNull();
    }

    [Fact]
    public void Resolve_SkipsCandidateEqualToConfiguredPath()
    {
        var probedPaths = new List<string>();
        var resolved = PerfBinaryResolver.Resolve(
            "/usr/bin/perf",
            enumerateCandidates: static () => new[] { "/usr/bin/perf", "/usr/lib/linux-tools-6/perf" },
            probe: p => { probedPaths.Add(p); return p == "/usr/lib/linux-tools-6/perf"; });

        resolved.Should().Be("/usr/lib/linux-tools-6/perf");
        probedPaths.Should().Equal("/usr/bin/perf", "/usr/lib/linux-tools-6/perf");
    }

    [Fact]
    public void ProbePerfVersion_ReturnsFalse_ForNonexistentPath()
    {
        PerfBinaryResolver.ProbePerfVersion("/does/not/exist/perf-xyz-12345")
            .Should().BeFalse();
    }

    [Fact]
    public void ProbePerfVersion_ReturnsFalse_ForBinaryThatDoesNotPrintPerfVersion()
    {
        PerfBinaryResolver.ProbePerfVersion("/bin/true").Should().BeFalse();
    }

    [Fact]
    public void CompareKernelVersionDescending_OrdersByNumericComponentsNotOrdinalString()
    {
        // Ordinal: "linux-tools-6.8..." sorts BEFORE "linux-tools-6.11..." because '8' > '1'.
        // We need the opposite: 6.11 is the newer kernel and must come first.
        var arr = new[]
        {
            "linux-tools-6.8.0-60-generic",
            "linux-tools-6.11.0-1-generic",
            "linux-tools-5.15.0-100-generic",
        };
        Array.Sort(arr, PerfBinaryResolver.CompareKernelVersionDescending);
        arr.Should().Equal(
            "linux-tools-6.11.0-1-generic",
            "linux-tools-6.8.0-60-generic",
            "linux-tools-5.15.0-100-generic");
    }
}
