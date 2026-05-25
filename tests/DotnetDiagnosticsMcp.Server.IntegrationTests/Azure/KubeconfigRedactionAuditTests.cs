using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Server.Azure;
using DotnetDiagnosticsMcp.Server.Azure.Discovery;
using DotnetDiagnosticsMcp.Server.Orchestrator;
using DotnetDiagnosticsMcp.Server.Tools;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests.Azure;

/// <summary>
/// Security regression for the kubeconfig handle subsystem (#234). Drives BOTH the
/// <c>discover_azure(kind=aksclusters, includeKubeconfig=true)</c> tool call AND the
/// follow-up <c>list_orchestrator(kind=pods, kubeconfigHandle=...)</c> redemption
/// through their tool methods end-to-end, then sweeps the serialized response
/// envelopes, every captured log line (across every category), structured scope
/// state, and every captured exception for any trace of the kubeconfig sentinel or
/// the bearer handle string.
/// </summary>
/// <remarks>
/// FIX 5 (#234 review): the v1 redaction test invoked <see cref="AzureAksDiscovery"/>
/// in isolation with a single-category sink whose <c>BeginScope</c> returned
/// <c>NullScope</c>. That left three big holes in the audit:
/// <list type="bullet">
///   <item>any logger category OTHER than <c>AzureAksDiscovery</c> went uncaptured;</item>
///   <item>structured scope state (e.g. <c>using (logger.BeginScope(new { Handle = h }))</c>)
///     was silently discarded;</item>
///   <item>the redemption tool (<c>list_orchestrator</c>) — where leaks are most
///     likely because the handle is the input — was never exercised.</item>
/// </list>
/// This rewrite plugs all three holes with a real multi-category sink (every
/// category that flows through the host's <see cref="ILoggerFactory"/>), a real
/// <c>BeginScope</c> that captures state, and a full two-stage call pipeline.
/// </remarks>
public sealed class KubeconfigRedactionAuditTests
{
    private const string ServerSentinel = "https://example-aks-SUPERSECRETSENTINEL.hcp.eastus.azmk8s.io";
    private const string TokenSentinel = "eyJSENTINELTOKENJWTPAYLOAD.0123456789ABCDEF";
    private const string ConfigSentinel = "REDACTION-SENTINEL-3F8A1B2C-DEAD-BEEF-CAFE-000000000001";

    private static readonly byte[] KubeconfigPayload = Encoding.UTF8.GetBytes(
        "apiVersion: v1\n" +
        "kind: Config\n" +
        "# " + ConfigSentinel + "\n" +
        "clusters:\n- cluster:\n    server: " + ServerSentinel + "\n" +
        "users:\n- user:\n    token: " + TokenSentinel + "\n");

    [Fact]
    public async Task FullToolPipeline_DiscoverThenRedeem_NeverLeaksKubeconfigOrHandle()
    {
        // ---- Arrange: shared sink, host LoggerFactory, store + context.
        var sink = new TestLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b =>
        {
            b.AddProvider(sink);
            b.SetMinimumLevel(LogLevel.Trace);
        });

        var clock = new ControllableClock();
        var options = new AzureDiscoveryOptions { Enabled = true, KubeconfigHandleTtl = TimeSpan.FromMinutes(10) };
        await using var store = new InMemoryKubeconfigHandleStore(options, clock);
        var context = new AsyncLocalKubeconfigContext();

        var adapter = new SentinelAdapter();
        var aksDiscovery = new AzureAksDiscovery(adapter, store, loggerFactory.CreateLogger<AzureAksDiscovery>());

        // No-op webapps + containerapps backends to satisfy the tool signature; AKS
        // is what we exercise.
        var noopWebApps = new NoopWebApps();
        var noopContainerApps = new NoopContainerApps();
        var discoverTool = new DiscoverAzureTool();

        // ---- Act 1: discover_azure(kind=aksclusters, includeKubeconfig=true).
        var discoverResult = await discoverTool.DiscoverAzureAsync(
            noopWebApps, noopContainerApps, aksDiscovery, options,
            subscriptionId: "11111111-1111-1111-1111-111111111111",
            kind: DiscoverAzureTool.KindAksClusters,
            includeKubeconfig: true,
            cancellationToken: CancellationToken.None);

        discoverResult.IsError.Should().BeFalse();
        var handoff = discoverResult.Data!.AksClusters!.Items.Single().Handoff!;
        handoff.KubeconfigHandle.Should().StartWith("kc:");
        var handle = handoff.KubeconfigHandle;

        // ---- Act 2: list_orchestrator(kind=pods, kubeconfigHandle=handle).
        var inventory = new ObservingInventory(loggerFactory.CreateLogger<ObservingInventory>(), context);
        var orchestratorOptions = new OrchestratorOptions { Enabled = true, DefaultNamespace = "diag" };
        orchestratorOptions.NamespaceAllowlist.Add("diag");

        var redeemResult = await ListOrchestratorTool.ListOrchestrator(
            inventory: inventory,
            store: null!,
            options: orchestratorOptions,
            principalAccessor: TestPrincipalAccessors.Root,
            kubeconfigContext: context,
            kubeconfigStore: store,
            kind: ListOrchestratorTool.KindPods,
            @namespace: "diag",
            kubeconfigHandle: handle,
            cancellationToken: CancellationToken.None);

        redeemResult.IsError.Should().BeFalse(redeemResult.Summary);
        inventory.HandleObservedDuringCall.Should().Be(handle,
            "handle MUST be active on the AsyncLocal context for the inner inventory call");

        // ---- Sweep #1: serialized response envelopes never contain the sentinels.
        var discoverJson = JsonSerializer.Serialize(discoverResult);
        var redeemJson = JsonSerializer.Serialize(redeemResult);
        foreach (var sentinel in new[] { ServerSentinel, TokenSentinel, ConfigSentinel, "apiVersion: v1" })
        {
            discoverJson.Should().NotContain(sentinel, "discover_azure response must not echo kubeconfig contents");
            redeemJson.Should().NotContain(sentinel, "list_orchestrator response must not echo kubeconfig contents");
        }

        // ---- Sweep #2: every captured log line — message, exception, scope state — is
        // free of every sentinel. Captured across EVERY category.
        var allLogs = sink.RenderAll();
        foreach (var sentinel in new[] { ServerSentinel, TokenSentinel, ConfigSentinel })
        {
            allLogs.Should().NotContain(sentinel,
                $"no log line / scope state / exception across any category may contain the sentinel '{sentinel}'");
        }

        // ---- Sweep #3: the handle string itself must NOT appear in any captured log
        // at Information or higher (it MAY appear at Debug/Trace per the production
        // code's audit log convention — verify below).
        var infoAndAbove = sink.Records
            .Where(r => r.Level >= LogLevel.Information)
            .ToList();
        foreach (var record in infoAndAbove)
        {
            record.RenderedMessage.Should().NotContain(handle,
                $"handle leaked to {record.Category}@{record.Level} message");
            foreach (var (k, v) in record.StateKeyValues)
            {
                v?.ToString().Should().NotContain(handle,
                    $"handle leaked to {record.Category}@{record.Level} structured state '{k}'");
            }
            foreach (var scope in record.Scopes)
            {
                scope.Should().NotContain(handle,
                    $"handle leaked to {record.Category}@{record.Level} scope state");
            }
            if (record.Exception is not null)
            {
                record.Exception.ToString().Should().NotContain(handle,
                    $"handle leaked to {record.Category}@{record.Level} exception (incl. stack trace)");
            }
        }

        store.Count.Should().Be(1);
    }

    private sealed class SentinelAdapter : IAzureManagedClusterCollectionAdapter
    {
        public Func<string, Exception?>? CredentialFailure { get; init; }

#pragma warning disable CS1998
        public async IAsyncEnumerable<AzureAksClusterRow> ListAsync(
            string subscriptionId,
            string? resourceGroup,
            [EnumeratorCancellation] CancellationToken cancellationToken)
#pragma warning restore CS1998
        {
            yield return new AzureAksClusterRow(
                ResourceId: "/subscriptions/x/resourceGroups/rg/providers/Microsoft.ContainerService/managedClusters/aks-prod",
                Name: "aks-prod",
                Location: "eastus",
                AgentPoolCount: 3,
                Fqdn: "aks-prod.hcp.eastus.azmk8s.io",
                KubernetesVersion: "1.30.0",
                NodeResourceGroup: "MC_rg_aks-prod_eastus",
                IsPrivateCluster: false);
        }

        public Task<byte[]> GetClusterUserKubeconfigAsync(string resourceId, CancellationToken cancellationToken)
        {
            var failure = CredentialFailure?.Invoke(resourceId);
            if (failure is not null) throw failure;
            return Task.FromResult((byte[])KubeconfigPayload.Clone());
        }
    }

    private sealed class NoopWebApps : IAzureWebAppsDiscovery
    {
        public Task<AzurePagedResult<AzureWebAppCandidate>> ListAsync(AzureDiscoveryRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new AzurePagedResult<AzureWebAppCandidate>(Array.Empty<AzureWebAppCandidate>(), null));
    }

    private sealed class NoopContainerApps : IAzureContainerAppsDiscovery
    {
        public Task<AzurePagedResult<AzureContainerAppCandidate>> ListAsync(AzureDiscoveryRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new AzurePagedResult<AzureContainerAppCandidate>(Array.Empty<AzureContainerAppCandidate>(), null));
    }

    private sealed class ObservingInventory : IPodInventory
    {
        private readonly ILogger _logger;
        private readonly IKubeconfigContext _context;
        public string? HandleObservedDuringCall { get; private set; }

        public ObservingInventory(ILogger logger, IKubeconfigContext context)
        {
            _logger = logger;
            _context = context;
        }

        public Task<PodCandidatePage> ListPodsAsync(ListPodsRequest request, CancellationToken cancellationToken)
        {
            HandleObservedDuringCall = _context.CurrentHandle;
            // Emit a representative log line so the audit covers the inventory category.
            _logger.LogInformation("Listing pods in {Namespace} (kubeconfigHandlePresent: {Present}).",
                request.Namespace, _context.CurrentHandle is not null);
            return Task.FromResult(new PodCandidatePage(Array.Empty<PodCandidate>(), null));
        }
    }

    private sealed class ControllableClock : TimeProvider
    {
        private readonly DateTimeOffset _now = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>
    /// Multi-category sink with full scope + structured-state + exception capture.
    /// Replaces the v1 single-category, NullScope-returning provider.
    /// </summary>
    internal sealed class TestLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        private readonly ConcurrentQueue<LogRecord> _records = new();
        private IExternalScopeProvider? _scopeProvider;

        public IReadOnlyCollection<LogRecord> Records => _records.ToArray();

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopeProvider = scopeProvider;

        public ILogger CreateLogger(string categoryName) => new Capturing(this, categoryName);

        public string RenderAll()
        {
            var sb = new StringBuilder();
            foreach (var r in _records)
            {
                sb.AppendLine($"[{r.Category}] {r.Level}: {r.RenderedMessage}");
                foreach (var (k, v) in r.StateKeyValues)
                {
                    sb.AppendLine($"    state.{k} = {v}");
                }
                foreach (var s in r.Scopes)
                {
                    sb.AppendLine($"    scope = {s}");
                }
                if (r.Exception is not null)
                {
                    sb.AppendLine($"    exception = {r.Exception}");
                }
            }
            return sb.ToString();
        }

        public void Dispose() { }

        private sealed class Capturing : ILogger
        {
            private readonly TestLoggerProvider _owner;
            private readonly string _category;

            public Capturing(TestLoggerProvider owner, string category)
            {
                _owner = owner;
                _category = category;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
                => _owner._scopeProvider?.Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var rendered = formatter(state, exception);
                var kv = new List<KeyValuePair<string, object?>>();
                if (state is IReadOnlyList<KeyValuePair<string, object?>> structured)
                {
                    kv.AddRange(structured);
                }

                var scopes = new List<string>();
                _owner._scopeProvider?.ForEachScope(static (scope, list) =>
                {
                    if (scope is null) return;
                    if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                    {
                        foreach (var p in pairs)
                        {
                            list.Add($"{p.Key}={p.Value}");
                        }
                    }
                    else
                    {
                        list.Add(scope.ToString() ?? string.Empty);
                    }
                }, scopes);

                _owner._records.Enqueue(new LogRecord(_category, logLevel, rendered, exception, kv, scopes));
            }
        }
    }

    internal sealed record LogRecord(
        string Category,
        LogLevel Level,
        string RenderedMessage,
        Exception? Exception,
        IReadOnlyList<KeyValuePair<string, object?>> StateKeyValues,
        IReadOnlyList<string> Scopes);
}
