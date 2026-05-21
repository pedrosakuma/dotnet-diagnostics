using DotnetDiagnosticsMcp.Core.ProcessDiscovery;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Core.Tests;

/// <summary>
/// Covers the Phase 2 in-memory <see cref="ISessionTargetBindingStore"/> introduced by
/// the central-orchestrator design (issue #20): set / get / remove / TTL eviction /
/// null + empty session id handling.
/// </summary>
public sealed class MemorySessionTargetBindingStoreTests
{
    [Fact]
    public void TryGet_UnknownSession_ReturnsNull()
    {
        var store = new MemorySessionTargetBindingStore();

        store.TryGet("never-set").Should().BeNull();
    }

    [Fact]
    public void TryGet_NullOrEmptySessionId_ReturnsNull()
    {
        var store = new MemorySessionTargetBindingStore();
        store.SetBinding("s1", new SessionTargetBinding(1234, "local-test"));

        store.TryGet(null).Should().BeNull();
        store.TryGet(string.Empty).Should().BeNull();
    }

    [Fact]
    public void SetBinding_ThenTryGet_ReturnsExactBinding()
    {
        var store = new MemorySessionTargetBindingStore();
        var binding = new SessionTargetBinding(4242, "orchestrator-attach");

        store.SetBinding("session-A", binding);

        store.TryGet("session-A").Should().Be(binding);
    }

    [Fact]
    public void SetBinding_OverwritesExistingBindingForSameSession()
    {
        var store = new MemorySessionTargetBindingStore();
        store.SetBinding("s", new SessionTargetBinding(1, "local-test"));
        store.SetBinding("s", new SessionTargetBinding(2, "orchestrator-attach"));

        var binding = store.TryGet("s");

        binding.Should().NotBeNull();
        binding!.ProcessId.Should().Be(2);
        binding.Source.Should().Be("orchestrator-attach");
    }

    [Fact]
    public void Remove_RemovesExistingBindingAndReturnsTrue()
    {
        var store = new MemorySessionTargetBindingStore();
        store.SetBinding("s", new SessionTargetBinding(7, "local-test"));

        store.Remove("s").Should().BeTrue();
        store.TryGet("s").Should().BeNull();
    }

    [Fact]
    public void Remove_UnknownSession_ReturnsFalse()
    {
        var store = new MemorySessionTargetBindingStore();

        store.Remove("never-set").Should().BeFalse();
        store.Remove(null).Should().BeFalse();
        store.Remove(string.Empty).Should().BeFalse();
    }

    [Fact]
    public void TryGet_ExpiredBinding_ReturnsNullAndEvicts()
    {
        var clock = new FakeTimeProvider();
        var store = new MemorySessionTargetBindingStore(clock);
        store.SetBinding("s", new SessionTargetBinding(1, "local-test", ExpiresAt: clock.GetUtcNow() + TimeSpan.FromSeconds(30)));

        clock.Advance(TimeSpan.FromSeconds(45));

        store.TryGet("s").Should().BeNull();
        // Eviction is observable: a subsequent Set+TryGet works normally.
        store.SetBinding("s", new SessionTargetBinding(2, "orchestrator-attach"));
        store.TryGet("s")!.ProcessId.Should().Be(2);
    }

    [Fact]
    public void TryGet_BindingWithNullExpiry_NeverExpires()
    {
        var clock = new FakeTimeProvider();
        var store = new MemorySessionTargetBindingStore(clock);
        store.SetBinding("s", new SessionTargetBinding(9, "local-test"));

        clock.Advance(TimeSpan.FromDays(365));

        store.TryGet("s")!.ProcessId.Should().Be(9);
    }

    [Fact]
    public void SetBinding_NullSessionId_Throws()
    {
        var store = new MemorySessionTargetBindingStore();

        Action act = () => store.SetBinding(null!, new SessionTargetBinding(1, "x"));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SetBinding_EmptySessionId_Throws()
    {
        var store = new MemorySessionTargetBindingStore();

        Action act = () => store.SetBinding(string.Empty, new SessionTargetBinding(1, "x"));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Bindings_AreIsolatedPerSession()
    {
        var store = new MemorySessionTargetBindingStore();
        store.SetBinding("s1", new SessionTargetBinding(11, "local-test"));
        store.SetBinding("s2", new SessionTargetBinding(22, "orchestrator-attach"));

        store.TryGet("s1")!.ProcessId.Should().Be(11);
        store.TryGet("s2")!.ProcessId.Should().Be(22);
        store.Remove("s1");
        store.TryGet("s2")!.ProcessId.Should().Be(22);
    }

    /// <summary>
    /// Minimal manually-advanced clock. Mirrors the one in ProcessContextResolverTests so the
    /// two suites do not drag in Microsoft.Extensions.TimeProvider.Testing for one assertion.
    /// </summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }
}
