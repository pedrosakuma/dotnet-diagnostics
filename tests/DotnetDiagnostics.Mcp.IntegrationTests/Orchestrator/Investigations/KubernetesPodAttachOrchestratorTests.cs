using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnostics.Mcp.Observability;
using DotnetDiagnostics.Mcp.Orchestrator;
using DotnetDiagnostics.Mcp.Orchestrator.Investigations;
using DotnetDiagnostics.Mcp.Security;
using FluentAssertions;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotnetDiagnostics.Mcp.IntegrationTests.Orchestrator.Investigations;

public class KubernetesPodAttachOrchestratorTests
{
    private const string Ns = "diagnosticsmcp";
    private const string Pod = "api-0";
    private const string Container = "app";

    [Fact]
    public async Task AttachAsync_ReturnsActiveHandle_OnHappyPath()
    {
        var api = new StubAttachApi(
            pod: BuildPreparedPod(),
            ephemeralRunningAfter: 1);
        var (orch, store, options) = NewOrchestrator(api);

        var handle = await orch.AttachAsync(NewRequest(), CancellationToken.None);

        handle.State.Should().Be(InvestigationState.Active);
        handle.Namespace.Should().Be(Ns);
        handle.PodName.Should().Be(Pod);
        handle.TargetContainerName.Should().Be(Container);
        handle.HandleId.Should().StartWith("inv_");
        handle.EphemeralContainerName.Should().StartWith(options.EphemeralContainerNamePrefix);
        handle.Kubernetes!.PodLocalBearerToken.Should().NotBeNullOrWhiteSpace();
        api.PatchInvoked.Should().BeTrue();
        api.PatchedSpec!.Image.Should().Be(options.EphemeralContainerImage);
        api.PatchedSpec.TargetContainerName.Should().Be(Container);
        api.PatchedSpec.Env.Should().Contain(e => e.Name == "MCP_BEARER_TOKEN" && e.Value == handle.Kubernetes!.PodLocalBearerToken);
        api.PatchedSpec.Env.Should().Contain(e =>
            e.Name == ToolScopeDelegation.EnvironmentVariableName &&
            e.Value == handle.InternalScopeDelegationKey);
        api.PatchedSpec.Env.Should().Contain(e => e.Name == "ASPNETCORE_URLS" && e.Value == $"http://0.0.0.0:{options.ProxyPodPort}");
        api.PatchedSpec.Args.Should().Equal("--urls", $"http://0.0.0.0:{options.ProxyPodPort}");
        store.GetById(handle.HandleId).Should().BeSameAs(handle);
    }

    [Fact]
    public async Task AttachAsync_HonoursConfiguredProxyPodPort_InEphemeralContainerEnv()
    {
        var api = new StubAttachApi(pod: BuildPreparedPod(), ephemeralRunningAfter: 1);
        var (orch, _, options) = NewOrchestrator(api);
        options.ProxyPodPort = 18888;

        await orch.AttachAsync(NewRequest(), CancellationToken.None);

        api.PatchedSpec!.Env.Should().Contain(e => e.Name == "ASPNETCORE_URLS" && e.Value == "http://0.0.0.0:18888");
        api.PatchedSpec.Args.Should().Equal("--urls", "http://0.0.0.0:18888");
    }

    [Fact]
    public async Task AttachAsync_Propagates_Authorization_Alternative_Policies_ToPod()
    {
        var api = new StubAttachApi(pod: BuildPreparedPod(), ephemeralRunningAfter: 1);
        var security = new DotnetDiagnostics.Core.Security.SecurityOptions
        {
            AllowSensitiveHeapValues = true,
            AllowMethodParameterCapture = true,
            SymbolServerAllowlist = ["symbols.example.test"],
            EventSourceAllowlist = ["Custom.Provider"],
        };
        var (orch, _, _) = NewOrchestrator(api, securityOptions: security);

        await orch.AttachAsync(NewRequest(), CancellationToken.None);

        api.PatchedSpec!.Env.Should().Contain(e =>
            e.Name == "Diagnostics__AllowSensitiveHeapValues" && e.Value == "True");
        api.PatchedSpec.Env.Should().Contain(e =>
            e.Name == "Diagnostics__AllowMethodParameterCapture" && e.Value == "True");
        api.PatchedSpec.Env.Should().Contain(e =>
            e.Name == "Diagnostics__SymbolServerAllowlist__0" && e.Value == "symbols.example.test");
        api.PatchedSpec.Env.Should().Contain(e =>
            e.Name == "Diagnostics__EventSourceAllowlist__0" && e.Value == "Custom.Provider");
    }

    [Fact]
    public async Task AttachAsync_InheritsTargetVolumeMounts_SoSharedTmpSocketIsVisible()
    {
        // Regression guard for the central topology: without this the ephemeral
        // container would have its own /tmp and the diagnostic IPC socket created
        // by the target's runtime at /tmp/dotnet-diagnostic-<pid> would be
        // invisible to it, breaking list_dotnet_processes through the proxy.
        var pod = BuildPreparedPod();
        pod.Spec!.Containers[0].VolumeMounts = new List<V1VolumeMount>
        {
            new() { Name = "diag-tmp", MountPath = "/tmp" },
            new() { Name = "ro-config", MountPath = "/config", ReadOnlyProperty = true },
        };
        var api = new StubAttachApi(pod: pod, ephemeralRunningAfter: 1);
        var (orch, _, _) = NewOrchestrator(api);

        await orch.AttachAsync(NewRequest(), CancellationToken.None);

        api.PatchedSpec!.VolumeMounts.Should().NotBeNull();
        api.PatchedSpec.VolumeMounts.Should().HaveCount(2);
        api.PatchedSpec.VolumeMounts!.Should().ContainSingle(v =>
            v.Name == "diag-tmp" && v.MountPath == "/tmp");
        api.PatchedSpec.VolumeMounts!.Should().ContainSingle(v =>
            v.Name == "ro-config" && v.MountPath == "/config" && v.ReadOnlyProperty == true);
    }

    [Fact]
    public async Task AttachAsync_TargetWithoutVolumeMounts_LeavesEphemeralVolumeMountsNull()
    {
        var pod = BuildPreparedPod();
        pod.Spec!.Containers[0].VolumeMounts = null;
        var api = new StubAttachApi(pod: pod, ephemeralRunningAfter: 1);
        var (orch, _, _) = NewOrchestrator(api);

        await orch.AttachAsync(NewRequest(), CancellationToken.None);

        api.PatchedSpec!.VolumeMounts.Should().BeNull(
            "container-level security context is optional and so is volumeMounts");
    }

    [Fact]
    public async Task AttachAsync_InheritsTargetSecurityContext_RunAsUserAndGroup()
    {
        // The ephemeral container must run as the same UID as the target so the
        // diagnostic IPC socket file (mode 0600 owned by the runtime's effective
        // uid) is readable. It also inherits non-elevating restrictions
        // (allowPrivilegeEscalation=false, capability drops, seccomp profile,
        // MAC contexts) so it survives Pod Security "restricted" admission.
        // Privileged=true and capability adds are intentionally dropped.
        var pod = BuildPreparedPod();
        pod.Spec!.Containers[0].SecurityContext = new V1SecurityContext
        {
            RunAsUser = 10001,
            RunAsGroup = 10001,
            RunAsNonRoot = true,
            Privileged = true, // must be dropped
            AllowPrivilegeEscalation = false,
            Capabilities = new V1Capabilities
            {
                Add = new List<string> { "NET_ADMIN" }, // must be dropped
                Drop = new List<string> { "ALL" },
            },
            SeccompProfile = new V1SeccompProfile { Type = "RuntimeDefault" },
        };
        var api = new StubAttachApi(pod: pod, ephemeralRunningAfter: 1);
        var (orch, _, _) = NewOrchestrator(api);

        await orch.AttachAsync(NewRequest(), CancellationToken.None);

        var ctx = api.PatchedSpec!.SecurityContext;
        ctx.Should().NotBeNull();
        ctx!.RunAsUser.Should().Be(10001);
        ctx.RunAsGroup.Should().Be(10001);
        ctx.RunAsNonRoot.Should().Be(true);
        ctx.Privileged.Should().BeNull(
            "the orchestrator must not silently propagate elevated privileges");
        ctx.AllowPrivilegeEscalation.Should().Be(false,
            "non-elevating restrictions must be inherited for PSS-restricted admission");
        ctx.Capabilities.Should().NotBeNull();
        ctx.Capabilities!.Add.Should().BeNullOrEmpty(
            "capability adds are workload-specific elevations and must not propagate");
        ctx.Capabilities.Drop.Should().BeEquivalentTo(new[] { "ALL" });
        ctx.SeccompProfile.Should().NotBeNull();
        ctx.SeccompProfile!.Type.Should().Be("RuntimeDefault");
    }

    [Fact]
    public async Task AttachAsync_ThrowsPodNotFound_WhenApiReturns404()
    {
        var api = new StubAttachApi(readPodException: NewHttpEx(HttpStatusCode.NotFound));
        var (orch, _, _) = NewOrchestrator(api);

        var act = () => orch.AttachAsync(NewRequest(), CancellationToken.None);

        var ex = await act.Should().ThrowAsync<OrchestratorException>();
        ex.Which.ErrorKind.Should().Be(OrchestratorErrorKinds.PodNotFound);
    }

    [Fact]
    public async Task AttachAsync_ThrowsContainerNotFound_WhenContainerMissing()
    {
        var api = new StubAttachApi(pod: BuildPreparedPod());
        var (orch, _, _) = NewOrchestrator(api);

        var act = () => orch.AttachAsync(NewRequest(containerName: "does-not-exist"), CancellationToken.None);

        var ex = await act.Should().ThrowAsync<OrchestratorException>();
        ex.Which.ErrorKind.Should().Be(OrchestratorErrorKinds.ContainerNotFound);
    }

    [Fact]
    public async Task AttachAsync_ThrowsPodNotRunning_WhenPhasePending()
    {
        var pod = BuildPreparedPod();
        pod.Status.Phase = "Pending";
        var api = new StubAttachApi(pod: pod);
        var (orch, _, _) = NewOrchestrator(api);

        var act = () => orch.AttachAsync(NewRequest(), CancellationToken.None);

        var ex = await act.Should().ThrowAsync<OrchestratorException>();
        ex.Which.ErrorKind.Should().Be(OrchestratorErrorKinds.PodNotRunning);
    }

    [Fact]
    public async Task AttachAsync_ThrowsPodNotPrepared_WhenLabelMissing()
    {
        var pod = BuildPreparedPod();
        pod.Metadata.Labels = new Dictionary<string, string>(); // drop opt-in label
        var api = new StubAttachApi(pod: pod);
        var (orch, _, _) = NewOrchestrator(api);

        var act = () => orch.AttachAsync(NewRequest(), CancellationToken.None);

        var ex = await act.Should().ThrowAsync<OrchestratorException>();
        ex.Which.ErrorKind.Should().Be(OrchestratorErrorKinds.PodNotPrepared);
    }

    [Fact]
    public async Task AttachAsync_AllowsUnpreparedPod_WhenCallerOptsOut()
    {
        var pod = BuildPreparedPod();
        pod.Metadata.Labels = new Dictionary<string, string>();
        var api = new StubAttachApi(pod: pod, ephemeralRunningAfter: 1);
        var (orch, _, _) = NewOrchestrator(api, requirePreparedLabel: false);

        var handle = await orch.AttachAsync(NewRequest(requirePreparedTarget: false), CancellationToken.None);

        handle.State.Should().Be(InvestigationState.Active);
    }

    [Fact]
    public async Task AttachAsync_ReusesExistingActiveHandle()
    {
        var api = new StubAttachApi(pod: BuildPreparedPod(), ephemeralRunningAfter: 1);
        var (orch, store, _) = NewOrchestrator(api);

        var first = await orch.AttachAsync(NewRequest(), CancellationToken.None);
        api.PatchInvocationCount.Should().Be(1);

        var second = await orch.AttachAsync(NewRequest(), CancellationToken.None);

        second.Should().BeSameAs(first);
        api.PatchInvocationCount.Should().Be(1);
        store.Snapshot().Should().HaveCount(1);
    }

    [Fact]
    public async Task AttachAsync_ReusesExistingHandle_ForSameStableOwner()
    {
        var api = new StubAttachApi(pod: BuildPreparedPod(), ephemeralRunningAfter: 1);
        var (orch, store, _) = NewOrchestrator(api);
        var ownerKey = PrincipalOwnershipKey.ForOpaqueEntry("operator-a");

        var first = await orch.AttachAsync(
            NewRequest(ownerBearerName: "display-a", ownerPrincipalKey: ownerKey),
            CancellationToken.None);
        var second = await orch.AttachAsync(
            NewRequest(ownerBearerName: "renamed-display", ownerPrincipalKey: ownerKey),
            CancellationToken.None);

        second.Should().BeSameAs(first);
        api.PatchInvocationCount.Should().Be(1);
        store.Snapshot().Should().ContainSingle();
    }

    [Fact]
    public async Task AttachAsync_RejectsReuse_WhenDisplayMatchesButOwnershipKeyDiffers()
    {
        var api = new StubAttachApi(pod: BuildPreparedPod(), ephemeralRunningAfter: 1);
        var (orch, _, _) = NewOrchestrator(api);

        await orch.AttachAsync(
            NewRequest(
                ownerBearerName: "shared-display",
                ownerPrincipalKey: PrincipalOwnershipKey.ForOpaqueEntry("operator-a")),
            CancellationToken.None);

        var act = () => orch.AttachAsync(
            NewRequest(
                ownerBearerName: "shared-display",
                ownerPrincipalKey: PrincipalOwnershipKey.ForOpaqueEntry("operator-b")),
            CancellationToken.None);

        (await act.Should().ThrowAsync<OrchestratorException>())
            .Which.ErrorKind.Should().Be(OrchestratorErrorKinds.PermissionDenied);
        api.PatchInvocationCount.Should().Be(1);
    }

    [Fact]
    public async Task AttachAsync_StoresNormalizedTransportNeutralProcessSelector()
    {
        var api = new StubAttachApi(pod: BuildPreparedPod(), ephemeralRunningAfter: 1);
        var (orch, store, _) = NewOrchestrator(api);
        var selector = new InvestigationProcessSelector(
            ManagedEntrypointAssemblyName: "  CoreClrSample  ",
            CommandLineContains: "  --p6-target=a ");

        var handle = await orch.AttachAsync(NewRequest(processSelector: selector), CancellationToken.None);

        handle.ProcessSelector.Should().Be(new InvestigationProcessSelector("CoreClrSample", "--p6-target=a"));
        store.GetById(handle.HandleId)!.ProcessSelector.Should().Be(handle.ProcessSelector);
    }

    [Fact]
    public async Task AttachAsync_RejectsAddingSelectorToReusedHandle()
    {
        var api = new StubAttachApi(pod: BuildPreparedPod(), ephemeralRunningAfter: 1);
        var (orch, _, _) = NewOrchestrator(api);
        var first = await orch.AttachAsync(NewRequest(), CancellationToken.None);
        var selector = new InvestigationProcessSelector(ManagedEntrypointAssemblyName: "CoreClrSample");

        var act = () => orch.AttachAsync(NewRequest(processSelector: selector), CancellationToken.None);

        var ex = await act.Should().ThrowAsync<OrchestratorException>();
        ex.Which.ErrorKind.Should().Be(OrchestratorErrorKinds.InvalidArgument);
        ex.Which.Message.Should().Contain(first.HandleId);
        api.PatchInvocationCount.Should().Be(1);
    }

    [Fact]
    public async Task AttachAsync_RejectsDifferentSelectorOnReusedHandle()
    {
        var api = new StubAttachApi(pod: BuildPreparedPod(), ephemeralRunningAfter: 1);
        var (orch, _, _) = NewOrchestrator(api);
        await orch.AttachAsync(
            NewRequest(processSelector: new InvestigationProcessSelector("Worker.One")),
            CancellationToken.None);

        var act = () => orch.AttachAsync(
            NewRequest(processSelector: new InvestigationProcessSelector("Worker.Two")),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<OrchestratorException>();
        ex.Which.ErrorKind.Should().Be(OrchestratorErrorKinds.InvalidArgument);
        api.PatchInvocationCount.Should().Be(1);
    }

    [Fact]
    public async Task AttachAsync_RejectsEmptyProcessSelector()
    {
        var api = new StubAttachApi(pod: BuildPreparedPod(), ephemeralRunningAfter: 1);
        var (orch, _, _) = NewOrchestrator(api);

        var act = () => orch.AttachAsync(
            NewRequest(processSelector: new InvestigationProcessSelector(" ", "	")),
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<OrchestratorException>();
        ex.Which.ErrorKind.Should().Be(OrchestratorErrorKinds.InvalidArgument);
        api.PatchInvoked.Should().BeFalse();
    }

    [Fact]
    public async Task AttachAsync_LegacyDisplayOwner_FailsReuseClosed()
    {
        var api = new StubAttachApi(pod: BuildPreparedPod(), ephemeralRunningAfter: 1);
        var (orch, _, _) = NewOrchestrator(api);

        await orch.AttachAsync(
            NewRequest(ownerBearerName: "legacy-display"),
            CancellationToken.None);

        var act = () => orch.AttachAsync(
            NewRequest(ownerBearerName: "legacy-display"),
            CancellationToken.None);

        (await act.Should().ThrowAsync<OrchestratorException>())
            .Which.ErrorKind.Should().Be(OrchestratorErrorKinds.PermissionDenied);
    }

    [Fact]
    public async Task AttachAsync_PatchesAgain_WhenReuseDisabled()
    {
        var api = new StubAttachApi(pod: BuildPreparedPod(), ephemeralRunningAfter: 1);
        var (orch, store, _) = NewOrchestrator(api);

        var first = await orch.AttachAsync(NewRequest(), CancellationToken.None);
        var second = await orch.AttachAsync(NewRequest(allowReuseExistingSession: false), CancellationToken.None);

        second.HandleId.Should().NotBe(first.HandleId);
        api.PatchInvocationCount.Should().Be(2);
        store.Snapshot().Should().HaveCount(2);
    }

    [Fact]
    public async Task AttachAsync_ThrowsAttachTimeout_WhenEphemeralNeverRuns()
    {
        var api = new StubAttachApi(pod: BuildPreparedPod()); // never reports running
        var (orch, store, _) = NewOrchestrator(api, attachTimeoutSeconds: 1);

        var act = () => orch.AttachAsync(NewRequest(), CancellationToken.None);

        var ex = await act.Should().ThrowAsync<OrchestratorException>();
        ex.Which.ErrorKind.Should().Be(OrchestratorErrorKinds.AttachTimeout);
        store.Snapshot().Should().ContainSingle(h => h.State == InvestigationState.Failed);
    }

    [Fact]
    public async Task AttachAsync_MapsForbiddenPatch_ToPermissionDenied()
    {
        var api = new StubAttachApi(pod: BuildPreparedPod(), patchException: NewHttpEx(HttpStatusCode.Forbidden));
        var (orch, store, _) = NewOrchestrator(api);

        var act = () => orch.AttachAsync(NewRequest(), CancellationToken.None);

        var ex = await act.Should().ThrowAsync<OrchestratorException>();
        ex.Which.ErrorKind.Should().Be(OrchestratorErrorKinds.PermissionDenied);
        store.Snapshot().Should().ContainSingle(h => h.State == InvestigationState.Failed);
    }

    [Fact]
    public async Task AttachAsync_ThrowsNamespaceNotAllowed_WhenNamespaceMissingFromAllowlist()
    {
        var api = new StubAttachApi(pod: BuildPreparedPod());
        var (orch, _, _) = NewOrchestrator(api);

        var act = () => orch.AttachAsync(NewRequest(@namespace: "kube-system"), CancellationToken.None);

        var ex = await act.Should().ThrowAsync<OrchestratorException>();
        ex.Which.ErrorKind.Should().Be(OrchestratorErrorKinds.NamespaceNotAllowed);
    }

    [Fact]
    public async Task AttachAsync_MapsServerErrorPatch_ToKubeApiUnavailable()
    {
        // 500/503 during the ephemeralcontainers patch must NOT be reported as AttachFailed
        // (which the design reserves for an accepted-but-unhealthy ephemeral container).
        var api = new StubAttachApi(pod: BuildPreparedPod(), patchException: NewHttpEx(HttpStatusCode.InternalServerError));
        var (orch, store, _) = NewOrchestrator(api);

        var act = () => orch.AttachAsync(NewRequest(), CancellationToken.None);

        var ex = await act.Should().ThrowAsync<OrchestratorException>();
        ex.Which.ErrorKind.Should().Be(OrchestratorErrorKinds.KubeApiUnavailable);
        store.Snapshot().Should().ContainSingle(h => h.State == InvestigationState.Failed);
    }

    [Fact]
    public async Task AttachAsync_OnCancellation_TransitionsHandleToFailed()
    {
        // Regression: cancellation must not leave the registered handle stuck in Attaching,
        // otherwise FindReusableTarget would return a permanently-orphaned handle on retry.
        var api = new StubAttachApi(pod: BuildPreparedPod()); // never reports running
        var (orch, store, _) = NewOrchestrator(api, attachTimeoutSeconds: 60);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var act = () => orch.AttachAsync(NewRequest(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        store.Snapshot().Should().ContainSingle(h =>
            h.State == InvestigationState.Failed &&
            h.FailureReason != null &&
            h.FailureReason.Contains("canceled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AttachAsync_DoesNotReviveHandleClosedDuringReadiness()
    {
        var api = new StubAttachApi(pod: BuildPreparedPod(), ephemeralRunningAfter: 1);
        var (orch, store, _) = NewOrchestrator(api);
        api.OnRunningObserved = () =>
        {
            var attaching = store.Snapshot().Single();
            store.TryTransitionToTerminal(
                attaching.HandleId,
                InvestigationState.Closed,
                failureReason: null,
                out _).Should().Be(InvestigationTerminalTransition.Transitioned);
        };

        var act = () => orch.AttachAsync(NewRequest(), CancellationToken.None);

        var error = await act.Should().ThrowAsync<OrchestratorException>();
        error.Which.ErrorKind.Should().Be(OrchestratorErrorKinds.AttachFailed);
        store.Snapshot().Should().ContainSingle(handle => handle.State == InvestigationState.Closed);
    }

    [Fact]
    public void InvestigationHandle_SerializedShape_ExcludesBearerToken()
    {
        // Defence in depth: even if a future caller serializes the internal handle directly,
        // [JsonIgnore] on both internal secrets must keep them out of the wire shape.
        var handle = new InvestigationHandle(
                HandleId: "inv_test",
                Kubernetes: new KubernetesInvestigationTarget(Ns, Pod, Container, "dotnet-dbg-mcp-abcd", "SECRET_TOKEN_VALUE"),
                State: InvestigationState.Active,
            AttachedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30),
            InternalScopeDelegationKey: "SECRET_DELEGATION_VALUE",
            ProcessSelector: new InvestigationProcessSelector("CoreClrSample"));

        var json = System.Text.Json.JsonSerializer.Serialize(handle);

        json.Should().NotContain("SECRET_TOKEN_VALUE");
        json.Should().NotContain("PodLocalBearerToken");
        json.Should().NotContain("SECRET_DELEGATION_VALUE");
        json.Should().NotContain("InternalScopeDelegationKey");
    }

    [Fact]
    public void AttachSession_FromHandle_DropsBearerToken()
    {
        var handle = new InvestigationHandle(
                HandleId: "inv_test",
                Kubernetes: new KubernetesInvestigationTarget(Ns, Pod, Container, "dotnet-dbg-mcp-abcd", "SECRET_TOKEN_VALUE"),
                State: InvestigationState.Active,
            AttachedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30),
            ProcessSelector: new InvestigationProcessSelector("CoreClrSample"));

        var session = AttachSession.FromHandle(handle);
        var json = System.Text.Json.JsonSerializer.Serialize(session);

        session.HandleId.Should().Be(handle.HandleId);
        session.ProcessSelector.Should().Be(handle.ProcessSelector);
        json.Should().NotContain("SECRET_TOKEN_VALUE");
    }

    // ---- stale ephemeral container detection and reuse ----

    [Fact]
    public async Task AttachAsync_ReusesStaleEphemeralContainer_AfterDetachWithinSameProcess()
    {
        // Regression guard for issue #695: after detach_from_pod the ephemeral container
        // keeps running in Kubernetes (containers are immutable once added). A second
        // attach to the same pod must reuse the existing container instead of patching a
        // new one — which would fail with "address already in use" on the shared ProxyPodPort.
        var api = new StubAttachApi(pod: BuildPreparedPod(), ephemeralRunningAfter: 1);
        var (orch, store, _) = NewOrchestrator(api);
        var closer = new InvestigationCloser(store, new NoOpProxyClient(), new NoOpPortForwardManager(),
            new MemoryInvestigationSessionBinder());

        var first = await orch.AttachAsync(NewRequest(), CancellationToken.None);
        first.State.Should().Be(InvestigationState.Active);
        api.PatchInvocationCount.Should().Be(1);

        // Simulate detach_from_pod: transition the handle to Closed.
        await closer.CloseAsync(first.HandleId, InvestigationState.Closed);
        store.GetById(first.HandleId)!.State.Should().Be(InvestigationState.Closed);
        // The K8s pod still has the ephemeral container Running (Kubernetes cannot remove it).
        api.PatchInvocationCount.Should().Be(1); // no extra patch to K8s

        // Reattach: the orchestrator detects the stale Running container and reuses it.
        var second = await orch.AttachAsync(NewRequest(), CancellationToken.None);

        second.State.Should().Be(InvestigationState.Active);
        second.HandleId.Should().NotBe(first.HandleId, "reattach creates a distinct handle");
        second.EphemeralContainerName.Should().Be(first.EphemeralContainerName,
            "the existing running container is reused — no new container was patched");
        second.PodLocalBearerToken.Should().Be(first.PodLocalBearerToken,
            "the running sidecar was started with the old token; reuse must carry it forward");
        api.PatchInvocationCount.Should().Be(1, "no second K8s ephemeral container patch");
        store.Snapshot().Should().HaveCount(2, "original Closed handle + new Active handle");
    }

    [Fact]
    public async Task AttachAsync_ReusesStaleEphemeral_CarriesDelegationKey()
    {
        // The internal scope-delegation key is embedded in the sidecar's environment at
        // startup. Reusing the running container must carry the same key — generating a
        // new one would invalidate the sidecar's HMAC verification for delegated calls.
        var api = new StubAttachApi(pod: BuildPreparedPod(), ephemeralRunningAfter: 1);
        var (orch, store, _) = NewOrchestrator(api);
        var closer = new InvestigationCloser(store, new NoOpProxyClient(), new NoOpPortForwardManager(),
            new MemoryInvestigationSessionBinder());

        var first = await orch.AttachAsync(NewRequest(), CancellationToken.None);
        await closer.CloseAsync(first.HandleId, InvestigationState.Closed);

        var second = await orch.AttachAsync(NewRequest(), CancellationToken.None);

        second.InternalScopeDelegationKey.Should().Be(first.InternalScopeDelegationKey,
            "delegation key embedded in the running sidecar must be preserved on reattach");
    }

    [Fact]
    public async Task AttachAsync_ThrowsEphemeralContainerStale_WhenNoMatchingTerminalHandle()
    {
        // After a server restart the in-memory store is empty. If the pod still has a
        // Running ephemeral container from a previous session, the orchestrator cannot
        // recover the bearer token and must surface a structured error.
        var pod = BuildPreparedPod();
        var prefix = OrchestratorOptions.DefaultEphemeralContainerNamePrefix;
        pod.Status!.EphemeralContainerStatuses = new List<V1ContainerStatus>
        {
            new()
            {
                Name = prefix + "staleaabb",
                Image = "ghcr.io/pedrosakuma/dotnet-diagnostics:0.3.0",
                ImageID = string.Empty,
                Ready = false,
                RestartCount = 0,
                State = new V1ContainerState { Running = new V1ContainerStateRunning() },
            },
        };
        var api = new StubAttachApi(pod: pod);
        var (orch, _, _) = NewOrchestrator(api);

        var act = () => orch.AttachAsync(NewRequest(), CancellationToken.None);

        var ex = await act.Should().ThrowAsync<OrchestratorException>();
        ex.Which.ErrorKind.Should().Be(OrchestratorErrorKinds.EphemeralContainerStale);
        ex.Which.Message.Should().Contain(prefix + "staleaabb");
        api.PatchInvoked.Should().BeFalse("no patch should be attempted when stale state is detected");
    }

    [Fact]
    public async Task AttachAsync_ThrowsPermissionDenied_WhenStaleContainerOwnedByDifferentSession()
    {
        // A stale container belonging to a different MCP session must not be
        // commandeered by a new caller — the old token would run under the old session's
        // identity and the new caller would never own the ephemeral container.
        var api = new StubAttachApi(pod: BuildPreparedPod(), ephemeralRunningAfter: 1);
        var (orch, store, _) = NewOrchestrator(api);
        var closer = new InvestigationCloser(store, new NoOpProxyClient(), new NoOpPortForwardManager(),
            new MemoryInvestigationSessionBinder());
        var ownerA = PrincipalOwnershipKey.ForOpaqueEntry("session-a");
        var ownerB = PrincipalOwnershipKey.ForOpaqueEntry("session-b");

        await orch.AttachAsync(NewRequest(ownerPrincipalKey: ownerA), CancellationToken.None);
        await closer.CloseAsync(store.Snapshot().Single().HandleId, InvestigationState.Closed);

        var act = () => orch.AttachAsync(NewRequest(ownerPrincipalKey: ownerB), CancellationToken.None);

        (await act.Should().ThrowAsync<OrchestratorException>())
            .Which.ErrorKind.Should().Be(OrchestratorErrorKinds.PermissionDenied);
        api.PatchInvocationCount.Should().Be(1, "no second K8s patch attempted");
    }

    [Fact]
    public async Task AttachAsync_FullLifecycle_AttachDetachReattachDetach()
    {
        // End-to-end: attach → detach → reattach (stale reuse) → detach.
        // Verifies the lifecycle contract: ephemeral containers are immutable in K8s;
        // the orchestrator reuses the running container after detach to avoid port conflicts.
        var api = new StubAttachApi(pod: BuildPreparedPod(), ephemeralRunningAfter: 1);
        var (orch, store, _) = NewOrchestrator(api);
        var closer = new InvestigationCloser(store, new NoOpProxyClient(), new NoOpPortForwardManager(),
            new MemoryInvestigationSessionBinder());

        // — Phase 1: attach ————————————————————————————————————————————————————————————
        var h1 = await orch.AttachAsync(NewRequest(), CancellationToken.None);
        h1.State.Should().Be(InvestigationState.Active);
        api.PatchInvocationCount.Should().Be(1);

        // — Phase 2: detach ————————————————————————————————————————————————————————————
        var closeOutcome = await closer.CloseAsync(h1.HandleId, InvestigationState.Closed);
        closeOutcome.NewState.Should().Be(InvestigationState.Closed);
        store.GetById(h1.HandleId)!.State.Should().Be(InvestigationState.Closed);

        // — Phase 3: reattach (stale reuse) ———————————————————————————————————————————
        var h2 = await orch.AttachAsync(NewRequest(), CancellationToken.None);
        h2.State.Should().Be(InvestigationState.Active);
        h2.HandleId.Should().NotBe(h1.HandleId);
        h2.EphemeralContainerName.Should().Be(h1.EphemeralContainerName);
        h2.PodLocalBearerToken.Should().Be(h1.PodLocalBearerToken);
        api.PatchInvocationCount.Should().Be(1, "no second K8s patch — stale container was reused");
        store.Snapshot().Should().HaveCount(2);
        store.Snapshot().Should().Contain(h => h.State == InvestigationState.Closed);  // h1
        store.Snapshot().Should().Contain(h => h.State == InvestigationState.Active);  // h2

        // — Phase 4: collect (verify handle is usable) ————————————————————————————————
        store.GetById(h2.HandleId).Should().NotBeNull();
        h2.PodLocalBearerToken.Should().NotBeNullOrWhiteSpace(
            "the bearer token is required for the proxy to authenticate with the sidecar");

        // — Phase 5: detach again ——————————————————————————————————————————————————————
        var closeOutcome2 = await closer.CloseAsync(h2.HandleId, InvestigationState.Closed);
        closeOutcome2.NewState.Should().Be(InvestigationState.Closed);
        store.GetById(h2.HandleId)!.State.Should().Be(InvestigationState.Closed);
        store.Snapshot().Should().OnlyContain(h => h.State == InvestigationState.Closed);
    }

    [Fact]
    public async Task AttachAsync_SkipsStaleDetection_WhenActiveHandleAlreadyExists()
    {
        // When a live Active handle exists in the store (e.g. two clients for the same
        // pod), the stale-detection path must not run — the existing reuse logic handles
        // it. Regression guard to ensure stale-detection only fires when the store has
        // no Active/Attaching handle for the target.
        var api = new StubAttachApi(pod: BuildPreparedPod(), ephemeralRunningAfter: 1);
        var (orch, store, _) = NewOrchestrator(api);

        // Inject a synthetic stale status so the pod looks like it has a previously
        // detached container. Then verify the orchestrator ignores it because there is
        // already an Active handle in the store.
        var first = await orch.AttachAsync(NewRequest(), CancellationToken.None);
        first.State.Should().Be(InvestigationState.Active);

        // Second attach with an active handle present: must reuse, not stale-detect.
        var second = await orch.AttachAsync(NewRequest(), CancellationToken.None);

        second.Should().BeSameAs(first);
        api.PatchInvocationCount.Should().Be(1);
    }

    // ---- helpers ----

    private static AttachRequest NewRequest(
        string @namespace = Ns,
        string? containerName = null,
        bool requirePreparedTarget = true,
        bool allowReuseExistingSession = true,
        string? ownerBearerName = null,
        string? ownerPrincipalKey = null,
        InvestigationProcessSelector? processSelector = null)
        => new(
            @namespace,
            Pod,
            containerName,
            TtlSeconds: null,
            requirePreparedTarget,
            allowReuseExistingSession,
            ownerBearerName,
            ownerPrincipalKey,
            processSelector);

    private static V1Pod BuildPreparedPod()
        => new()
        {
            Metadata = new V1ObjectMeta
            {
                Name = Pod,
                NamespaceProperty = Ns,
                Labels = new Dictionary<string, string>
                {
                    [OrchestratorOptions.DefaultPreparedLabelKey] = "true",
                },
            },
            Spec = new V1PodSpec
            {
                Containers = new List<V1Container>
                {
                    new() { Name = Container, Image = "myapp:1.0" },
                },
            },
            Status = new V1PodStatus { Phase = "Running" },
        };

    private static (KubernetesPodAttachOrchestrator orch, IInvestigationStore store, OrchestratorOptions options)
        NewOrchestrator(
            StubAttachApi api,
            bool requirePreparedLabel = true,
            int attachTimeoutSeconds = 10,
            DotnetDiagnostics.Core.Security.SecurityOptions? securityOptions = null)
    {
        var options = new OrchestratorOptions
        {
            Enabled = true,
            RequirePreparedLabel = requirePreparedLabel,
            AttachReadinessTimeoutSeconds = attachTimeoutSeconds,
        };
        options.NamespaceAllowlist.Add(Ns);
        var store = new MemoryInvestigationStore();
        var services = new ServiceCollection();
        services.AddMetrics();
        var provider = services.BuildServiceProvider();
        var observability = new OrchestratorObservability(
            provider.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(),
            store,
            new AuditLogWriter(TextWriter.Null));
        var closer = new InvestigationCloser(store, new NoOpProxyClient(), new NoOpPortForwardManager(), new MemoryInvestigationSessionBinder());
        var time = new FakeTimeProvider();
        var orch = new KubernetesPodAttachOrchestrator(
            api,
            store,
            closer,
            observability,
            options,
            securityOptions ?? new DotnetDiagnostics.Core.Security.SecurityOptions(),
            time,
            TimeSpan.FromMilliseconds(1),
            NullLogger<KubernetesPodAttachOrchestrator>.Instance);
        return (orch, store, options);
    }

    private static HttpOperationException NewHttpEx(HttpStatusCode code)
        => new($"HTTP {(int)code}")
        {
            Response = new HttpResponseMessageWrapper(new HttpResponseMessage(code), string.Empty),
        };

    private sealed class StubAttachApi : IKubernetesPodsApi
    {
        private readonly V1Pod? _pod;
        private readonly int _ephemeralRunningAfter;
        private readonly Exception? _readEx;
        private readonly Exception? _patchEx;
        private int _readCount;

        public StubAttachApi(
            V1Pod? pod = null,
            int ephemeralRunningAfter = int.MaxValue,
            Exception? readPodException = null,
            Exception? patchException = null)
        {
            _pod = pod;
            _ephemeralRunningAfter = ephemeralRunningAfter;
            _readEx = readPodException;
            _patchEx = patchException;
        }

        public bool PatchInvoked { get; private set; }
        public int PatchInvocationCount { get; private set; }
        public V1EphemeralContainer? PatchedSpec { get; private set; }
        public Action? OnRunningObserved { get; set; }

        public Task<V1PodList> ListPodsAsync(string? namespaceName, string? labelSelector, string? fieldSelector, int? limit, string? continueToken, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<V1Pod> ReadPodAsync(string namespaceName, string name, CancellationToken cancellationToken)
        {
            _readCount++;
            if (_readEx is not null) throw _readEx;
            if (_pod is null) throw new InvalidOperationException("StubAttachApi configured without a pod.");
            if (PatchedSpec is not null)
            {
                // After patch, the read includes the ephemeral container status; flip to Running on the configured tick.
                var statuses = _pod.Status.EphemeralContainerStatuses ??= new List<V1ContainerStatus>();
                var existing = statuses.FirstOrDefault(s => s.Name == PatchedSpec.Name);
                var state = _readCount >= _ephemeralRunningAfter
                    ? new V1ContainerState { Running = new V1ContainerStateRunning() }
                    : new V1ContainerState { Waiting = new V1ContainerStateWaiting { Reason = "ContainerCreating" } };
                if (state.Running is not null)
                {
                    OnRunningObserved?.Invoke();
                    OnRunningObserved = null;
                }
                if (existing is null)
                {
                    statuses.Add(new V1ContainerStatus
                    {
                        Name = PatchedSpec.Name,
                        Image = PatchedSpec.Image,
                        ImageID = string.Empty,
                        Ready = false,
                        RestartCount = 0,
                        State = state,
                    });
                }
                else
                {
                    existing.State = state;
                }
            }
            return Task.FromResult(_pod);
        }

        public Task<V1Pod> AddEphemeralContainerAsync(string namespaceName, string name, V1EphemeralContainer ephemeralContainer, CancellationToken cancellationToken)
        {
            if (_patchEx is not null) throw _patchEx;
            PatchInvoked = true;
            PatchInvocationCount++;
            PatchedSpec = ephemeralContainer;
            _readCount = 0; // restart readiness clock so ephemeralRunningAfter applies post-patch
            return Task.FromResult(_pod!);
        }

        public Task<k8s.IStreamDemuxer> OpenPortForwardAsync(string namespaceName, string name, int podPort, CancellationToken cancellationToken)
            => throw new NotSupportedException("StubAttachApi does not exercise port-forward; use the dedicated KubernetesPortForwardManager tests.");
    }

    private sealed class NoOpProxyClient : IInvestigationProxyClient
    {
        public Task<ModelContextProtocol.Protocol.CallToolResult> CallToolAsync(InvestigationHandle handle, ModelContextProtocol.Protocol.CallToolRequestParams request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task DisposeForHandleAsync(string handleId) => Task.CompletedTask;
    }

    private sealed class NoOpPortForwardManager : IPortForwardManager
    {
        public Task<HttpClient> GetOrCreateClientAsync(InvestigationHandle handle, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task CloseAsync(string handleId) => Task.CompletedTask;
    }

    /// <summary>
    /// Minimal manual <see cref="TimeProvider"/> double — advances on every <see cref="GetUtcNow"/>
    /// call so AttachReadinessTimeoutSeconds is reached deterministically in tests without real sleeps.
    /// </summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow()
        {
            var snapshot = _now;
            _now = _now.AddMilliseconds(250);
            return snapshot;
        }
    }
}
