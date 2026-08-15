using DotnetDiagnostics.Core.CpuEfficiency;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Drives <see cref="PerfStatOutputParser"/> against representative <c>perf stat -x,</c> CSV
/// fixtures, including the graceful-degradation "&lt;not supported&gt;"/"&lt;not counted&gt;" cases
/// documented in issue #828 (common on cloud VMs / CI runners with no vPMU exposed to the guest).
/// </summary>
public sealed class PerfStatOutputParserTests
{
    [Fact]
    public void ParsesAllSupportedEvents()
    {
        const string csv = """
            1234567,,cycles,1000000000,100.00,,
            987654,,instructions,1000000000,100.00,0.80,insn per cycle
            50000,,cache-references,1000000000,100.00,,
            1200,,cache-misses,1000000000,100.00,2.40,of all cache refs
            20000,,branch-instructions,1000000000,100.00,,
            300,,branch-misses,1000000000,100.00,1.50,of all branches
            400,,stalled-cycles-frontend,1000000000,100.00,,
            600,,stalled-cycles-backend,1000000000,100.00,,
            5,,dTLB-load-misses,1000000000,100.00,,
            2,,iTLB-load-misses,1000000000,100.00,,
            10,,page-faults,1000000000,100.00,,
            3,,context-switches,1000000000,100.00,,
            1,,cpu-migrations,1000000000,100.00,,
            """;

        var result = PerfStatOutputParser.Parse(csv);

        result.UnavailableEvents.Should().BeEmpty();
        result.Values.Should().Contain(new KeyValuePair<string, long>("cycles", 1234567));
        result.Values.Should().Contain(new KeyValuePair<string, long>("instructions", 987654));
        result.Values.Should().Contain(new KeyValuePair<string, long>("cache-references", 50000));
        result.Values.Should().Contain(new KeyValuePair<string, long>("cache-misses", 1200));
        result.Values.Should().Contain(new KeyValuePair<string, long>("branch-instructions", 20000));
        result.Values.Should().Contain(new KeyValuePair<string, long>("branch-misses", 300));
        result.Values.Should().Contain(new KeyValuePair<string, long>("stalled-cycles-frontend", 400));
        result.Values.Should().Contain(new KeyValuePair<string, long>("stalled-cycles-backend", 600));
        result.Values.Should().Contain(new KeyValuePair<string, long>("dTLB-load-misses", 5));
        result.Values.Should().Contain(new KeyValuePair<string, long>("iTLB-load-misses", 2));
        result.Values.Should().Contain(new KeyValuePair<string, long>("page-faults", 10));
        result.Values.Should().Contain(new KeyValuePair<string, long>("context-switches", 3));
        result.Values.Should().Contain(new KeyValuePair<string, long>("cpu-migrations", 1));
    }

    [Fact]
    public void NotSupportedToken_SurfacesAsUnavailableNote_NotAValue()
    {
        // vPMU-less host (e.g. a virtualized CI runner): the kernel doesn't expose this generic
        // event alias for the current (possibly virtual) CPU at all.
        const string csv = """
            <not supported>,,stalled-cycles-frontend,1000000000,100.00,,
            1234,,cycles,1000000000,100.00,,
            """;

        var result = PerfStatOutputParser.Parse(csv);

        result.Values.Should().NotContainKey("stalled-cycles-frontend");
        result.Values.Should().ContainKey("cycles");
        result.UnavailableEvents.Should().ContainSingle(n => n.Contains("stalled-cycles-frontend", StringComparison.Ordinal)
            && n.Contains("not supported", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NotCountedToken_SurfacesAsUnavailableNote_NotAValue()
    {
        // Event exists on this CPU but couldn't be scheduled into a hardware counter slot
        // during the window (e.g. too many simultaneous events requested).
        const string csv = "<not counted>,,branch-misses,1000000000,0.00,,\n";

        var result = PerfStatOutputParser.Parse(csv);

        result.Values.Should().BeEmpty();
        result.UnavailableEvents.Should().ContainSingle();
    }

    [Fact]
    public void MalformedValue_SurfacesAsUnavailableNote_DoesNotThrow()
    {
        const string csv = "not-a-number,,cycles,1000000000,100.00,,";

        var act = () => PerfStatOutputParser.Parse(csv);

        act.Should().NotThrow();
        var result = PerfStatOutputParser.Parse(csv);
        result.Values.Should().BeEmpty();
        result.UnavailableEvents.Should().ContainSingle(n => n.Contains("could not parse", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BlankLinesAndTrailerLines_AreIgnored()
    {
        const string csv = """
            1234567,,cycles,1000000000,100.00,,

               2.001234567 seconds time elapsed

            """;

        var result = PerfStatOutputParser.Parse(csv);

        result.Values.Should().ContainKey("cycles");
        result.UnavailableEvents.Should().BeEmpty();
    }

    [Fact]
    public void EmptyInput_ReturnsEmptyResult()
    {
        var result = PerfStatOutputParser.Parse(string.Empty);

        result.Values.Should().BeEmpty();
        result.UnavailableEvents.Should().BeEmpty();
    }

    [Fact]
    public void NullInput_Throws()
    {
        var act = () => PerfStatOutputParser.Parse(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
