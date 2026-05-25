using System;
using System.Linq;
using System.Threading.Tasks;
using DotnetDiagnosticsMcp.Server.Azure;
using DotnetDiagnosticsMcp.Server.Orchestrator;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests.Azure;

/// <summary>
/// Capacity-bound + active-eviction + disposal tests for
/// <see cref="InMemoryKubeconfigHandleStore"/> (FIX 2 from the #234 review).
/// </summary>
public sealed class KubeconfigHandleStoreEvictionTests
{
    [Fact]
    public void Register_AtCapacity_EvictsClosestToExpiry_BeforeAdding()
    {
        var clock = new ManualClock();
        var sut = new InMemoryKubeconfigHandleStore(
            new AzureDiscoveryOptions
            {
                Enabled = true,
                KubeconfigHandleTtl = TimeSpan.FromMinutes(10),
                KubeconfigHandleMaxEntries = 3,
            },
            clock);

        // 3 entries minted at 0/+1m/+2m. Per-entry expiry: t+10m / t+11m / t+12m.
        var first = sut.Register(new byte[] { 1, 1, 1, 1 });
        var firstBytes = new byte[] { 1, 1, 1, 1 }; // we'll detect zero-out indirectly via Count + behavior

        clock.Advance(TimeSpan.FromMinutes(1));
        var second = sut.Register(new byte[] { 2, 2 });

        clock.Advance(TimeSpan.FromMinutes(1));
        var third = sut.Register(new byte[] { 3, 3, 3 });

        sut.Count.Should().Be(3);

        // Subscribe AFTER warm-up so the events we capture are only the eviction.
        var evicted = new System.Collections.Generic.List<string>();
        sut.HandleEvicted += (_, e) => evicted.Add(e.Handle);

        // 4th registration MUST evict 'first' (smallest ExpiresAt).
        var fourth = sut.Register(new byte[] { 4 });

        sut.Count.Should().Be(3);
        sut.TryResolve(first.Handle).Should().BeNull("the oldest-expiring entry must be the eviction victim");
        sut.TryResolve(second.Handle).Should().NotBeNull();
        sut.TryResolve(third.Handle).Should().NotBeNull();
        sut.TryResolve(fourth.Handle).Should().NotBeNull();

        evicted.Should().ContainSingle().Which.Should().Be(first.Handle);
    }

    [Fact]
    public void Register_AtCapacity_EvictionZeroesEvictedBuffer()
    {
        var clock = new ManualClock();
        var sut = new InMemoryKubeconfigHandleStore(
            new AzureDiscoveryOptions { Enabled = true, KubeconfigHandleMaxEntries = 1 },
            clock);

        var payload = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        var observable = payload; // keep reference to original buffer to inspect after eviction.
        sut.Register(payload);

        // Force capacity-bound eviction by registering a second entry.
        sut.Register(new byte[] { 0x01 });

        observable.Should().OnlyContain(b => b == 0,
            "the capacity-evicted entry's underlying byte buffer must be Array.Clear'd before removal");
    }

    [Fact]
    public void TryPeekExpiry_LiveHandle_ReturnsRegisteredExpiry_NotBytes()
    {
        var clock = new ManualClock();
        var sut = new InMemoryKubeconfigHandleStore(
            new AzureDiscoveryOptions { Enabled = true, KubeconfigHandleTtl = TimeSpan.FromMinutes(5) },
            clock);

        var mint = sut.Register(new byte[] { 1, 2, 3 });
        sut.TryPeekExpiry(mint.Handle).Should().Be(mint.ExpiresAt);
        sut.TryPeekExpiry("kc:nonexistent").Should().BeNull();
    }

    [Fact]
    public void TryPeekExpiry_ExpiredHandle_ReturnsNull_AndEvictsEntry()
    {
        var clock = new ManualClock();
        var sut = new InMemoryKubeconfigHandleStore(
            new AzureDiscoveryOptions { Enabled = true, KubeconfigHandleTtl = TimeSpan.FromMinutes(1) },
            clock);

        var mint = sut.Register(new byte[] { 1, 2, 3 });
        clock.Advance(TimeSpan.FromMinutes(2));

        sut.TryPeekExpiry(mint.Handle).Should().BeNull();
        sut.Count.Should().Be(0);
    }

    [Fact]
    public async Task DisposeAsync_ZeroesAllBuffers_AndRaisesEvictedForEachLiveHandle()
    {
        var clock = new ManualClock();
        var sut = new InMemoryKubeconfigHandleStore(
            new AzureDiscoveryOptions { Enabled = true },
            clock);

        var a = new byte[] { 9, 9, 9 };
        var b = new byte[] { 7, 7 };
        var oa = a;
        var ob = b;
        var ma = sut.Register(a);
        var mb = sut.Register(b);

        var evicted = new System.Collections.Generic.List<string>();
        sut.HandleEvicted += (_, e) => evicted.Add(e.Handle);

        await sut.DisposeAsync();

        oa.Should().OnlyContain(x => x == 0);
        ob.Should().OnlyContain(x => x == 0);
        evicted.Should().Contain(new[] { ma.Handle, mb.Handle });
    }

    [Fact]
    public void Register_AfterDispose_Throws()
    {
        var sut = new InMemoryKubeconfigHandleStore(new AzureDiscoveryOptions(), new ManualClock());
        sut.Dispose();
        Action act = () => sut.Register(new byte[] { 1 });
        act.Should().Throw<ObjectDisposedException>();
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }
}
