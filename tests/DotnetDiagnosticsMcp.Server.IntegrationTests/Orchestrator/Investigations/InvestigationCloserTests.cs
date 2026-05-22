using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnosticsMcp.Server.Orchestrator.Investigations;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests.Orchestrator.Investigations;

/// <summary>
/// Unit tests for <see cref="InvestigationCloser"/>. Covers the shared cleanup pipeline
/// driven by <c>detach_from_pod</c> (caller close) and the TTL reaper (server eviction).
/// </summary>
public sealed class InvestigationCloserTests
{
    private static InvestigationHandle Active(string id = "h-1") => new(
        HandleId: id,
        Namespace: "ns",
        PodName: "pod",
        TargetContainerName: "api",
        EphemeralContainerName: "diag",
        PodLocalBearerToken: "secret",
        State: InvestigationState.Active,
        AttachedAt: DateTimeOffset.UtcNow,
        ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30));

    [Fact]
    public async Task CloseAsync_UnknownHandle_ReturnsNotFound_NoSideEffects()
    {
        var fx = new Fixture();
        var outcome = await fx.Closer.CloseAsync("missing", InvestigationState.Closed);

        outcome.Found.Should().BeFalse();
        outcome.AlreadyTerminal.Should().BeFalse();
        outcome.PreviousState.Should().BeNull();
        outcome.NewState.Should().BeNull();
        outcome.UnboundSessionIds.Should().BeEmpty();
        fx.Proxy.DisposeCalls.Should().BeEmpty();
        fx.PortForward.CloseCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task CloseAsync_EmptyHandleId_ReturnsNotFound()
    {
        var fx = new Fixture();
        var outcome = await fx.Closer.CloseAsync(string.Empty, InvestigationState.Closed);
        outcome.Found.Should().BeFalse();
        outcome.HandleId.Should().BeEmpty();
    }

    [Fact]
    public async Task CloseAsync_ActiveHandle_TransitionsAndRunsCleanupInOrder()
    {
        var fx = new Fixture();
        var h = Active();
        fx.Store.Add(h);
        fx.Binder.Bind("session-1", h.HandleId);
        fx.Binder.Bind("session-2", h.HandleId);

        var outcome = await fx.Closer.CloseAsync(h.HandleId, InvestigationState.Closed);

        outcome.Found.Should().BeTrue();
        outcome.AlreadyTerminal.Should().BeFalse();
        outcome.PreviousState.Should().Be(InvestigationState.Active);
        outcome.NewState.Should().Be(InvestigationState.Closed);
        outcome.UnboundSessionIds.Should().BeEquivalentTo(new[] { "session-1", "session-2" });

        fx.Store.GetById(h.HandleId)!.State.Should().Be(InvestigationState.Closed);
        fx.Proxy.DisposeCalls.Should().Equal(h.HandleId);
        fx.PortForward.CloseCalls.Should().Equal(h.HandleId);
        fx.Order.Should().Equal("proxy-dispose", "portforward-close");
    }

    [Fact]
    public async Task CloseAsync_AlreadyClosed_IsIdempotent_DrainsResidualState()
    {
        var fx = new Fixture();
        var h = Active() with { State = InvestigationState.Closed };
        fx.Store.Add(h);
        fx.Binder.Bind("late-session", h.HandleId);

        var outcome = await fx.Closer.CloseAsync(h.HandleId, InvestigationState.Closed);

        outcome.Found.Should().BeTrue();
        outcome.AlreadyTerminal.Should().BeTrue();
        outcome.PreviousState.Should().Be(InvestigationState.Closed);
        outcome.NewState.Should().Be(InvestigationState.Closed);
        outcome.UnboundSessionIds.Should().Equal("late-session");
        fx.Proxy.DisposeCalls.Should().Equal(h.HandleId); // still drained
        fx.PortForward.CloseCalls.Should().Equal(h.HandleId);
    }

    [Fact]
    public async Task CloseAsync_Expired_WithReason_RecordsFailureReason()
    {
        var fx = new Fixture();
        var h = Active();
        fx.Store.Add(h);

        var outcome = await fx.Closer.CloseAsync(h.HandleId, InvestigationState.Expired, "TTL expired");

        outcome.NewState.Should().Be(InvestigationState.Expired);
        fx.Store.GetById(h.HandleId)!.State.Should().Be(InvestigationState.Expired);
        fx.Store.GetById(h.HandleId)!.FailureReason.Should().Be("TTL expired");
    }

    [Fact]
    public async Task CloseAsync_TransitioningToClosed_PreservesExistingFailureReason()
    {
        var fx = new Fixture();
        var h = Active() with { FailureReason = "carried-over" };
        fx.Store.Add(h);

        await fx.Closer.CloseAsync(h.HandleId, InvestigationState.Closed, failureReason: "ignored");

        fx.Store.GetById(h.HandleId)!.FailureReason.Should().Be("carried-over");
    }

    [Fact]
    public async Task CloseAsync_ProxyDisposeThrows_StillClosesAndUnbinds()
    {
        var fx = new Fixture();
        fx.Proxy.ThrowOnDispose = new InvalidOperationException("proxy gone");
        var h = Active();
        fx.Store.Add(h);
        fx.Binder.Bind("session-x", h.HandleId);

        var outcome = await fx.Closer.CloseAsync(h.HandleId, InvestigationState.Closed);

        outcome.Found.Should().BeTrue();
        outcome.NewState.Should().Be(InvestigationState.Closed);
        fx.PortForward.CloseCalls.Should().Equal(h.HandleId);
        outcome.UnboundSessionIds.Should().Equal("session-x");
    }

    [Fact]
    public async Task CloseAsync_PortForwardThrows_StillUnbinds()
    {
        var fx = new Fixture();
        fx.PortForward.ThrowOnClose = new InvalidOperationException("transport gone");
        var h = Active();
        fx.Store.Add(h);
        fx.Binder.Bind("session-y", h.HandleId);

        var outcome = await fx.Closer.CloseAsync(h.HandleId, InvestigationState.Closed);

        outcome.Found.Should().BeTrue();
        outcome.UnboundSessionIds.Should().Equal("session-y");
    }

    private sealed class Fixture
    {
        public MemoryInvestigationStore Store { get; } = new();
        public RecordingProxyClient Proxy { get; }
        public RecordingPortForwardManager PortForward { get; }
        public MemoryInvestigationSessionBinder Binder { get; } = new();
        public List<string> Order { get; } = new();
        public InvestigationCloser Closer { get; }

        public Fixture()
        {
            Proxy = new RecordingProxyClient(Order);
            PortForward = new RecordingPortForwardManager(Order);
            Closer = new InvestigationCloser(Store, Proxy, PortForward, Binder);
        }
    }

    private sealed class RecordingProxyClient : IInvestigationProxyClient
    {
        private readonly List<string> _order;
        public List<string> DisposeCalls { get; } = new();
        public Exception? ThrowOnDispose;
        public RecordingProxyClient(List<string> order) { _order = order; }

        public Task<CallToolResult> CallToolAsync(InvestigationHandle handle, CallToolRequestParams request, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not under test here.");

        public Task DisposeForHandleAsync(string handleId)
        {
            _order.Add("proxy-dispose");
            DisposeCalls.Add(handleId);
            if (ThrowOnDispose is not null) throw ThrowOnDispose;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingPortForwardManager : IPortForwardManager
    {
        private readonly List<string> _order;
        public List<string> CloseCalls { get; } = new();
        public Exception? ThrowOnClose;
        public RecordingPortForwardManager(List<string> order) { _order = order; }

        public Task<System.Net.Http.HttpClient> GetOrCreateClientAsync(InvestigationHandle handle, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not under test here.");

        public Task CloseAsync(string handleId)
        {
            _order.Add("portforward-close");
            CloseCalls.Add(handleId);
            if (ThrowOnClose is not null) throw ThrowOnClose;
            return Task.CompletedTask;
        }
    }
}
