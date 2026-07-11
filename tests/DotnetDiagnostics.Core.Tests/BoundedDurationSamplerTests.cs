using DotnetDiagnostics.Core.Internal;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class BoundedDurationSamplerTests
{
    [Fact]
    public void GetPercentile_RemainsExact_BelowCapacity()
    {
        var sampler = new BoundedDurationSampler();
        foreach (var value in Enumerable.Range(1, 100))
        {
            sampler.Add(TimeSpan.FromMilliseconds(value));
        }

        sampler.Count.Should().Be(100);
        sampler.IsApproximate.Should().BeFalse();
        sampler.GetPercentile(0.50).Should().Be(TimeSpan.FromMilliseconds(50));
        sampler.GetPercentile(0.95).Should().Be(TimeSpan.FromMilliseconds(95));
        sampler.Max.Should().Be(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public void Max_RemainsExact_AfterReservoirSamplingStarts()
    {
        var sampler = new BoundedDurationSampler();
        for (var i = 0; i < BoundedPercentileSampler.ExactSampleCapacity; i++)
        {
            sampler.Add(TimeSpan.FromMilliseconds(7));
        }

        sampler.Add(TimeSpan.FromMilliseconds(999));

        sampler.IsApproximate.Should().BeTrue();
        sampler.Max.Should().Be(TimeSpan.FromMilliseconds(999));
        sampler.GetPercentile(0.95).Should().Be(TimeSpan.FromMilliseconds(7));
    }
}
