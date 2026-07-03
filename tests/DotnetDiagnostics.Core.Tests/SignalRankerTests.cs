using DotnetDiagnostics.Core.Signals;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class SignalRankerTests
{
    private static SignalGroup Make(string signal, double salience)
        => new(signal, signal, salience, Array.Empty<SignalBucket>());

    [Fact]
    public void Rank_OrdersBySalienceDescending()
    {
        var signals = new[]
        {
            Make("low", 0.2),
            Make("high", 0.9),
            Make("mid", 0.5),
        };

        var ranked = SignalRanker.Rank(signals);

        ranked.Select(s => s.Signal).Should().ContainInOrder("high", "mid", "low");
    }

    [Fact]
    public void Rank_CapsToMax()
    {
        var signals = Enumerable.Range(0, 10)
            .Select(i => Make($"s{i}", 1.0 - (i * 0.01)))
            .ToArray();

        SignalRanker.Rank(signals, max: 3).Should().HaveCount(3);
    }

    [Fact]
    public void Rank_ReturnsEmpty_WhenMaxIsNonPositive()
    {
        SignalRanker.Rank(new[] { Make("s", 0.5) }, max: 0).Should().BeEmpty();
    }

    [Fact]
    public void Rank_ReturnsEmpty_ForEmptyInput()
    {
        SignalRanker.Rank(Array.Empty<SignalGroup>()).Should().BeEmpty();
    }
}
