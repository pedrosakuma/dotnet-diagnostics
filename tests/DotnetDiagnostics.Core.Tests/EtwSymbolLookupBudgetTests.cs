using DotnetDiagnostics.Core.CpuSampling;
using FluentAssertions;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace DotnetDiagnostics.Core.Tests;

public sealed class EtwSymbolLookupBudgetTests
{
    [Fact]
    public void GetNextServerTimeout_IsCappedByServerTimeoutThenByRemainingBudgetShare_WithOneSecondFloor()
    {
        var clock = new ManualTimeProvider();
        var budget = new EtwSymbolLookupBudget(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), clock);

        budget.GetNextServerTimeout().Should().Be(TimeSpan.FromSeconds(7.5));
        clock.Advance(TimeSpan.FromSeconds(20));
        budget.GetNextServerTimeout().Should().Be(TimeSpan.FromSeconds(2.5));
        var capped = new EtwSymbolLookupBudget(TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(60), clock);
        capped.GetNextServerTimeout().Should().Be(TimeSpan.FromSeconds(60));
        clock.Advance(TimeSpan.FromSeconds(9.5));
        budget.IsExhausted.Should().BeFalse();
        budget.GetNextServerTimeout().Should().Be(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(0.5));
        budget.IsExhausted.Should().BeTrue("the half-open budget ends exactly at its total");
    }

    [Fact]
    public void OpenCore_StopsServerRequestsOnceBudgetIsSpent_AndReportsUnresolvedModulesExplicitly()
    {
        var clock = new ManualTimeProvider();
        var budget = new EtwSymbolLookupBudget(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), clock);
        var serverTimeouts = new List<TimeSpan>();
        var cacheOnlyEntries = 0;
        var lookups = new List<(int Module, bool CacheOnly)>();

        // Every module costs 20 virtual seconds when the "server" is consulted and is a miss.
        var pool = EtwPdbSymbolResolverPool.OpenCore(
            new[] { 1, 2, 3, 4, 2 },
            module => (ModuleFileIndex)module,
            budget,
            serverTimeouts.Add,
            () => cacheOnlyEntries++,
            (int module, out EtwPdbSymbolResolver? resolver, out NativeSymbolResolverOpenStatus status) =>
            {
                lookups.Add((module, cacheOnlyEntries > 0));
                if (cacheOnlyEntries == 0)
                {
                    clock.Advance(TimeSpan.FromSeconds(20));
                }

                resolver = null;
                status = NativeSymbolResolverOpenStatus.MatchingPdbUnavailable;
                return false;
            });

        serverTimeouts.Should().Equal(TimeSpan.FromSeconds(7.5), TimeSpan.FromSeconds(2.5));
        cacheOnlyEntries.Should().Be(1, "cache-only mode is entered once");
        // The duplicate module 2 is not looked up again.
        lookups.Should().Equal((1, false), (2, false), (3, true), (4, true));
        pool.OpenStatusCounts[NativeSymbolResolverOpenStatus.MatchingPdbUnavailable].Should().Be(2);
        pool.OpenStatusCounts[NativeSymbolResolverOpenStatus.SymbolLookupBudgetExceeded].Should().Be(2);
        pool.LookupBudget.Should().Be(TimeSpan.FromSeconds(30));
        pool.LookupElapsed.Should().Be(TimeSpan.FromSeconds(40));
    }

    [Fact]
    public void OpenCore_DoesNotRelabelStatusesOtherThanMissingPdb_AfterBudgetIsSpent()
    {
        var budget = new EtwSymbolLookupBudget(TimeSpan.Zero, TimeSpan.FromSeconds(60), new ManualTimeProvider());

        var pool = EtwPdbSymbolResolverPool.OpenCore(
            new[] { 1 },
            module => (ModuleFileIndex)module,
            budget,
            setServerTimeout: null,
            enterCacheOnly: null,
            (int module, out EtwPdbSymbolResolver? resolver, out NativeSymbolResolverOpenStatus status) =>
            {
                resolver = null;
                status = NativeSymbolResolverOpenStatus.MissingPdbIdentity;
                return false;
            });

        pool.OpenStatusCounts.Should().ContainSingle()
            .Which.Key.Should().Be(NativeSymbolResolverOpenStatus.MissingPdbIdentity);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => ticks;

        public void Advance(TimeSpan by) => ticks += by.Ticks;
    }
}
