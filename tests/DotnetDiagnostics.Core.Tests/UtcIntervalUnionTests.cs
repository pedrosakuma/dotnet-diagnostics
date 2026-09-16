using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.DistributedTrace;
using DotnetDiagnostics.Core.Security;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class UtcIntervalUnionTests
{
    private const string Trace = "abcdef0123456789abcdef0123456789";
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    [Fact]
    public void GeneratedIntervals_MatchIndependentDiscreteGridOracleInBothProjectors()
    {
        var random = new Random(949);
        for (var test = 0; test < 200; test++)
        {
            var children = Enumerable.Range(0, random.Next(0, 7))
                .Select(_ => (Start: random.Next(-5, 16), Length: random.Next(0, 12))).ToArray();
            // Independent oracle: each unit cell is covered if ANY child contains its midpoint.
            var coveredCells = Enumerable.Range(0, 10).Count(cell =>
                children.Any(child => child.Start <= cell && child.Start + child.Length > cell));
            foreach (var translation in new[] { TimeSpan.Zero, TimeSpan.FromDays(500) })
            {
                var start = Start + translation;
                var spans = new List<CapturedActivity> { Span(1, null, start, 10) };
                spans.AddRange(children.Select((child, i) =>
                    Span(i + 2, "0000000000000001",
                        start.AddMilliseconds(child.Start).ToOffset(TimeSpan.FromHours(i % 3 - 1)), child.Length)));
                var capture = new ActivityCapture(1, null, start, TimeSpan.FromSeconds(1), spans.Count, spans.Count, spans, [], []);
                var distributed = DistributedTraceStitcher.Stitch(Trace, [("pod", capture)]);
                var local = ActivityTraceProjector.Project(capture, Trace, 20, new SensitiveDataRedactor());
                distributed.Spans.Single(s => s.SpanId == "0000000000000001").SelfDurationMs.Should().Be(10 - coveredCells);
                local.Spans.Single(s => s.SpanId == "0000000000000001").ResidualDurationMs.Should().Be(10 - coveredCells);
            }
        }
    }

    [Theory]
    [InlineData(0, 3, 6, 9, 6)] // disjoint
    [InlineData(0, 8, 2, 10, 10)] // overlap
    [InlineData(2, 8, 2, 8, 6)] // identical
    [InlineData(1, 9, 3, 6, 8)] // nested
    [InlineData(0, 5, 5, 10, 10)] // adjacent
    [InlineData(2, 2, 5, 5, 0)] // zero
    [InlineData(-2, 4, 8, 15, 6)] // clipped
    [InlineData(-5, -1, 11, 15, 0)] // wholly outside
    [InlineData(5, 2, 0, 0, 0)] // invalid
    public void ExplicitIntervalMatrix(int a, int b, int c, int d, int expected)
    {
        var result = UtcIntervalUnion.Measure(Start, Start.AddMilliseconds(10),
            [(Start.AddMilliseconds(a), Start.AddMilliseconds(b)), (Start.AddMilliseconds(c), Start.AddMilliseconds(d))]);
        result.CoveredTicks.Should().Be(expected * TimeSpan.TicksPerMillisecond);
    }

    private static CapturedActivity Span(int id, string? parent, DateTimeOffset start, int duration)
        => new("test", id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            id.ToString("x16", System.Globalization.CultureInfo.InvariantCulture), parent,
            Trace, id.ToString("x16", System.Globalization.CultureInfo.InvariantCulture), parent, start, start.AddMilliseconds(duration),
            TimeSpan.FromMilliseconds(duration), new Dictionary<string, string>());
}
