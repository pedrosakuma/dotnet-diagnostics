using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

public sealed class RegexBacktrackingHintTests
{
    [Fact]
    public void TryBuildRegexBacktrackingHint_EmitsHint_WhenRegexEngineIsHot()
    {
        var sample = SampleWithTopFrame(
            "System.Text.RegularExpressions.RegexRunner.Scan(System.Text.RegularExpressions.Regex, System.ReadOnlySpan`1[[System.Char]])");

        var hint = DiagnosticTools.TryBuildRegexBacktrackingHint(sample, "handle-123");

        hint.Should().NotBeNull();
        hint!.NextTool.Should().Be("query_snapshot");
        hint.Reason.Should().Contain("GeneratedRegex");
        hint.SuggestedArguments.Should().ContainKey("handle").WhoseValue.Should().Be("handle-123");
        hint.SuggestedArguments.Should().ContainKey("view").WhoseValue.Should().Be("call-tree");
    }

    [Fact]
    public void TryBuildRegexBacktrackingHint_EmitsHint_ForInterpreter()
    {
        var sample = SampleWithTopFrame(
            "System.Text.RegularExpressions.RegexInterpreter.Go()");

        DiagnosticTools.TryBuildRegexBacktrackingHint(sample, "h").Should().NotBeNull();
    }

    [Fact]
    public void TryBuildRegexBacktrackingHint_ReturnsNull_WhenNoRegexFrame()
    {
        var sample = SampleWithTopFrame("MyApp.Services.OrderService.Process()");

        DiagnosticTools.TryBuildRegexBacktrackingHint(sample, "h").Should().BeNull();
    }

    [Fact]
    public void TryBuildRegexBacktrackingHint_ReturnsNull_WhenRegexFrameIsColdInTopN()
    {
        // A low-share incidental regex frame (e.g. 5 of 1000 inclusive samples) must NOT fire the
        // catastrophic-backtracking hint — only a genuinely hot regex profile should.
        var sample = new CpuSample(
            ProcessId: 4242,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(10),
            TotalSamples: 1000,
            TopHotspots: new[]
            {
                new Hotspot(new SampledFrame("MyApp.dll", "MyApp.Services.OrderService.Process()"), 900, 900),
                new Hotspot(new SampledFrame("System.Private.CoreLib.dll", "System.Text.RegularExpressions.RegexRunner.Scan()"), 5, 5),
            });

        DiagnosticTools.TryBuildRegexBacktrackingHint(sample, "h").Should().BeNull();
    }

    private static CpuSample SampleWithTopFrame(string method)
        => new(
            ProcessId: 4242,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(10),
            TotalSamples: 100,
            TopHotspots: new[]
            {
                new Hotspot(new SampledFrame("System.Private.CoreLib.dll", method), 80, 80),
            });
}
