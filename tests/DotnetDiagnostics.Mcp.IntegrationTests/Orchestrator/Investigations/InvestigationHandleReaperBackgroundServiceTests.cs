using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnostics.Mcp.Hosting;
using DotnetDiagnostics.Mcp.Observability;
using DotnetDiagnostics.Mcp.Orchestrator.Investigations;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DotnetDiagnostics.Mcp.IntegrationTests.Orchestrator.Investigations;

/// <summary>
/// Unit tests for <see cref="InvestigationHandleReaperBackgroundService.ReapExpiredAsync"/>.
/// </summary>
public sealed class InvestigationHandleReaperBackgroundServiceTests
{
    private static InvestigationHandle Handle(
        string id,
        InvestigationState state,
        DateTimeOffset attachedAt,
        DateTimeOffset attachDeadline,
        DateTimeOffset idleExpiresAt,
        DateTimeOffset absoluteExpiresAt,
        DateTimeOffset? lastSuccessfulUseAt = null) => new(
            HandleId: id,
            Kubernetes: new KubernetesInvestigationTarget("ns", "pod", "api", "diag", "secret"),
            State: state,
            AttachedAt: attachedAt,
            Lease: new InvestigationLease(
                IdleTtl: idleExpiresAt > attachedAt ? idleExpiresAt - attachedAt : TimeSpan.Zero,
                AttachDeadline: attachDeadline,
                LastSuccessfulUseAt: lastSuccessfulUseAt,
                IdleExpiresAt: idleExpiresAt,
                AbsoluteExpiresAt: absoluteExpiresAt));

    [Fact]
    public async Task ReapExpiredAsync_TransitionsActiveHandlesPastTtl_ToExpired()
    {
        var fx = new Fixture();
        var now = DateTimeOffset.UtcNow;
        var attachedAt = now.AddMinutes(-10);
        fx.Store.Add(Handle("expired", InvestigationState.Active, attachedAt, now.AddMinutes(-9), now.AddSeconds(-1), now.AddHours(6), now.AddMinutes(-1)));
        fx.Store.Add(Handle("fresh", InvestigationState.Active, attachedAt, now.AddMinutes(-9), now.AddMinutes(5), now.AddHours(6), now.AddMinutes(-1)));
        fx.Store.Add(Handle("stuck-attach", InvestigationState.Attaching, attachedAt, now.AddSeconds(-30), now.AddMinutes(20), now.AddHours(6)));
        fx.Store.Add(Handle("already-closed", InvestigationState.Closed, attachedAt, now.AddMinutes(-9), now.AddSeconds(-1), now.AddHours(6)));

        var reaped = await fx.Reaper.ReapExpiredAsync(now);

        reaped.Should().Be(2);
        fx.Store.GetById("expired")!.State.Should().Be(InvestigationState.Expired);
        fx.Store.GetById("stuck-attach")!.State.Should().Be(InvestigationState.Expired);
        fx.Store.GetById("fresh")!.State.Should().Be(InvestigationState.Active);
        fx.Store.GetById("already-closed")!.State.Should().Be(InvestigationState.Closed);

        fx.Proxy.DisposeCalls.Should().BeEquivalentTo(new[] { "expired", "stuck-attach" });
        fx.PortForward.CloseCalls.Should().BeEquivalentTo(new[] { "expired", "stuck-attach" });
    }

    [Fact]
    public async Task ReapExpiredAsync_RecordsTtlReasonOnExpiry()
    {
        var fx = new Fixture();
        var now = DateTimeOffset.UtcNow;
        var attachedAt = now.AddMinutes(-10);
        fx.Store.Add(Handle("h", InvestigationState.Active, attachedAt, now.AddMinutes(-9), now.AddSeconds(-5), now.AddHours(6), now.AddMinutes(-2)));

        await fx.Reaper.ReapExpiredAsync(now);

        var after = fx.Store.GetById("h")!;
        after.State.Should().Be(InvestigationState.Expired);
        after.FailureReason.Should().NotBeNullOrEmpty();
        after.FailureReason.Should().Contain("Idle lease expired");
    }

    [Fact]
    public async Task ReapExpiredAsync_AttachingHandlesUseAttachDeadline_NotIdleExpiry()
    {
        var fx = new Fixture();
        var now = DateTimeOffset.UtcNow;
        var attachedAt = now.AddMinutes(-2);
        fx.Store.Add(Handle(
            "attach-past-deadline",
            InvestigationState.Attaching,
            attachedAt,
            now.AddSeconds(-1),
            now.AddMinutes(10),
            now.AddHours(6)));
        fx.Store.Add(Handle(
            "attach-future-deadline",
            InvestigationState.Attaching,
            attachedAt,
            now.AddMinutes(1),
            now.AddSeconds(-1),
            now.AddHours(6)));

        var reaped = await fx.Reaper.ReapExpiredAsync(now);

        reaped.Should().Be(1);
        fx.Store.GetById("attach-past-deadline")!.State.Should().Be(InvestigationState.Expired);
        fx.Store.GetById("attach-future-deadline")!.State.Should().Be(InvestigationState.Attaching);
    }

    [Fact]
    public async Task ReapExpiredAsync_ActiveHandlesHonorAbsoluteExpiry()
    {
        var fx = new Fixture();
        var now = DateTimeOffset.UtcNow;
        var attachedAt = now.AddHours(-7).AddMinutes(-50);
        fx.Store.Add(Handle(
            "absolute-expired",
            InvestigationState.Active,
            attachedAt,
            attachedAt.AddMinutes(1),
            now.AddMinutes(20),
            now.AddSeconds(-1),
            now.AddMinutes(-5)));

        var reaped = await fx.Reaper.ReapExpiredAsync(now);

        reaped.Should().Be(1);
        var expired = fx.Store.GetById("absolute-expired")!;
        expired.State.Should().Be(InvestigationState.Expired);
        expired.FailureReason.Should().Contain("Absolute lease expired");
    }

    [Fact]
    public async Task ReapExpiredAsync_TouchRace_DoesNotExpireRefreshedHandle()
    {
        var attachedAt = new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);
        var staleSnapshot = Handle(
            "touch-race",
            InvestigationState.Active,
            attachedAt,
            attachedAt.AddMinutes(1),
            attachedAt.AddMinutes(30),
            attachedAt.AddHours(8));
        var current = staleSnapshot with
        {
            Lease = InvestigationLeasePolicy.RecordSuccessfulUse(staleSnapshot.Lease, attachedAt.AddMinutes(29)),
        };
        var store = new TouchBeforeExpireStore(staleSnapshot, current);
        var fx = new Fixture(store);
        var now = attachedAt.AddMinutes(31);

        var reaped = await fx.Reaper.ReapExpiredAsync(now);

        reaped.Should().Be(0);
        store.GetById("touch-race")!.State.Should().Be(InvestigationState.Active);
        store.GetById("touch-race")!.LastSuccessfulUseAt.Should().Be(attachedAt.AddMinutes(29));
        fx.Proxy.DisposeCalls.Should().BeEmpty();
        fx.PortForward.CloseCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task ReapExpiredAsync_EmptyStore_IsNoOp()
    {
        var fx = new Fixture();
        var reaped = await fx.Reaper.ReapExpiredAsync(DateTimeOffset.UtcNow);
        reaped.Should().Be(0);
    }

    private sealed class Fixture
    {
        public IInvestigationStore Store { get; }
        public CountingProxy Proxy { get; } = new();
        public CountingPortForward PortForward { get; } = new();
        public MemoryInvestigationSessionBinder Binder { get; } = new();
        public InvestigationHandleReaperBackgroundService Reaper { get; }

        public Fixture(IInvestigationStore? store = null)
        {
            Store = store ?? new MemoryInvestigationStore();
            var services = new ServiceCollection();
            services.AddMetrics();
            var provider = services.BuildServiceProvider();
            var observability = new OrchestratorObservability(
                provider.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(),
                Store,
                new AuditLogWriter(TextWriter.Null));
            var closer = new InvestigationCloser(Store, Proxy, PortForward, Binder);
            Reaper = new InvestigationHandleReaperBackgroundService(Store, closer, observability);
        }
    }

    private sealed class CountingProxy : IInvestigationProxyClient
    {
        public List<string> DisposeCalls { get; } = new();
        public Task<CallToolResult> CallToolAsync(InvestigationHandle handle, CallToolRequestParams request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task EnsureInitializedAsync(InvestigationHandle handle, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DisposeForHandleAsync(string handleId) { DisposeCalls.Add(handleId); return Task.CompletedTask; }
    }

    private sealed class CountingPortForward : IPortForwardManager
    {
        public List<string> CloseCalls { get; } = new();
        public Task<System.Net.Http.HttpClient> GetOrCreateClientAsync(InvestigationHandle handle, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task CloseAsync(string handleId) { CloseCalls.Add(handleId); return Task.CompletedTask; }
    }

    private sealed class TouchBeforeExpireStore : IInvestigationStore, IInvestigationStoreExpiry
    {
        private readonly InvestigationHandle _snapshotHandle;
        private InvestigationHandle _currentHandle;
        private bool _touched;

        public TouchBeforeExpireStore(InvestigationHandle snapshotHandle, InvestigationHandle currentHandle)
        {
            _snapshotHandle = snapshotHandle;
            _currentHandle = currentHandle;
        }

        public void Add(InvestigationHandle handle) => throw new NotSupportedException();

        public bool TryReserveTarget(InvestigationHandle newHandle, bool allowReuse, out InvestigationHandle? existing)
            => throw new NotSupportedException();

        public void Update(InvestigationHandle handle) => _currentHandle = handle;

        public InvestigationTerminalTransition TryTransitionToTerminal(
            string handleId,
            InvestigationState targetState,
            string? failureReason,
            out InvestigationState? previousState) => throw new NotSupportedException();

        public InvestigationHandle? GetById(string handleId)
            => string.Equals(handleId, _currentHandle.HandleId, StringComparison.Ordinal) ? _currentHandle : null;

        public InvestigationHandle? FindReusableTarget(string reservationKey) => null;

        public InvestigationHandle? FindTerminalHandleByEphemeralName(
            string podNamespace,
            string podName,
            string ephemeralContainerName) => null;

        public IReadOnlyCollection<InvestigationHandle> Snapshot() => [_snapshotHandle];

        public InvestigationExpiryTransition TryTransitionToExpiredIfStillExpired(
            string handleId,
            DateTimeOffset now,
            string failureReason,
            out InvestigationHandle? updated,
            out InvestigationState? previousState)
        {
            if (!_touched)
            {
                _touched = true;
                updated = _currentHandle;
                previousState = _currentHandle.State;
                return InvestigationLeasePolicy.IsExpired(_currentHandle, now)
                    ? InvestigationExpiryTransition.Transitioned
                    : InvestigationExpiryTransition.Skipped;
            }

            updated = _currentHandle;
            previousState = _currentHandle.State;
            return InvestigationLeasePolicy.IsExpired(_currentHandle, now)
                ? InvestigationExpiryTransition.Transitioned
                : InvestigationExpiryTransition.Skipped;
        }
    }
}
