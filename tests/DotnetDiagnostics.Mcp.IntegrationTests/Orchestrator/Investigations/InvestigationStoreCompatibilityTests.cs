using DotnetDiagnostics.Mcp.Orchestrator.Investigations;
using FluentAssertions;

namespace DotnetDiagnostics.Mcp.IntegrationTests.Orchestrator.Investigations;

public sealed class InvestigationStoreCompatibilityTests
{
    [Fact]
    public void LegacyStoreImplementation_DoesNotNeedActivationMember()
    {
        IInvestigationStore store = new LegacyInvestigationStore();

        store.Should().NotBeAssignableTo<IInvestigationStoreActivation>();
        typeof(IInvestigationStore).GetMethod("TryTransitionToActive").Should().BeNull();
    }

    private sealed class LegacyInvestigationStore : IInvestigationStore
    {
        public void Add(InvestigationHandle handle) => throw new NotSupportedException();

        public bool TryReserveTarget(
            InvestigationHandle newHandle,
            bool allowReuse,
            out InvestigationHandle? existing)
        {
            existing = null;
            throw new NotSupportedException();
        }

        public void Update(InvestigationHandle handle) => throw new NotSupportedException();

        public InvestigationTerminalTransition TryTransitionToTerminal(
            string handleId,
            InvestigationState targetState,
            string? failureReason,
            out InvestigationState? previousState)
        {
            previousState = null;
            throw new NotSupportedException();
        }

        public InvestigationHandle? GetById(string handleId) => null;

        public InvestigationHandle? FindReusableTarget(string reservationKey) => null;

        public IReadOnlyCollection<InvestigationHandle> Snapshot() => [];
    }
}
