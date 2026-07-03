using DotnetDiagnostics.Core.Findings;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class FindingRankerTests
{
    private static Finding Make(string pattern, FindingSeverity severity, double confidence)
        => new(pattern, severity, confidence, pattern, Array.Empty<FindingEvidence>());

    [Fact]
    public void Rank_OrdersBySeverityThenConfidenceDescending()
    {
        var findings = new[]
        {
            Make("low-high-conf", FindingSeverity.Low, 0.9),
            Make("critical", FindingSeverity.Critical, 0.1),
            Make("high-low-conf", FindingSeverity.High, 0.2),
            Make("high-high-conf", FindingSeverity.High, 0.8),
        };

        var ranked = FindingRanker.Rank(findings);

        ranked.Select(f => f.Pattern).Should().ContainInOrder(
            "critical", "high-high-conf", "high-low-conf", "low-high-conf");
    }

    [Fact]
    public void Rank_CapsToMax()
    {
        var findings = Enumerable.Range(0, 10)
            .Select(i => Make($"f{i}", FindingSeverity.Medium, 1.0 - (i * 0.01)))
            .ToArray();

        FindingRanker.Rank(findings, max: 3).Should().HaveCount(3);
    }

    [Fact]
    public void Rank_ReturnsEmpty_WhenMaxIsNonPositive()
    {
        var findings = new[] { Make("f", FindingSeverity.High, 0.5) };

        FindingRanker.Rank(findings, max: 0).Should().BeEmpty();
    }

    [Fact]
    public void Rank_ReturnsEmpty_ForEmptyInput()
    {
        FindingRanker.Rank(Array.Empty<Finding>()).Should().BeEmpty();
    }
}
