using DotnetDiagnostics.Core.Counters;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class HeadlineCountersTests
{
    [Fact]
    public void FilterCounters_IncludesActiveTimerCount()
    {
        var all = new[]
        {
            Counter("System.Runtime", "active-timer-count", 7),
            Counter("System.Runtime", "cpu-usage", 12),
            Counter("System.Runtime", "some-noise-counter", 1),
        };

        var headline = HeadlineCounters.FilterCounters(all);

        headline.Select(c => c.Name).Should().Contain("active-timer-count");
        headline.Select(c => c.Name).Should().NotContain("some-noise-counter");
    }

    private static CounterValue Counter(string provider, string name, double value)
        => new(provider, name, name, value, CounterKind.Mean);
}
