using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using DotnetDiagnostics.Mcp.Orchestrator;
using DotnetDiagnostics.Mcp.Orchestrator.Investigations;
using DotnetDiagnostics.Mcp.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DotnetDiagnostics.Mcp.IntegrationTests.Orchestrator.Investigations;

[Collection(nameof(EnvSerial))]
public sealed class InvestigationProxyTaskIntegrationTests
{
    [Fact]
    public async Task BoundModifierCall_UsesOuterTaskLifecycle_AndDoesNotLeakPrincipal()
    {
        await using var factory = new TaskProxyFactory();
        await using var client = await ConnectAsync(factory, TaskProxyFactory.AuthorizedToken);
        var arguments = MethodParameterArguments();
        var metadata = new McpTaskMetadata { TimeToLive = TimeSpan.FromMinutes(1) };

        var task = await client.CallToolAsTaskAsync(
            "collect_sample",
            arguments,
            metadata,
            cancellationToken: CancellationToken.None);

        task.TaskId.Should().NotBeNullOrWhiteSpace();
        (await client.ListTasksAsync(cancellationToken: CancellationToken.None))
            .Select(static item => item.TaskId)
            .Should().Contain(task.TaskId);
        task = await WaitForTerminalAsync(client, task);
        task.Status.Should().Be(McpTaskStatus.Completed);

        var rawResult = await client.GetTaskResultAsync(
            task.TaskId,
            cancellationToken: CancellationToken.None);
        var result = JsonSerializer.Deserialize<CallToolResult>(
            rawResult.GetRawText(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        ResultText(result).Should().Be("pod-call-completed");

        factory.Proxy.CallCount.Should().Be(1);
        factory.Proxy.LastPrincipalName.Should().Be(TaskProxyFactory.AuthorizedName);
        factory.Proxy.LastRequest!.Task.Should().BeNull(
            "the pod must execute synchronously inside the orchestrator-owned task");
        factory.Proxy.LastRequest.Arguments.Should().ContainKey(ToolScopeDelegation.ArgumentName);

        await using var underScopedClient = await ConnectAsync(factory, TaskProxyFactory.UnderScopedToken);
        var denied = await underScopedClient.CallToolAsync(
            "collect_sample",
            arguments,
            cancellationToken: CancellationToken.None);

        denied.IsError.Should().BeTrue();
        ResultText(denied).Should().Contain("sensitive-parameter-read");
        factory.Proxy.CallCount.Should().Be(1,
            "the prior task's ambient principal must not leak into a later request");
    }

    [Fact]
    public async Task ExplicitHandleExportTask_UsesOuterTaskAndExactDelegation()
    {
        await using var factory = new TaskProxyFactory();
        await using var client = await ConnectAsync(factory, TaskProxyFactory.ExportToken);
        var task = await client.CallToolAsTaskAsync(
            "export_investigation_summary",
            ExportArguments(),
            new McpTaskMetadata { TimeToLive = TimeSpan.FromMinutes(1) },
            cancellationToken: CancellationToken.None);

        task = await WaitForTerminalAsync(client, task);
        task.Status.Should().Be(McpTaskStatus.Completed);
        var rawResult = await client.GetTaskResultAsync(
            task.TaskId,
            cancellationToken: CancellationToken.None);
        var result = JsonSerializer.Deserialize<CallToolResult>(
            rawResult.GetRawText(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        ResultText(result).Should().Be("pod-call-completed");

        factory.Proxy.CallCount.Should().Be(1);
        factory.Proxy.LastRequest!.Task.Should().BeNull();
        factory.Proxy.LastDelegatedScopes.Should().BeEquivalentTo(
            "investigation-export",
            "read-counters");

        await using var underScopedClient = await ConnectAsync(factory, TaskProxyFactory.UnderScopedToken);
        var denied = await underScopedClient.CallToolAsync(
            "export_investigation_summary",
            ExportArguments(),
            cancellationToken: CancellationToken.None);

        denied.IsError.Should().BeTrue();
        ResultText(denied).Should().Contain("investigation-export");
        factory.Proxy.CallCount.Should().Be(1,
            "local authorization must deny before proxy routing and must not inherit the task delegation");
    }

    [Fact]
    public async Task BoundTask_CancelTargetsOuterTask_AndCancelsPodInvocation()
    {
        await using var factory = new TaskProxyFactory();
        factory.Proxy.BlockNextCall = true;
        await using var client = await ConnectAsync(factory, TaskProxyFactory.AuthorizedToken);

        var task = await client.CallToolAsTaskAsync(
            "collect_sample",
            MethodParameterArguments(),
            new McpTaskMetadata { TimeToLive = TimeSpan.FromMinutes(1) },
            cancellationToken: CancellationToken.None);
        await factory.Proxy.CallStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var cancelled = await client.CancelTaskAsync(
            task.TaskId,
            cancellationToken: CancellationToken.None);

        cancelled.Status.Should().Be(McpTaskStatus.Cancelled);
        await factory.Proxy.CallCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        (await client.GetTaskAsync(task.TaskId, cancellationToken: CancellationToken.None))
            .Status.Should().Be(McpTaskStatus.Cancelled);
    }

    [Fact]
    public async Task TaskTtl_IsClampedBeforeExpiryArithmetic()
    {
        await using var factory = new TaskProxyFactory();
        await using var client = await ConnectAsync(factory, TaskProxyFactory.AuthorizedToken);

        var task = await client.CallToolAsTaskAsync(
            "collect_sample",
            MethodParameterArguments(),
            new McpTaskMetadata { TimeToLive = TimeSpan.MaxValue },
            cancellationToken: CancellationToken.None);

        task.TimeToLive.Should().Be(TimeSpan.FromHours(1));
        task = await WaitForTerminalAsync(client, task);
        task.Status.Should().Be(McpTaskStatus.Completed);
        (await client.ListTasksAsync(cancellationToken: CancellationToken.None))
            .Should().Contain(item => item.TaskId == task.TaskId);
    }

    [Fact]
    public async Task PodProxyClient_DoesNotRecreateTransport_AfterHandleClose()
    {
        var ports = new NeverUsedPortForwardManager();
        var services = new ServiceCollection().BuildServiceProvider();
        await using var proxy = new PodLocalInvestigationProxyClient(
            ports,
            ToolScopeRegistry.Build(DotnetDiagnostics.Mcp.Hosting.PodLocalToolSurfaces.Proxyable),
            TestPrincipalAccessors.WithScopes("read-counters"),
            services);
        var handle = new InvestigationHandle(
            "closed-handle",
            "ns",
            "pod",
            "app",
            "diag",
            "pod-token",
            InvestigationState.Active,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(10),
            InternalScopeDelegationKey: "closed-handle-key");
        await proxy.DisposeForHandleAsync(handle.HandleId);

        var act = async () => await proxy.CallToolAsync(
            handle,
            new CallToolRequestParams
            {
                Name = "collect_events",
                Arguments = new Dictionary<string, JsonElement>
                {
                    ["kind"] = JsonSerializer.SerializeToElement("counters"),
                },
            },
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*has been closed*");
        ports.GetCalls.Should().Be(0);
    }

    private static async Task<McpTask> WaitForTerminalAsync(McpClient client, McpTask task)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (task.Status is McpTaskStatus.Working && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(task.PollInterval ?? TimeSpan.FromMilliseconds(100));
            task = await client.GetTaskAsync(task.TaskId, cancellationToken: CancellationToken.None);
        }

        return task;
    }

    private static Dictionary<string, object?> MethodParameterArguments()
        => new(StringComparer.Ordinal)
        {
            ["kind"] = "method-params",
            ["methodFilters"] = new[] { "Example.Type::Method" },
            ["includeSensitiveValues"] = true,
            ["reason"] = "proxy task authorization regression",
            ["durationSeconds"] = 1,
            [InvestigationRoutingArguments.InvestigationHandleIdArgument] = TaskProxyFactory.HandleId,
        };

    private static Dictionary<string, object?> ExportArguments()
        => new(StringComparer.Ordinal)
        {
            ["handle"] = "opaque-counter-handle",
            [InvestigationRoutingArguments.InvestigationHandleIdArgument] = TaskProxyFactory.ExportHandleId,
        };

    private static async Task<McpClient> ConnectAsync(TaskProxyFactory factory, string token)
    {
        var httpClient = factory.CreateClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await McpClient.CreateAsync(
            new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp,
                },
                httpClient,
                ownsHttpClient: true),
            cancellationToken: CancellationToken.None);
    }

    private static string ResultText(CallToolResult result)
        => string.Join(
            "\n",
            result.Content.OfType<TextContentBlock>().Select(static content => content.Text));

    private sealed class TaskProxyFactory : WebApplicationFactory<Program>
    {
        internal const string HandleId = "inv-task-707";
        internal const string ExportHandleId = "inv-export-task-707";
        internal const string AuthorizedName = "task-caller";
        internal const string ExportName = "export-task-caller";
        internal const string AuthorizedToken = "task-caller-token";
        internal const string ExportToken = "export-task-caller-token";
        internal const string UnderScopedToken = "under-scoped-token";

        internal ProbeProxyClient Proxy { get; } = new();
        private TaskInvestigationStore Store { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Environment.SetEnvironmentVariable("MCP_BEARER_TOKEN", null);
            builder.UseSetting("Orchestrator:Enabled", "true");
            builder.UseSetting("Diagnostics:AllowMethodParameterCapture", "true");
            builder.UseSetting("Auth:BearerTokens:0:Name", AuthorizedName);
            builder.UseSetting("Auth:BearerTokens:0:Token", AuthorizedToken);
            builder.UseSetting("Auth:BearerTokens:0:Scopes:0", "eventpipe");
            builder.UseSetting("Auth:BearerTokens:0:Scopes:1", "sensitive-parameter-read");
            builder.UseSetting("Auth:BearerTokens:1:Name", "under-scoped");
            builder.UseSetting("Auth:BearerTokens:1:Token", UnderScopedToken);
            builder.UseSetting("Auth:BearerTokens:1:Scopes:0", "eventpipe");
            builder.UseSetting("Auth:BearerTokens:2:Name", ExportName);
            builder.UseSetting("Auth:BearerTokens:2:Token", ExportToken);
            builder.UseSetting("Auth:BearerTokens:2:Scopes:0", "investigation-export");
            builder.UseSetting("Auth:BearerTokens:2:Scopes:1", "read-counters");
            builder.ConfigureTestServices(services =>
            {
                Store.Add(new InvestigationHandle(
                    HandleId,
                    "ns",
                    "pod",
                    "app",
                    "diag",
                    "pod-token",
                    InvestigationState.Active,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow.AddMinutes(10),
                    OwnerBearerName: AuthorizedName,
                    OwnerPrincipalKey: PrincipalOwnershipKey.ForOpaqueEntry("Auth:BearerTokens:0"),
                    InternalScopeDelegationKey: "task-proxy-delegation-key"));
                Store.Add(new InvestigationHandle(
                    ExportHandleId,
                    "ns",
                    "pod",
                    "app",
                    "diag-export",
                    "pod-token",
                    InvestigationState.Active,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow.AddMinutes(10),
                    OwnerBearerName: ExportName,
                    OwnerPrincipalKey: PrincipalOwnershipKey.ForOpaqueEntry("Auth:BearerTokens:2"),
                    InternalScopeDelegationKey: "export-task-proxy-delegation-key"));
                services.RemoveAll<IInvestigationStore>();
                services.AddSingleton<IInvestigationStore>(Store);
                services.RemoveAll<IInvestigationProxyClient>();
                services.AddSingleton<IInvestigationProxyClient>(sp =>
                {
                    Proxy.PrincipalAccessor = sp.GetRequiredService<IPrincipalAccessor>();
                    return Proxy;
                });
            });
            base.ConfigureWebHost(builder);
        }
    }

    private sealed class ProbeProxyClient : IInvestigationProxyClient
    {
        internal IPrincipalAccessor? PrincipalAccessor { get; set; }
        internal int CallCount;
        internal string? LastPrincipalName;
        internal CallToolRequestParams? LastRequest;
        internal IReadOnlyCollection<string>? LastDelegatedScopes;
        internal bool BlockNextCall;
        internal TaskCompletionSource CallStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CallCancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CallToolResult> CallToolAsync(
            InvestigationHandle handle,
            CallToolRequestParams request,
            CancellationToken cancellationToken)
        {
            var principal = PrincipalAccessor?.Current
                ?? throw new InvalidOperationException("The promoted task lost its authorized caller principal.");
            if (string.Equals(request.Name, "collect_sample", StringComparison.Ordinal) &&
                !principal.HasExplicitScope("sensitive-parameter-read"))
            {
                throw new InvalidOperationException("The promoted task lost its modifier scope.");
            }
            if (string.Equals(request.Name, "export_investigation_summary", StringComparison.Ordinal))
            {
                ToolScopeDelegation.TryConsume(
                    request,
                    ToolScopeRegistry.Build(DotnetDiagnostics.Mcp.Hosting.PodLocalToolSurfaces.Proxyable),
                    new ToolScopeResolutionPolicies(null, null, null, null),
                    handle.InternalScopeDelegationKey,
                    TimeProvider.System,
                    out var delegatedPrincipal,
                    out var failure).Should().BeTrue(failure);
                LastDelegatedScopes = delegatedPrincipal!.Scopes;
            }

            Interlocked.Increment(ref CallCount);
            LastPrincipalName = principal.Name;
            LastRequest = request;
            CallStarted.TrySetResult();

            if (BlockNextCall)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    CallCancelled.TrySetResult();
                    throw;
                }
            }

            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = "pod-call-completed" }],
            };
        }

        public Task DisposeForHandleAsync(string handleId) => Task.CompletedTask;
    }

    private sealed class TaskInvestigationStore : IInvestigationStore, IInvestigationStoreActivation
    {
        private readonly ConcurrentDictionary<string, InvestigationHandle> _handles =
            new(StringComparer.Ordinal);

        public void Add(InvestigationHandle handle) => _handles[handle.HandleId] = handle;

        public bool TryReserveTarget(
            InvestigationHandle newHandle,
            bool allowReuse,
            out InvestigationHandle? existing)
        {
            existing = null;
            return _handles.TryAdd(newHandle.HandleId, newHandle);
        }

        public void Update(InvestigationHandle handle) => _handles[handle.HandleId] = handle;

        public bool TryTransitionToActive(string handleId, out InvestigationHandle? active)
        {
            if (!_handles.TryGetValue(handleId, out var current) ||
                current.State != InvestigationState.Attaching)
            {
                active = null;
                return false;
            }

            active = current with { State = InvestigationState.Active };
            _handles[handleId] = active;
            return true;
        }

        public InvestigationHandle? GetById(string handleId)
            => _handles.TryGetValue(handleId, out var handle) ? handle : null;

        public InvestigationTerminalTransition TryTransitionToTerminal(
            string handleId,
            InvestigationState targetState,
            string? failureReason,
            out InvestigationState? previousState)
        {
            previousState = null;
            if (!_handles.TryGetValue(handleId, out var current))
            {
                return InvestigationTerminalTransition.NotFound;
            }

            previousState = current.State;
            if (current.State is InvestigationState.Closed or InvestigationState.Expired or InvestigationState.Failed)
            {
                return InvestigationTerminalTransition.AlreadyTerminal;
            }

            _handles[handleId] = current with { State = targetState, FailureReason = failureReason };
            return InvestigationTerminalTransition.Transitioned;
        }

        public InvestigationHandle? FindReusableTarget(
            string podNamespace,
            string podName,
            string containerName)
            => null;

        public IReadOnlyCollection<InvestigationHandle> Snapshot() => _handles.Values.ToArray();
    }

    private sealed class NeverUsedPortForwardManager : IPortForwardManager
    {
        internal int GetCalls;

        public Task<HttpClient> GetOrCreateClientAsync(
            InvestigationHandle handle,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref GetCalls);
            throw new InvalidOperationException("A closed handle must not recreate its transport.");
        }

        public Task CloseAsync(string handleId) => Task.CompletedTask;
    }
}
