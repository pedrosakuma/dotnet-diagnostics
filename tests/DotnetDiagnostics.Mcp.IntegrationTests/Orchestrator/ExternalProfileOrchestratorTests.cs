using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnostics.Mcp.Observability;
using DotnetDiagnostics.Mcp.Orchestrator;
using DotnetDiagnostics.Mcp.Orchestrator.Investigations;
using DotnetDiagnostics.Mcp.Security;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotnetDiagnostics.Mcp.IntegrationTests.Orchestrator;

/// <summary>
/// Unit and integration tests for the external-profile orchestrator surface (issue #711):
/// <c>list_orchestrator(kind="external-profiles")</c>,
/// <c>attach_to_pod(profileName=…)</c>, and <c>detach_from_pod</c> transport-aware summary.
/// </summary>
public sealed class ExternalProfileOrchestratorTests
{
    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static OrchestratorOptions OptionsWithProfile(string name = "prod-api", string url = "https://mcp.internal/mcp") =>
        new()
        {
            Enabled = true,
            DefaultNamespace = "ns-a",
            ExternalMcpProfiles =
            {
                [name] = new ExternalMcpProfile
                {
                    Url = url,
                    BearerToken = "super-secret-token",
                    ConnectTimeoutSeconds = 5,
                    MaxConcurrency = 2,
                },
            },
        };

    // ─── ExternalProfileAttachOrchestrator unit tests ─────────────────────────

    [Fact]
    public async Task AttachAsync_NullProfileName_ThrowsArgumentException()
    {
        var fx = new OrchestratorFixture();
        var sut = fx.CreateAttachOrchestrator();
        var request = new ExternalProfileAttachRequest(
            ProfileName: null!,
            TtlSeconds: null,
            AllowReuseExistingSession: true,
            OwnerBearerName: "caller",
            OwnerPrincipalKey: "key");

        await sut.Invoking(o => o.AttachAsync(request, CancellationToken.None))
            .Should().ThrowAsync<OrchestratorException>()
            .Where(ex => ex.ErrorKind == OrchestratorErrorKinds.InvalidArgument);
    }

    [Fact]
    public async Task AttachAsync_UnknownProfileName_ThrowsExternalMcpProfileInvalid()
    {
        var fx = new OrchestratorFixture();
        var sut = fx.CreateAttachOrchestrator();
        var request = new ExternalProfileAttachRequest(
            ProfileName: "does-not-exist",
            TtlSeconds: null,
            AllowReuseExistingSession: true,
            OwnerBearerName: "caller",
            OwnerPrincipalKey: "key");

        await sut.Invoking(o => o.AttachAsync(request, CancellationToken.None))
            .Should().ThrowAsync<OrchestratorException>()
            .Where(ex => ex.ErrorKind == OrchestratorErrorKinds.ExternalMcpProfileInvalid);
    }

    [Fact]
    public async Task AttachAsync_TransportFails_MarksHandleFailedAndRethrows()
    {
        var fx = new OrchestratorFixture(transportThrows: true);
        var sut = fx.CreateAttachOrchestrator();
        var request = new ExternalProfileAttachRequest(
            ProfileName: "prod-api",
            TtlSeconds: null,
            AllowReuseExistingSession: true,
            OwnerBearerName: "caller",
            OwnerPrincipalKey: "caller-key");

        var ex = await sut.Invoking(o => o.AttachAsync(request, CancellationToken.None))
            .Should().ThrowAsync<OrchestratorException>();
        ex.Which.ErrorKind.Should().Be(OrchestratorErrorKinds.ExternalMcpConnectFailed);

        // The store should contain a Failed handle — not left in Attaching
        var handles = fx.Store.Snapshot();
        handles.Should().ContainSingle()
            .Which.State.Should().Be(InvestigationState.Failed);
    }

    [Fact]
    public async Task AttachAsync_Success_TransitionsHandleToActive()
    {
        var fx = new OrchestratorFixture();
        var sut = fx.CreateAttachOrchestrator();
        var request = new ExternalProfileAttachRequest(
            ProfileName: "prod-api",
            TtlSeconds: 600,
            AllowReuseExistingSession: false,
            OwnerBearerName: "caller",
            OwnerPrincipalKey: "caller-key");

        var handle = await sut.AttachAsync(request, CancellationToken.None);

        handle.State.Should().Be(InvestigationState.Active);
        handle.ExternalMcp.Should().NotBeNull();
        handle.ExternalMcp!.ProfileName.Should().Be("prod-api");
        // BearerToken must NOT be accessible on the handle's ExternalMcp.Url (stored separately)
        handle.ExternalMcp.Url.Should().NotBeNull();
        // issue #711: Active must only follow a real MCP initialize handshake, not just
        // HttpClient construction.
        fx.Proxy.EnsureInitializedCalled.Should().BeTrue();
    }

    [Fact]
    public async Task AttachAsync_McpInitializeFails_MarksHandleFailedAndDisposesTransport()
    {
        var fx = new OrchestratorFixture(initThrows: true);
        var sut = fx.CreateAttachOrchestrator();
        var request = new ExternalProfileAttachRequest(
            ProfileName: "prod-api",
            TtlSeconds: null,
            AllowReuseExistingSession: true,
            OwnerBearerName: "caller",
            OwnerPrincipalKey: "caller-key");

        var ex = await sut.Invoking(o => o.AttachAsync(request, CancellationToken.None))
            .Should().ThrowAsync<OrchestratorException>();
        ex.Which.ErrorKind.Should().Be(OrchestratorErrorKinds.ExternalMcpConnectFailed);

        var handles = fx.Store.Snapshot();
        handles.Should().ContainSingle()
            .Which.State.Should().Be(InvestigationState.Failed);
        fx.Proxy.EnsureInitializedCalled.Should().BeTrue();
        fx.Proxy.DisposeForHandleCalled.Should().BeTrue();
    }

    [Fact]
    public async Task AttachAsync_ReuseExistingByDifferentOwner_ThrowsPermissionDenied()
    {
        var fx = new OrchestratorFixture();
        var sut = fx.CreateAttachOrchestrator();

        // First attach by owner-A
        var r1 = new ExternalProfileAttachRequest("prod-api", null, true, "owner-a", "key-a");
        await sut.AttachAsync(r1, CancellationToken.None);

        // Second attach by owner-B with reuse=true — targets same profile; owner check fires
        var r2 = new ExternalProfileAttachRequest("prod-api", null, true, "owner-b", "key-b");
        await sut.Invoking(o => o.AttachAsync(r2, CancellationToken.None))
            .Should().ThrowAsync<OrchestratorException>()
            .Where(ex => ex.ErrorKind == OrchestratorErrorKinds.PermissionDenied,
                "a different owner cannot steal or displace the existing active investigation");
    }

    [Fact]
    public async Task AttachAsync_ReuseSameOwner_ReturnsExistingHandle()
    {
        var fx = new OrchestratorFixture();
        var sut = fx.CreateAttachOrchestrator();

        var r1 = new ExternalProfileAttachRequest("prod-api", null, true, "owner-a", "key-a");
        var h1 = await sut.AttachAsync(r1, CancellationToken.None);

        // Same owner re-attaches with reuse=true → must get back the same handle
        var r2 = new ExternalProfileAttachRequest("prod-api", null, true, "owner-a", "key-a");
        var h2 = await sut.AttachAsync(r2, CancellationToken.None);

        h2.HandleId.Should().Be(h1.HandleId, "same-owner reuse returns the original handle");
    }

    // ─── list_orchestrator(kind="external-profiles") tests ────────────────────

    [Fact]
    public async Task ListOrchestrator_ExternalProfiles_ListOnlyScopeIsDenied()
    {
        var options = OptionsWithProfile();
        var result = await ListOrchestratorTool.ListOrchestrator(
            inventory: null!,
            store: null!,
            options: options,
            principalAccessor: TestPrincipalAccessors.WithScopes("orchestrator-list"),
            kubeconfigContext: null!,
            kubeconfigStore: null!,
            kind: ListOrchestratorTool.KindExternalProfiles,
            cancellationToken: CancellationToken.None);

        result.IsError.Should().BeTrue("a list-only token must not enumerate external profiles");
        result.Error!.Kind.Should().Be(OrchestratorErrorKinds.PermissionDenied);
    }

    [Fact]
    public async Task ListOrchestrator_ExternalProfiles_AttachScopeIsGranted()
    {
        var options = OptionsWithProfile();
        var result = await ListOrchestratorTool.ListOrchestrator(
            inventory: null!,
            store: null!,
            options: options,
            principalAccessor: TestPrincipalAccessors.WithScopes("orchestrator-attach"),
            kubeconfigContext: null!,
            kubeconfigStore: null!,
            kind: ListOrchestratorTool.KindExternalProfiles,
            cancellationToken: CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Data.Should().NotBeNull();
        result.Data!.Kind.Should().Be(ListOrchestratorTool.KindExternalProfiles);
        result.Data.ExternalProfiles.Should().NotBeNull();
    }

    [Fact]
    public async Task ListOrchestrator_ExternalProfiles_ReturnsNonSecretMetadata()
    {
        var options = OptionsWithProfile(name: "prod-api", url: "https://mcp.internal/mcp");
        var result = await ListOrchestratorTool.ListOrchestrator(
            inventory: null!,
            store: null!,
            options: options,
            principalAccessor: TestPrincipalAccessors.WithScopes("orchestrator-attach"),
            kubeconfigContext: null!,
            kubeconfigStore: null!,
            kind: ListOrchestratorTool.KindExternalProfiles,
            cancellationToken: CancellationToken.None);

        result.IsError.Should().BeFalse();
        var profiles = result.Data!.ExternalProfiles!.Items;
        profiles.Should().ContainSingle();
        var entry = profiles[0];

        entry.Name.Should().Be("prod-api");
        entry.Url.Should().Be("https://mcp.internal/mcp");
        // BearerToken must NEVER appear in the entry
        // (verified structurally: ExternalProfileEntry has no BearerToken field)
        entry.ConnectTimeoutSeconds.Should().Be(5);
        entry.MaxConcurrency.Should().Be(2);
    }

    [Fact]
    public async Task ListOrchestrator_ExternalProfiles_EmptyWhenNoProfilesConfigured()
    {
        var options = new OrchestratorOptions { Enabled = true };
        var result = await ListOrchestratorTool.ListOrchestrator(
            inventory: null!,
            store: null!,
            options: options,
            principalAccessor: TestPrincipalAccessors.WithScopes("orchestrator-attach"),
            kubeconfigContext: null!,
            kubeconfigStore: null!,
            kind: ListOrchestratorTool.KindExternalProfiles,
            cancellationToken: CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Data!.ExternalProfiles!.Items.Should().BeEmpty();
        result.Summary.Should().Contain("no external MCP profiles");
    }

    [Fact]
    public async Task ListOrchestrator_ExternalProfiles_OrderedByName()
    {
        var options = new OrchestratorOptions { Enabled = true };
        options.ExternalMcpProfiles["z-profile"] = new ExternalMcpProfile { Url = "https://z.internal/mcp" };
        options.ExternalMcpProfiles["a-profile"] = new ExternalMcpProfile { Url = "https://a.internal/mcp" };
        options.ExternalMcpProfiles["m-profile"] = new ExternalMcpProfile { Url = "https://m.internal/mcp" };

        var result = await ListOrchestratorTool.ListOrchestrator(
            inventory: null!,
            store: null!,
            options: options,
            principalAccessor: TestPrincipalAccessors.WithScopes("orchestrator-attach"),
            kubeconfigContext: null!,
            kubeconfigStore: null!,
            kind: ListOrchestratorTool.KindExternalProfiles,
            cancellationToken: CancellationToken.None);

        result.IsError.Should().BeFalse();
        var names = result.Data!.ExternalProfiles!.Items.Select(e => e.Name).ToList();
        names.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task ListOrchestrator_ExternalProfiles_PodsAndInvestigationsAreNull()
    {
        var options = OptionsWithProfile();
        var result = await ListOrchestratorTool.ListOrchestrator(
            inventory: null!,
            store: null!,
            options: options,
            principalAccessor: TestPrincipalAccessors.WithScopes("orchestrator-attach"),
            kubeconfigContext: null!,
            kubeconfigStore: null!,
            kind: ListOrchestratorTool.KindExternalProfiles,
            cancellationToken: CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Data!.Pods.Should().BeNull("only ExternalProfiles field is populated for this kind");
        result.Data.Investigations.Should().BeNull("only ExternalProfiles field is populated for this kind");
    }

    // ─── attach_to_pod(profileName=…) via OrchestratorTools ───────────────────

    [Fact]
    public async Task AttachToPod_ExternalProfile_RootTokenWithoutExplicitAdminScope_ReturnsForbidden()
    {
        var fx = new ToolFixture();
        // Root token has wildcard scope but NOT the explicit orchestrator-admin modifier
        var result = await OrchestratorTools.AttachToPod(
            fx.KubeOrchestrator,
            externalOrchestrator: null!,
            fx.Options,
            fx.Binder,
            fx.Store,
            TestPrincipalAccessors.Root,
            fx.Observability,
            server: null!,
            profileName: "prod-api");

        result.IsError.Should().BeTrue("wildcard/root bearer must not satisfy the explicit orchestrator-admin scope");
        result.Error!.Kind.Should().Be(OrchestratorErrorKinds.PermissionDenied);
        result.Summary.Should().Contain("orchestrator-admin");
    }

    [Fact]
    public async Task AttachToPod_ExternalProfile_NoProfilesConfigured_ReturnsExternalMcpProfileInvalid()
    {
        var fx = new ToolFixture(); // default options has empty ExternalMcpProfiles
        var result = await OrchestratorTools.AttachToPod(
            fx.KubeOrchestrator,
            externalOrchestrator: null!,
            fx.Options,
            fx.Binder,
            fx.Store,
            TestPrincipalAccessors.WithScopes("orchestrator-attach", "orchestrator-admin"),
            fx.Observability,
            server: null!,
            profileName: "any-profile");

        result.IsError.Should().BeTrue("the feature gate fires when no profiles are configured");
        result.Error!.Kind.Should().Be(OrchestratorErrorKinds.ExternalMcpProfileInvalid);
    }

    [Fact]
    public async Task AttachToPod_ExternalProfile_TransportFailure_SurfacesError()
    {
        var fx = new ToolFixture(withProfile: true, transportThrows: true);
        var result = await OrchestratorTools.AttachToPod(
            fx.KubeOrchestrator,
            fx.ExternalOrchestrator,
            fx.Options,
            fx.Binder,
            fx.Store,
            TestPrincipalAccessors.WithScopes("orchestrator-attach", "orchestrator-admin"),
            fx.Observability,
            server: null!,
            profileName: "prod-api");

        result.IsError.Should().BeTrue();
        result.Error!.Kind.Should().Be(OrchestratorErrorKinds.ExternalMcpConnectFailed);
    }

    [Fact]
    public async Task AttachToPod_ExternalProfile_Success_AttachSessionPopulatesProfileName()
    {
        var fx = new ToolFixture(withProfile: true);
        var result = await OrchestratorTools.AttachToPod(
            fx.KubeOrchestrator,
            fx.ExternalOrchestrator,
            fx.Options,
            fx.Binder,
            fx.Store,
            TestPrincipalAccessors.WithScopes("orchestrator-attach", "orchestrator-admin"),
            fx.Observability,
            server: null!,
            profileName: "prod-api");

        result.IsError.Should().BeFalse();
        result.Data.Should().NotBeNull();
        result.Data!.ProfileName.Should().Be("prod-api");
        result.Data.State.Should().Be(InvestigationState.Active);
        result.Data.ProxyBaseUrl.Should().BeNull("external handles route without URL rewriting");
        result.Summary.Should().ContainEquivalentOf("external investigation");
        result.Summary.Should().Contain("prod-api");
    }

    [Fact]
    public async Task AttachToPod_ExternalProfile_AuthDisabled_SucceedsWithoutAdminScope()
    {
        // When auth is disabled (null principal), the admin scope check is skipped.
        var fx = new ToolFixture(withProfile: true);
        var result = await OrchestratorTools.AttachToPod(
            fx.KubeOrchestrator,
            fx.ExternalOrchestrator,
            fx.Options,
            fx.Binder,
            fx.Store,
            TestPrincipalAccessors.Anonymous,  // null principal = auth disabled
            fx.Observability,
            server: null!,
            profileName: "prod-api");

        result.IsError.Should().BeFalse("null principal bypasses all scope checks");
    }

    // ─── detach_from_pod transport-aware summary ──────────────────────────────

    [Fact]
    public async Task DetachFromPod_ExternalHandle_SummaryMentionsExternalTransport()
    {
        var fx = new ToolFixture(withProfile: true);
        var externalHandle = new InvestigationHandle(
            HandleId: "ext-h1",
            Kubernetes: null,
            State: InvestigationState.Active,
            AttachedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30),
            ExternalMcp: new ExternalMcpInvestigationTarget(
                ProfileName: "prod-api",
                Url: new Uri("https://mcp.internal/mcp"),
                BearerToken: null));
        fx.Store.Add(externalHandle);

        var result = await OrchestratorTools.DetachFromPod(
            fx.Closer, fx.Binder, fx.Store, fx.Options,
            TestPrincipalAccessors.Root, fx.Observability, server: null!,
            handleId: "ext-h1");

        result.IsError.Should().BeFalse();
        result.Summary.Should().Contain("external transport released",
            "detach summary must be transport-aware for external handles");
        result.Summary.Should().NotContain("ephemeral container",
            "Pod language must not appear in external handle summaries");
    }

    [Fact]
    public async Task DetachFromPod_KubernetesHandle_SummaryMentionsEphemeralContainer()
    {
        var fx = new ToolFixture();
        var k8sHandle = new InvestigationHandle(
            HandleId: "k8s-h1",
            Kubernetes: new KubernetesInvestigationTarget("ns", "pod", "app", "diag", "secret"),
            State: InvestigationState.Active,
            AttachedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30));
        fx.Store.Add(k8sHandle);

        var result = await OrchestratorTools.DetachFromPod(
            fx.Closer, fx.Binder, fx.Store, fx.Options,
            TestPrincipalAccessors.Root, fx.Observability, server: null!,
            handleId: "k8s-h1");

        result.IsError.Should().BeFalse();
        result.Summary.Should().Contain("ephemeral container",
            "Kubernetes detach summary must mention the ephemeral container constraint");
        result.Summary.Should().NotContain("external transport",
            "External-transport language must not appear in Kubernetes handle summaries");
    }

    // ─── AttachSession.FromHandle ─────────────────────────────────────────────

    [Fact]
    public void AttachSession_FromExternalHandle_PopulatesProfileName()
    {
        var handle = new InvestigationHandle(
            HandleId: "ext-h",
            Kubernetes: null,
            State: InvestigationState.Active,
            AttachedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30),
            ExternalMcp: new ExternalMcpInvestigationTarget(
                ProfileName: "my-profile",
                Url: new Uri("https://mcp.example.com/mcp"),
                BearerToken: null));

        var session = AttachSession.FromHandle(handle, proxyBaseUrl: null);

        session.ProfileName.Should().Be("my-profile");
        session.ProxyBaseUrl.Should().BeNull();
    }

    [Fact]
    public void AttachSession_FromKubernetesHandle_ProfileNameIsNull()
    {
        var handle = new InvestigationHandle(
            HandleId: "k8s-h",
            Kubernetes: new KubernetesInvestigationTarget("ns", "pod", "app", "diag", "secret"),
            State: InvestigationState.Active,
            AttachedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30));

        var session = AttachSession.FromHandle(handle, proxyBaseUrl: "/proxy/k8s-h");

        session.ProfileName.Should().BeNull();
        session.ProxyBaseUrl.Should().Be("/proxy/k8s-h");
    }

    // ─── Fixtures ─────────────────────────────────────────────────────────────

    private sealed class OrchestratorFixture
    {
        private readonly bool _transportThrows;
        private readonly bool _initThrows;

        public OrchestratorFixture(bool transportThrows = false, bool initThrows = false)
        {
            _transportThrows = transportThrows;
            _initThrows = initThrows;
        }

        public MemoryInvestigationStore Store { get; } = new();
        public StubProxyClient Proxy { get; private set; } = new();

        private OrchestratorOptions Options { get; } = OptionsWithProfile();

        public ExternalProfileAttachOrchestrator CreateAttachOrchestrator()
        {
            Proxy = new StubProxyClient(_initThrows);
            return new(Store, new StubTransportManager(_transportThrows), Proxy, Options, NullLogger<ExternalProfileAttachOrchestrator>.Instance);
        }

        private static OrchestratorOptions OptionsWithProfile() =>
            ExternalProfileOrchestratorTests.OptionsWithProfile();
    }

    private sealed class ToolFixture
    {
        public MemoryInvestigationStore Store { get; } = new();
        public MemoryInvestigationSessionBinder Binder { get; } = new();
        public OrchestratorOptions Options { get; }
        public OrchestratorObservability Observability { get; }
        public InvestigationCloser Closer { get; }
        public IExternalProfileAttachOrchestrator ExternalOrchestrator { get; }
        public NoOpKubePodOrchestrator KubeOrchestrator { get; } = new();

        public ToolFixture(bool withProfile = false, bool transportThrows = false)
        {
            Options = withProfile ? OptionsWithProfile() : new OrchestratorOptions { Enabled = true };
            var services = new ServiceCollection();
            services.AddMetrics();
            var provider = services.BuildServiceProvider();
            Observability = new OrchestratorObservability(
                provider.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(),
                Store,
                new AuditLogWriter(TextWriter.Null));
            Closer = new InvestigationCloser(Store, new NoopProxy(), new NoopPortForward(), Binder);
            ExternalOrchestrator = new ExternalProfileAttachOrchestrator(
                Store,
                new StubTransportManager(transportThrows),
                new StubProxyClient(),
                Options,
                NullLogger<ExternalProfileAttachOrchestrator>.Instance);
        }
    }

    private sealed class StubTransportManager : IInvestigationTransportManager
    {
        private readonly bool _throws;

        public StubTransportManager(bool throws = false) { _throws = throws; }

        public Task<HttpClient> GetOrCreateClientAsync(InvestigationHandle handle, CancellationToken cancellationToken)
        {
            if (_throws)
            {
                throw new OrchestratorException(
                    OrchestratorErrorKinds.ExternalMcpConnectFailed,
                    "Stub transport failed to connect.");
            }
            return Task.FromResult(new HttpClient());
        }

        public Task CloseAsync(string handleId) => Task.CompletedTask;
    }

    private sealed class NoOpKubePodOrchestrator : IPodAttachOrchestrator
    {
        public Task<InvestigationHandle> AttachAsync(AttachRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Kubernetes attach should not be called in external-profile tests.");
    }

    private sealed class NoopProxy : IInvestigationProxyClient
    {
        public Task<ModelContextProtocol.Protocol.CallToolResult> CallToolAsync(InvestigationHandle handle, ModelContextProtocol.Protocol.CallToolRequestParams request, CancellationToken cancellationToken)
            => Task.FromResult(new ModelContextProtocol.Protocol.CallToolResult());
        public Task EnsureInitializedAsync(InvestigationHandle handle, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DisposeForHandleAsync(string handleId) => Task.CompletedTask;
    }

    /// <summary>
    /// Stub proxy client used to unit-test <see cref="ExternalProfileAttachOrchestrator"/>
    /// without a real MCP transport. Tracks whether <see cref="EnsureInitializedAsync"/>
    /// and <see cref="DisposeForHandleAsync"/> were invoked so tests can assert the
    /// attach orchestrator actually performs (and cleans up after) the handshake.
    /// </summary>
    private sealed class StubProxyClient : IInvestigationProxyClient
    {
        private readonly bool _initThrows;

        public StubProxyClient(bool initThrows = false) { _initThrows = initThrows; }

        public bool EnsureInitializedCalled { get; private set; }
        public bool DisposeForHandleCalled { get; private set; }

        public Task<ModelContextProtocol.Protocol.CallToolResult> CallToolAsync(InvestigationHandle handle, ModelContextProtocol.Protocol.CallToolRequestParams request, CancellationToken cancellationToken)
            => Task.FromResult(new ModelContextProtocol.Protocol.CallToolResult());

        public Task EnsureInitializedAsync(InvestigationHandle handle, CancellationToken cancellationToken)
        {
            EnsureInitializedCalled = true;
            if (_initThrows)
            {
                throw new OrchestratorException(
                    OrchestratorErrorKinds.ExternalMcpConnectFailed,
                    "Stub proxy failed the MCP initialize handshake.");
            }
            return Task.CompletedTask;
        }

        public Task DisposeForHandleAsync(string handleId)
        {
            DisposeForHandleCalled = true;
            return Task.CompletedTask;
        }
    }

    private sealed class NoopPortForward : IPortForwardManager
    {
        public Task<HttpClient> GetOrCreateClientAsync(InvestigationHandle handle, CancellationToken cancellationToken)
            => Task.FromResult(new HttpClient());
        public Task CloseAsync(string handleId) => Task.CompletedTask;
    }
}
