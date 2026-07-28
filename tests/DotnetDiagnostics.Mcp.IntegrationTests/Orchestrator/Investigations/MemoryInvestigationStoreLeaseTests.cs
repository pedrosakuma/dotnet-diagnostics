using System;
using DotnetDiagnostics.Mcp.Orchestrator.Investigations;
using FluentAssertions;

namespace DotnetDiagnostics.Mcp.IntegrationTests.Orchestrator.Investigations;

public sealed class MemoryInvestigationStoreLeaseTests
{
    [Fact]
    public void TryTouchSuccessfulCall_RefreshesIdleLease_AndClampsToAbsoluteExpiry()
    {
        var store = new MemoryInvestigationStore();
        var attachedAt = new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);
        var handle = new InvestigationHandle(
            HandleId: "inv-clamp",
            Kubernetes: new KubernetesInvestigationTarget("ns", "pod", "api", "diag", "pod-bearer"),
            State: InvestigationState.Active,
            AttachedAt: attachedAt,
            Lease: InvestigationLeasePolicy.Create(
                attachedAt,
                TimeSpan.FromMinutes(1),
                TimeSpan.FromHours(7),
                TimeSpan.FromHours(8)),
            InternalScopeDelegationKey: "delegation");
        store.Add(handle);

        var touchedAt = attachedAt.AddHours(2);
        var result = store.TryTouchSuccessfulCall(handle.HandleId, touchedAt, out var updated);

        result.Should().Be(InvestigationLeaseTouchResult.Touched);
        updated.Should().NotBeNull();
        updated!.LastSuccessfulUseAt.Should().Be(touchedAt);
        updated.IdleExpiresAt.Should().Be(attachedAt.AddHours(8));
        updated.AbsoluteExpiresAt.Should().Be(attachedAt.AddHours(8));
        updated.ExpiresAt.Should().Be(attachedAt.AddHours(8));
    }

    [Fact]
    public void TryTouchSuccessfulCall_WhenHandleAlreadyClosed_DoesNotResurrectIt()
    {
        var store = new MemoryInvestigationStore();
        var attachedAt = new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);
        var handle = new InvestigationHandle(
            HandleId: "inv-race",
            Kubernetes: new KubernetesInvestigationTarget("ns", "pod", "api", "diag", "pod-bearer"),
            State: InvestigationState.Active,
            AttachedAt: attachedAt,
            Lease: InvestigationLeasePolicy.Create(
                attachedAt,
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(30),
                TimeSpan.FromHours(8)),
            InternalScopeDelegationKey: "delegation");
        store.Add(handle);

        store.TryTransitionToTerminal(handle.HandleId, InvestigationState.Closed, failureReason: null, out _)
            .Should().Be(InvestigationTerminalTransition.Transitioned);

        var result = store.TryTouchSuccessfulCall(handle.HandleId, attachedAt.AddMinutes(5), out var updated);

        result.Should().Be(InvestigationLeaseTouchResult.Skipped);
        updated.Should().NotBeNull();
        updated!.State.Should().Be(InvestigationState.Closed);
        updated.LastSuccessfulUseAt.Should().BeNull();
        updated.IdleExpiresAt.Should().Be(handle.IdleExpiresAt);
    }
}
