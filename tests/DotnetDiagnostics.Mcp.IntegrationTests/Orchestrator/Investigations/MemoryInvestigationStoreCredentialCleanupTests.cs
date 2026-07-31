using DotnetDiagnostics.Mcp.Orchestrator.Investigations;
using FluentAssertions;

namespace DotnetDiagnostics.Mcp.IntegrationTests.Orchestrator.Investigations;

public sealed class MemoryInvestigationStoreCredentialCleanupTests
{
    [Fact]
    public void TrySetCredentialsMayBeInUse_CannotResurrectTerminalHandle()
    {
        var store = new MemoryInvestigationStore();
        var handle = Handle(credentialsMayBeInUse: false);
        store.Add(handle);
        store.TryTransitionToTerminal(
            handle.HandleId,
            InvestigationState.Expired,
            "attach deadline elapsed",
            out _);
        store.ScrubCredentials(handle.HandleId, InvestigationCredentialMaterial.All);

        var changed = store.TrySetCredentialsMayBeInUse(
            handle.HandleId,
            mayBeInUse: true,
            out var current);

        changed.Should().BeFalse();
        current!.State.Should().Be(InvestigationState.Expired);
        current.Kubernetes!.CredentialsMayBeInUse.Should().BeFalse();
        current.Kubernetes.PodLocalBearerToken.Should().BeEmpty();
        current.Kubernetes.CredentialSecretName.Should().BeNull();
        current.InternalScopeDelegationKey.Should().BeNull();
    }

    [Fact]
    public void TrySetCredentialsMayBeInUse_DefinitiveRejectionCanClearTerminalFlag()
    {
        var store = new MemoryInvestigationStore();
        var handle = Handle(credentialsMayBeInUse: true);
        store.Add(handle);
        store.TryTransitionToTerminal(
            handle.HandleId,
            InvestigationState.Failed,
            "patch rejected",
            out _);

        var changed = store.TrySetCredentialsMayBeInUse(
            handle.HandleId,
            mayBeInUse: false,
            out var current);

        changed.Should().BeTrue();
        current!.State.Should().Be(InvestigationState.Failed);
        current.Kubernetes!.CredentialsMayBeInUse.Should().BeFalse();
        current.Kubernetes.PodLocalBearerToken.Should().Be("bearer");
        current.InternalScopeDelegationKey.Should().Be("delegation");
    }

    private static InvestigationHandle Handle(bool credentialsMayBeInUse)
        => new(
            HandleId: "inv-delivery-race",
            Kubernetes: new KubernetesInvestigationTarget(
                "ns",
                "pod",
                "app",
                "diag",
                "bearer",
                "credential-secret",
                CredentialsMayBeInUse: credentialsMayBeInUse),
            State: InvestigationState.Attaching,
            AttachedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5),
            InternalScopeDelegationKey: "delegation");
}
