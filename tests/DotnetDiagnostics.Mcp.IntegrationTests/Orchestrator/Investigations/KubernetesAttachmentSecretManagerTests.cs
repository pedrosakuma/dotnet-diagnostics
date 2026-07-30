using System.Net;
using System.Net.Http;
using DotnetDiagnostics.Mcp.Orchestrator.Investigations;
using FluentAssertions;
using k8s.Autorest;

namespace DotnetDiagnostics.Mcp.IntegrationTests.Orchestrator.Investigations;

public sealed class KubernetesAttachmentSecretManagerTests
{
    [Fact]
    public async Task DeleteAsync_NotFound_IsIdempotentSuccess()
    {
        var manager = new KubernetesAttachmentSecretManager(
            (_, _, _) => throw NewHttpException(HttpStatusCode.NotFound));

        var act = () => manager.DeleteAsync(Handle(), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeleteAsync_TransientFailure_RemainsRetryable()
    {
        var attempts = 0;
        var manager = new KubernetesAttachmentSecretManager(
            (_, _, _) =>
            {
                attempts++;
                return attempts == 1
                    ? throw NewHttpException(HttpStatusCode.ServiceUnavailable)
                    : Task.CompletedTask;
            });
        var handle = Handle();

        var first = () => manager.DeleteAsync(handle, CancellationToken.None);
        await first.Should().ThrowAsync<HttpOperationException>();
        await manager.DeleteAsync(handle, CancellationToken.None);

        attempts.Should().Be(2);
    }

    private static InvestigationHandle Handle()
        => new(
            HandleId: "inv-secret",
            Kubernetes: new KubernetesInvestigationTarget(
                "ns",
                "pod",
                "app",
                "diag",
                "bearer",
                "credential-secret"),
            State: InvestigationState.Closed,
            AttachedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5),
            InternalScopeDelegationKey: "delegation");

    private static HttpOperationException NewHttpException(HttpStatusCode statusCode)
        => new($"HTTP {(int)statusCode}")
        {
            Response = new HttpResponseMessageWrapper(
                new HttpResponseMessage(statusCode),
                string.Empty),
        };
}
