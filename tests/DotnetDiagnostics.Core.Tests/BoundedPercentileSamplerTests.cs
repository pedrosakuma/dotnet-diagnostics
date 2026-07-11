using DotnetDiagnostics.Core.Internal;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class BoundedPercentileSamplerTests
{
    [Fact]
    public void GetPercentile95_RemainsExact_BelowCapacity()
    {
        var sampler = new BoundedPercentileSampler();
        foreach (var value in Enumerable.Range(1, 100))
        {
            sampler.Add(value);
        }

        sampler.Count.Should().Be(100);
        sampler.IsApproximate.Should().BeFalse();
        sampler.GetPercentile95().Should().Be(95);
    }

    [Fact]
    public void GetPercentile95_SwitchesToBoundedReservoir_AfterCapacity()
    {
        var sampler = new BoundedPercentileSampler();
        for (var i = 0; i < BoundedPercentileSampler.ExactSampleCapacity + 100; i++)
        {
            sampler.Add(7);
        }

        sampler.Count.Should().Be(BoundedPercentileSampler.ExactSampleCapacity + 100);
        sampler.IsApproximate.Should().BeTrue();
        sampler.GetPercentile95().Should().Be(7);
    }
}
