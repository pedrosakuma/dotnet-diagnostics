using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Mcp.Orchestrator.Investigations;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

/// <summary>
/// Opt-in end-to-end acceptance test for the external-investigation Docker passthrough
/// workflow (issue #712).
///
/// <para>
/// Run through <c>scripts/test-docker-external-investigation.sh</c>, which starts a
/// CoreClrSample target, invokes the current checkout's CLI <c>docker-bootstrap</c>,
/// starts the central with the emitted profile configuration, and gates the test via
/// <see cref="EnableEnvVar"/>.
/// </para>
/// <para>
/// Acceptance criteria exercised:
/// <list type="bullet">
/// <item>Central MCP lists the 'sidecar' external profile.</item>
/// <item><c>attach_to_pod(profileName="sidecar")</c> succeeds and returns an Active handle.</item>
/// <item><c>inspect_process(view=list)</c> forwarded through the handle returns CoreClrSample
///   running inside the sidecar's PID namespace.</item>
/// <item><c>collect_batch</c> forwards counters + GC collectors through the handle and returns
///   real System.Runtime EventPipe evidence from the target.</item>
/// <item>After <c>detach_from_pod</c>, subsequent forwarded tool calls via the stale handle
///   return a structured routing-failure result.</item>
/// </list>
/// </para>
/// </summary>
[Trait("Category", "DockerIntegration")]
public sealed class DockerExternalInvestigationTests
{
    private const string EnableEnvVar = "DOTNET_DBG_MCP_DOCKER_EXT_INV_TEST";
    private const string CentralUrlEnvVar = "DOTNET_DBG_MCP_DOCKER_EXT_INV_CENTRAL_URL";
    private const string CentralTokenEnvVar = "DOTNET_DBG_MCP_DOCKER_EXT_INV_CENTRAL_TOKEN";
    private const string ProfileNameEnvVar = "DOTNET_DBG_MCP_DOCKER_EXT_INV_PROFILE";
    private const string TargetUrlEnvVar = "DOTNET_DBG_MCP_DOCKER_EXT_INV_TARGET_URL";

    /// <summary>
    /// Central orchestrator MCP endpoint (the only endpoint clients connect to).
    /// Published port matches <c>docker-compose.external-investigation.yml</c>.
    /// </summary>
    private const string DefaultCentralMcpUrl = "http://127.0.0.1:18890/mcp";

    /// <summary>
    /// Bearer token for the central MCP, as configured in the compose file
    /// (<c>Auth__BearerTokens__0__Token=central-dev-token</c>).
    /// </summary>
    private const string DefaultCentralBearerToken = "central-dev-token";

    /// <summary>Name of the external-MCP profile configured in the central.</summary>
    private const string DefaultSidecarProfileName = "sidecar";
    private const string DefaultTargetUrl = "http://127.0.0.1:18080";

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly ITestOutputHelper _output;

    public DockerExternalInvestigationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(Timeout = 120_000)]
    public async Task ExternalInvestigation_FullPassthroughWorkflow_AttachInspectCollectDetach()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(EnableEnvVar), "1", StringComparison.Ordinal))
        {
            _output.WriteLine($"{EnableEnvVar} is unset; skipping Docker external-investigation acceptance test.");
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var ct = cts.Token;
        var centralMcpUrl = GetEnvironmentOrDefault(CentralUrlEnvVar, DefaultCentralMcpUrl);
        var centralBearerToken = GetEnvironmentOrDefault(CentralTokenEnvVar, DefaultCentralBearerToken);
        var sidecarProfileName = GetEnvironmentOrDefault(ProfileNameEnvVar, DefaultSidecarProfileName);
        var targetUrl = GetEnvironmentOrDefault(TargetUrlEnvVar, DefaultTargetUrl);

        // ── Step 1: connect to the central (orchestrator) MCP ────────────────
        await using var centralClient = await ConnectAsync(new Uri(centralMcpUrl), centralBearerToken, ct)
            .ConfigureAwait(false);
        _output.WriteLine($"Connected to central orchestrator at {centralMcpUrl}");

        // ── Step 2: verify the sidecar profile is registered ─────────────────
        var listProfilesResult = await centralClient.CallToolAsync(
            "list_orchestrator",
            new Dictionary<string, object?> { ["kind"] = "external-profiles" },
            cancellationToken: ct).ConfigureAwait(false);

        listProfilesResult.IsError.Should().NotBe(true,
            "list_orchestrator(kind=external-profiles) must succeed on the central MCP");
        var profilesEnvelope = DeserializeEnvelope(listProfilesResult);
        profilesEnvelope.Should().NotBeNull();
        profilesEnvelope!.Error.Should().BeNull(profilesEnvelope.Summary);

        var profilesJson = GetResponseJson(listProfilesResult);
        _output.WriteLine($"list_orchestrator(external-profiles): {profilesJson}");

        var externalProfiles = profilesEnvelope.Data
            .GetProperty("externalProfiles")
            .GetProperty("items");
        externalProfiles.GetArrayLength().Should().BeGreaterThan(0,
            "at least the 'sidecar' profile must be configured");
        var profileNames = new List<string>();
        foreach (var p in externalProfiles.EnumerateArray())
        {
            profileNames.Add(p.GetProperty("name").GetString()!);
        }
        profileNames.Should().Contain(sidecarProfileName,
            $"the '{sidecarProfileName}' profile must be listed by the central MCP");

        // ── Step 3: attach through the central MCP using the sidecar profile ─
        var attachResult = await centralClient.CallToolAsync(
            "attach_to_pod",
            new Dictionary<string, object?>
            {
                ["profileName"] = sidecarProfileName,
                ["allowReuseExistingSession"] = false,
                ["ttlSeconds"] = 300,
            },
            cancellationToken: ct).ConfigureAwait(false);

        attachResult.IsError.Should().NotBe(true,
            "attach_to_pod(profileName=sidecar) must succeed against the running sidecar MCP");
        var attachEnvelope = DeserializeResult<AttachSession>(attachResult);
        attachEnvelope.Error.Should().BeNull(attachEnvelope.Summary);
        var session = attachEnvelope.Data;
        session.Should().NotBeNull();
        session!.State.Should().Be(InvestigationState.Active,
            $"handle must be Active after a successful external-profile attach (got {session.State}; reason='{session.FailureReason}')");
        session.ProfileName.Should().Be(sidecarProfileName);
        var handleId = session.HandleId;
        _output.WriteLine($"Attached: handleId={handleId} profile={session.ProfileName} state={session.State}");

        try
        {
            // ── Step 4: inspect_process forwarded through the central MCP ─────
            // No processId → the filter routes this to the sidecar.
            var listProcsResult = await centralClient.CallToolAsync(
                "inspect_process",
                new Dictionary<string, object?>
                {
                    ["view"] = "list",
                    ["investigationHandleId"] = handleId,
                },
                cancellationToken: ct).ConfigureAwait(false);

            listProcsResult.IsError.Should().NotBe(true,
                "inspect_process(view=list) forwarded via handle must succeed");
            var procsEnvelope = DeserializeResult<InspectProcessReport>(listProcsResult);
            procsEnvelope.Error.Should().BeNull(procsEnvelope.Summary);
            var procs = procsEnvelope.Data?.List;
            procs.Should().NotBeNull("the sidecar must return a process list");

            _output.WriteLine($"Processes visible through sidecar ({procs!.Count} total):");
            foreach (var p in procs)
            {
                _output.WriteLine($"  pid={p.ProcessId} entry={p.ManagedEntrypointAssemblyName} cmd={p.CommandLine}");
            }

            procs.Should().Contain(p => p.ManagedEntrypointAssemblyName == "CoreClrSample",
                "CoreClrSample must be visible through the sidecar's PID namespace");
            procs.Should().NotContain(p => p.CommandLine != null &&
                p.CommandLine.Contains("DotnetDiagnostics.Mcp", StringComparison.OrdinalIgnoreCase) &&
                p.CommandLine.Contains("/tmp/", StringComparison.OrdinalIgnoreCase),
                "the sidecar MCP process must not have a visible diagnostic socket " +
                "(DOTNET_EnableDiagnostics=0 suppresses it)");

            // ── Step 5: collect EventPipe counters + GC through the handle ─────
            // No processId → filter routes to sidecar; sidecar auto-selects CoreClrSample
            // (the only .NET process with an active diagnostic socket, because the sidecar
            // itself has DOTNET_EnableDiagnostics=0).
            using var loadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var loadTask = DriveAllocationLoadAsync(new Uri(targetUrl), loadCts.Token);
            CallToolResult collectResult;
            try
            {
                collectResult = await centralClient.CallToolAsync(
                    "collect_batch",
                    new Dictionary<string, object?>
                    {
                        ["requests"] = new object[]
                        {
                            new Dictionary<string, object?> { ["tool"] = "collect_events", ["kind"] = "counters" },
                            new Dictionary<string, object?> { ["tool"] = "collect_events", ["kind"] = "gc" },
                        },
                        ["investigationHandleId"] = handleId,
                        ["durationSeconds"] = 8,
                    },
                    cancellationToken: ct).ConfigureAwait(false);
            }
            finally
            {
                loadCts.Cancel();
            }

            await loadTask.ConfigureAwait(false);

            collectResult.IsError.Should().NotBe(true,
                "collect_batch(counters+gc) forwarded via handle must succeed");
            var batchEnvelope = DeserializeResult<CollectBatchReport>(collectResult);
            batchEnvelope.Error.Should().BeNull(batchEnvelope.Summary);
            var batch = batchEnvelope.Data;
            batch.Should().NotBeNull();
            batch!.DurationSeconds.Should().Be(8);
            batch.Results.Should().HaveCount(2);

            var countersEntry = batch.Results.Single(r => r.Tool == "collect_events" && r.Kind == "counters");
            countersEntry.Error.Should().BeNull();
            countersEntry.Data.Should().NotBeNull();
            var countersData = countersEntry.Data!.Value;
            countersData.GetProperty("kind").GetString().Should().Be("counters");
            countersData.GetProperty("counters").GetProperty("counters").EnumerateArray()
                .Should().Contain(c =>
                    c.GetProperty("provider").GetString() == "System.Runtime"
                    && c.GetProperty("name").GetString() == "cpu-usage",
                    "the routed batch must contain a real System.Runtime cpu-usage counter");

            var gcEntry = batch.Results.Single(r => r.Tool == "collect_events" && r.Kind == "gc");
            gcEntry.Error.Should().BeNull();
            gcEntry.Data.Should().NotBeNull();
            var gcData = gcEntry.Data!.Value;
            gcData.GetProperty("kind").GetString().Should().Be("gc");
            gcData.GetProperty("gc").GetProperty("totalCollections").GetInt32().Should().BeGreaterThan(0,
                "allocation load during the shared collection window must produce real GC events");

            _output.WriteLine(
                $"collect_batch: pid={batch.ProcessId} countersHandle={countersEntry.Handle} " +
                $"gcHandle={gcEntry.Handle} totalCollections={gcData.GetProperty("gc").GetProperty("totalCollections").GetInt32()}");

            // ── Step 6: detach and verify routing fails ───────────────────────
            var detachResult = await centralClient.CallToolAsync(
                "detach_from_pod",
                new Dictionary<string, object?> { ["handleId"] = handleId },
                cancellationToken: ct).ConfigureAwait(false);

            detachResult.IsError.Should().NotBe(true, "detach_from_pod must succeed");
            var detachEnvelope = DeserializeResult<DetachResult>(detachResult);
            detachEnvelope.Error.Should().BeNull(detachEnvelope.Summary);
            var detached = detachEnvelope.Data;
            detached.Should().NotBeNull();
            detached!.Found.Should().BeTrue();
            detached.NewState.Should().Be(InvestigationState.Closed,
                "detach_from_pod must transition Active → Closed");
            _output.WriteLine($"detach_from_pod: {detached.PreviousState} → {detached.NewState}");

            // ── Step 7: verify stale handle returns a routing-failure ─────────
            // The handle is now Closed. The filter must refuse to forward and return
            // a structured error rather than executing locally on the central.
            var staleResult = await centralClient.CallToolAsync(
                "inspect_process",
                new Dictionary<string, object?>
                {
                    ["view"] = "list",
                    ["investigationHandleId"] = handleId,
                },
                cancellationToken: ct).ConfigureAwait(false);

            staleResult.IsError.Should().BeTrue(
                "calls via a detached handle must return IsError=true from the routing layer; " +
                "the central must not silently fall through to its own local process list");
            var staleText = GetResponseJson(staleResult);
            staleText.Should().ContainAny(
                "unknown or no longer active",
                "no longer active",
                "not active",
                "Closed",
                "closed");
            _output.WriteLine($"Post-detach routing error (expected): {staleText[..Math.Min(200, staleText.Length)]}");
        }
        catch
        {
            // Best-effort cleanup: detach so a partial failure does not leak the handle.
            try
            {
                await centralClient.CallToolAsync(
                    "detach_from_pod",
                    new Dictionary<string, object?> { ["handleId"] = handleId },
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Swallow — the handle may already be closed.
            }
            throw;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<McpClient> ConnectAsync(
        Uri endpoint,
        string bearer,
        CancellationToken cancellationToken)
    {
        var httpClient = new System.Net.Http.HttpClient { BaseAddress = endpoint };
        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", bearer);

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = endpoint,
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {bearer}",
                },
            },
            httpClient,
            ownsHttpClient: true);

        return await McpClient.CreateAsync(
            transport,
            clientOptions: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task DriveAllocationLoadAsync(Uri targetBaseUri, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = targetBaseUri };
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1500), cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                using var response = await client.GetAsync(
                    "/render?count=6000",
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static string GetEnvironmentOrDefault(string name, string fallback)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

    private static DiagnosticResult<T> DeserializeResult<T>(CallToolResult result)
    {
        var json = GetResponseJson(result);
        return JsonSerializer.Deserialize<DiagnosticResult<T>>(json, DeserializeOptions)
            ?? throw new JsonException("MCP response did not contain a diagnostic envelope.");
    }

    private static DiagnosticResult<JsonElement>? DeserializeEnvelope(CallToolResult result)
        => DeserializeResult<JsonElement>(result);

    private static string GetResponseJson(CallToolResult result)
    {
        if (result.StructuredContent is { } structured)
        {
            return structured.GetRawText();
        }

        var text = result.Content.OfType<TextContentBlock>().FirstOrDefault();
        if (text is null)
        {
            throw new JsonException("Tool result contains neither structured content nor a text block.");
        }

        return text.Text;
    }
}
