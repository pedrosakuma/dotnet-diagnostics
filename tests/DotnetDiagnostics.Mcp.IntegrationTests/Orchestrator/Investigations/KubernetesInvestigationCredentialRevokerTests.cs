using System.Net;
using System.Net.Http;
using DotnetDiagnostics.Mcp.Hosting;
using DotnetDiagnostics.Mcp.Orchestrator;
using DotnetDiagnostics.Mcp.Orchestrator.Investigations;
using FluentAssertions;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace DotnetDiagnostics.Mcp.IntegrationTests.Orchestrator.Investigations;

public sealed class KubernetesInvestigationCredentialRevokerTests
{
    [Fact]
    public async Task RevokeAsync_UsesInternalEndpoint_AndWaitsForContainerTermination()
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        var transport = new StubTransport(handler);
        var pods = new StubPodsApi(terminated: true);
        var revoker = new KubernetesInvestigationCredentialRevoker(
            transport,
            pods,
            TimeProvider.System);

        await revoker.RevokeAsync(Handle(), CancellationToken.None);

        handler.RequestPath.Should().Be(EphemeralAttachmentLifetime.RevokePath);
        pods.ReadCount.Should().Be(1);
    }

    [Fact]
    public async Task RevokeAsync_NonSuccessResponse_FailsClosed()
    {
        var handler = new RecordingHandler(HttpStatusCode.NotFound);
        var revoker = new KubernetesInvestigationCredentialRevoker(
            new StubTransport(handler),
            new StubPodsApi(terminated: false),
            TimeProvider.System);

        var act = () => revoker.RevokeAsync(Handle(), CancellationToken.None);

        (await act.Should().ThrowAsync<OrchestratorException>())
            .Which.ErrorKind.Should().Be(OrchestratorErrorKinds.PortForwardFailed);
    }

    [Fact]
    public async Task RevokeAsync_EndpointUnavailableButContainerTerminated_CountsAsSuccess()
    {
        var pods = new StubPodsApi(terminated: true);
        var revoker = new KubernetesInvestigationCredentialRevoker(
            new StubTransport(new ThrowingHandler()),
            pods,
            TimeProvider.System);

        await revoker.RevokeAsync(Handle(), CancellationToken.None);

        pods.ReadCount.Should().Be(1);
    }

    [Fact]
    public async Task RevokeAsync_PodLocalNotFoundAndTargetPodNotFound_CountsAsSuccess()
    {
        var pods = new StubPodsApi(
            readException: NewHttpException(HttpStatusCode.NotFound));
        var revoker = new KubernetesInvestigationCredentialRevoker(
            new StubTransport(new RecordingHandler(HttpStatusCode.NotFound)),
            pods,
            TimeProvider.System);

        await revoker.RevokeAsync(Handle(), CancellationToken.None);

        pods.ReadCount.Should().Be(1);
    }

    [Fact]
    public async Task RevokeAsync_TargetPodDisappearsAfterAcceptedRevoke_CountsAsSuccess()
    {
        var pods = new StubPodsApi(
            readException: NewHttpException(HttpStatusCode.NotFound));
        var revoker = new KubernetesInvestigationCredentialRevoker(
            new StubTransport(new RecordingHandler(HttpStatusCode.NoContent)),
            pods,
            TimeProvider.System);

        await revoker.RevokeAsync(Handle(), CancellationToken.None);

        pods.ReadCount.Should().Be(1);
    }

    [Fact]
    public async Task RevokeAsync_NonNotFoundKubernetesFailure_RemainsFailure()
    {
        var pods = new StubPodsApi(
            readException: NewHttpException(HttpStatusCode.ServiceUnavailable));
        var revoker = new KubernetesInvestigationCredentialRevoker(
            new StubTransport(new RecordingHandler(HttpStatusCode.NoContent)),
            pods,
            TimeProvider.System);

        var act = () => revoker.RevokeAsync(Handle(), CancellationToken.None);

        await act.Should().ThrowAsync<HttpOperationException>();
    }

    [Fact]
    public async Task RevokeAsync_ConcurrentCallers_AwaitOneSharedRevocation()
    {
        var handler = new BlockingHandler();
        var revoker = new KubernetesInvestigationCredentialRevoker(
            new StubTransport(handler),
            new StubPodsApi(terminated: true),
            TimeProvider.System);
        var handle = Handle();

        var first = revoker.RevokeAsync(handle, CancellationToken.None);
        await handler.Started.Task;
        var second = revoker.RevokeAsync(handle, CancellationToken.None);

        second.IsCompleted.Should().BeFalse();
        handler.CallCount.Should().Be(1);

        handler.Release.TrySetResult();
        await Task.WhenAll(first, second);
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task RevokeAsync_CanceledWaiter_DoesNotCancelSharedRevocation()
    {
        var handler = new BlockingHandler();
        var revoker = new KubernetesInvestigationCredentialRevoker(
            new StubTransport(handler),
            new StubPodsApi(terminated: true),
            TimeProvider.System);
        var handle = Handle();
        using var firstCancellation = new CancellationTokenSource();

        var first = revoker.RevokeAsync(handle, firstCancellation.Token);
        await handler.Started.Task;
        var second = revoker.RevokeAsync(handle, CancellationToken.None);
        firstCancellation.Cancel();

        var firstWait = async () => await first;
        await firstWait.Should().ThrowAsync<OperationCanceledException>();
        second.IsCompleted.Should().BeFalse();

        handler.Release.TrySetResult();
        await second;
        handler.CallCount.Should().Be(1);
    }

    private static InvestigationHandle Handle()
        => new(
            HandleId: "inv_test",
            Kubernetes: new KubernetesInvestigationTarget(
                "ns",
                "pod",
                "app",
                "diag",
                "token",
                "credential-secret"),
            State: InvestigationState.Active,
            AttachedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5),
            InternalScopeDelegationKey: "delegation-key");

    private static HttpOperationException NewHttpException(HttpStatusCode statusCode)
        => new($"HTTP {(int)statusCode}")
        {
            Response = new HttpResponseMessageWrapper(
                new HttpResponseMessage(statusCode),
                string.Empty),
        };

    private sealed class StubTransport : IInvestigationTransportManager
    {
        private readonly HttpClient _client;

        public StubTransport(HttpMessageHandler handler)
        {
            _client = new HttpClient(handler)
            {
                BaseAddress = new Uri("http://pod-local"),
            };
        }

        public Task<HttpClient> GetOrCreateClientAsync(
            InvestigationHandle handle,
            CancellationToken cancellationToken)
            => Task.FromResult(_client);

        public Task CloseAsync(string handleId) => Task.CompletedTask;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;

        public RecordingHandler(HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
        }

        public string? RequestPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestPath = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(_statusCode));
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        private int _callCount;

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref _callCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => throw new HttpRequestException("pod-local process exited");
    }

    private sealed class StubPodsApi : IKubernetesPodsApi
    {
        private readonly bool _terminated;
        private readonly Exception? _readException;

        public StubPodsApi(
            bool terminated = false,
            Exception? readException = null)
        {
            _terminated = terminated;
            _readException = readException;
        }

        public int ReadCount { get; private set; }

        public Task<V1Pod> ReadPodAsync(
            string namespaceName,
            string name,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            if (_readException is not null)
            {
                throw _readException;
            }
            return Task.FromResult(new V1Pod
            {
                Status = new V1PodStatus
                {
                    EphemeralContainerStatuses =
                    [
                        new V1ContainerStatus
                        {
                            Name = "diag",
                            Image = "image",
                            ImageID = "image-id",
                            Ready = false,
                            RestartCount = 0,
                            State = _terminated
                                ? new V1ContainerState
                                {
                                    Terminated = new V1ContainerStateTerminated
                                    {
                                        ExitCode = 0,
                                    },
                                }
                                : new V1ContainerState
                                {
                                    Running = new V1ContainerStateRunning(),
                                },
                        },
                    ],
                },
            });
        }

        public Task<V1PodList> ListPodsAsync(
            string? namespaceName,
            string? labelSelector,
            string? fieldSelector,
            int? limit,
            string? continueToken,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<V1Pod> AddEphemeralContainerAsync(
            string namespaceName,
            string name,
            V1EphemeralContainer ephemeralContainer,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IStreamDemuxer> OpenPortForwardAsync(
            string namespaceName,
            string name,
            int podPort,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
