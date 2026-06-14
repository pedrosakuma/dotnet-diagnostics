using System.Net.Http.Headers;
using System.Text.Json;
using DotnetDiagnostics.Core.Dump;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

/// <summary>
/// Issue #425 — end-to-end coverage for the native MCP <b>Elicitation</b> dump-approval gate on
/// <c>collect_process_dump</c>. An elicitation-capable client drives an approve/deny decision
/// in-call; clients without the capability keep the legacy <c>confirm=true</c> preview/retry
/// contract (covered separately in <c>McpToolsTests</c>).
/// </summary>
[Collection(DiagnosticIntegrationGroup.Name)]
public sealed class DumpApprovalElicitationTests : IClassFixture<DumpApprovalElicitationTests.AuthedFactory>
{
    private readonly AuthedFactory _factory;

    public DumpApprovalElicitationTests(AuthedFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CollectProcessDump_ElicitationDeclined_WritesNothing_AndDoesNotInviteRetry()
    {
        // An elicitation-capable client whose handler DENIES the request. The server must write
        // nothing and return a declined envelope that does NOT tell the LLM to retry with confirm=true.
        var elicited = new List<ElicitRequestParams>();
        await using var client = await ConnectAsync(approve: false, captured: elicited);

        var relativeSub = $"diagnosticsmcp-elicit-deny-{Guid.NewGuid():N}";
        var absoluteRoot = Path.Combine(Path.GetTempPath(), "dotnet-diagnostics-mcp", relativeSub);
        try
        {
            var result = await client.CallToolAsync(
                "collect_process_dump",
                new Dictionary<string, object?>
                {
                    ["processId"] = Environment.ProcessId,
                    ["dumpType"] = "Mini",
                    ["outputDirectory"] = relativeSub,
                    // confirm intentionally omitted — approval must come from elicitation.
                },
                cancellationToken: CancellationToken.None);

            result.IsError.Should().NotBe(true, "a declined approval is a normal outcome, not an error");
            elicited.Should().ContainSingle("the server must issue exactly one elicitation request");
            elicited[0].Message.Should().Contain("Mini");

            var payload = DeserializeStructured<DumpToolResult>(result);
            payload.Should().NotBeNull();
            payload!.Dump.Should().BeNull("a declined approval must not write a dump");
            payload.Message.Should().NotContain("confirm=true",
                "a human declined — the LLM must not be invited to retry around that decision");

            Directory.Exists(absoluteRoot).Should().BeFalse(
                "a declined approval must short-circuit before any disk write");
        }
        finally
        {
            if (Directory.Exists(absoluteRoot)) Directory.Delete(absoluteRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CollectProcessDump_ElicitationApproved_WritesDump_WithoutConfirmFlag()
    {
        // An elicitation-capable client whose handler APPROVES. Approval is equivalent to confirm=true:
        // the dump is written even though the confirm flag was never set by the caller.
        var elicited = new List<ElicitRequestParams>();
        await using var client = await ConnectAsync(approve: true, captured: elicited);

        var relativeSub = $"diagnosticsmcp-elicit-approve-{Guid.NewGuid():N}";
        var absoluteRoot = Path.Combine(Path.GetTempPath(), "dotnet-diagnostics-mcp", relativeSub);
        try
        {
            var result = await client.CallToolAsync(
                "collect_process_dump",
                new Dictionary<string, object?>
                {
                    ["processId"] = Environment.ProcessId,
                    ["dumpType"] = "Mini",
                    ["outputDirectory"] = relativeSub,
                    // confirm intentionally omitted — approval comes from elicitation.
                },
                cancellationToken: CancellationToken.None);

            result.IsError.Should().NotBe(true, "an approved dump must succeed");
            elicited.Should().ContainSingle("the server must issue exactly one elicitation request");

            var payload = DeserializeStructured<DumpToolResult>(result);
            payload.Should().NotBeNull();
            payload!.Kind.Should().Be(DumpToolResultKinds.DumpWritten,
                "an approved request writes the dump without needing the confirm flag");
            payload.Dump.Should().NotBeNull();
            payload.Dump!.FilePath.Should().NotBeNullOrWhiteSpace();
            File.Exists(payload.Dump.FilePath).Should().BeTrue("the approved dump file must exist on disk");
        }
        finally
        {
            if (Directory.Exists(absoluteRoot)) Directory.Delete(absoluteRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CollectProcessDump_ElicitationDeclined_CannotBeBypassedWithConfirmTrue()
    {
        // Security: a human decline via elicitation must win even when the caller (an over-eager
        // LLM) also sets confirm=true. The elicitation-capable client is always prompted, and a
        // decline writes nothing — confirm=true must NOT bypass the native approval.
        var elicited = new List<ElicitRequestParams>();
        await using var client = await ConnectAsync(approve: false, captured: elicited);

        var relativeSub = $"diagnosticsmcp-elicit-bypass-{Guid.NewGuid():N}";
        var absoluteRoot = Path.Combine(Path.GetTempPath(), "dotnet-diagnostics-mcp", relativeSub);
        try
        {
            var result = await client.CallToolAsync(
                "collect_process_dump",
                new Dictionary<string, object?>
                {
                    ["processId"] = Environment.ProcessId,
                    ["dumpType"] = "Mini",
                    ["outputDirectory"] = relativeSub,
                    ["confirm"] = true, // must be ignored for an elicitation-capable client.
                },
                cancellationToken: CancellationToken.None);

            result.IsError.Should().NotBe(true);
            elicited.Should().ContainSingle("the server must elicit even when confirm=true is set");

            var payload = DeserializeStructured<DumpToolResult>(result);
            payload.Should().NotBeNull();
            payload!.Dump.Should().BeNull("a declined approval must not write a dump even with confirm=true");

            Directory.Exists(absoluteRoot).Should().BeFalse(
                "confirm=true must not let the caller bypass a human decline");
        }
        finally
        {
            if (Directory.Exists(absoluteRoot)) Directory.Delete(absoluteRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CollectProcessDump_ElicitationFails_FailsClosed_EvenWithConfirmTrue()
    {
        // Fail-closed: a client that ADVERTISED elicitation but whose handler throws must NOT fall
        // back to honouring confirm=true. The dump must not be written; a structured error returns.
        var elicited = new List<ElicitRequestParams>();
        await using var client = await ConnectAsync(approve: true, captured: elicited, throwOnElicit: true);

        var relativeSub = $"diagnosticsmcp-elicit-fail-{Guid.NewGuid():N}";
        var absoluteRoot = Path.Combine(Path.GetTempPath(), "dotnet-diagnostics-mcp", relativeSub);
        try
        {
            var result = await client.CallToolAsync(
                "collect_process_dump",
                new Dictionary<string, object?>
                {
                    ["processId"] = Environment.ProcessId,
                    ["dumpType"] = "Mini",
                    ["outputDirectory"] = relativeSub,
                    ["confirm"] = true, // must NOT rescue a failed elicitation gate.
                },
                cancellationToken: CancellationToken.None);

            var envelope = DeserializeEnvelope(result);
            envelope.Should().NotBeNull();
            envelope!.Error.Should().NotBeNull("a failed elicitation must surface a structured error");
            envelope.Error!.Kind.Should().Be("ElicitationFailed");
            envelope.Data.Should().BeNull("no dump payload when the approval gate failed");

            Directory.Exists(absoluteRoot).Should().BeFalse(
                "fail-closed: a failed elicitation must not write a dump even with confirm=true");
        }
        finally
        {
            if (Directory.Exists(absoluteRoot)) Directory.Delete(absoluteRoot, recursive: true);
        }
    }

    // ---- helpers ----

    private async Task<McpClient> ConnectAsync(bool approve, List<ElicitRequestParams> captured, bool throwOnElicit = false)
    {
        var httpClient = _factory.CreateClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AuthedFactory.Token);

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {AuthedFactory.Token}",
                },
            },
            httpClient,
            ownsHttpClient: true);

        var options = new McpClientOptions
        {
            Capabilities = new ClientCapabilities
            {
                Elicitation = new ElicitationCapability(),
            },
            Handlers = new McpClientHandlers
            {
                ElicitationHandler = (request, _) =>
                {
                    if (request is not null)
                    {
                        lock (captured) captured.Add(request);
                    }

                    if (throwOnElicit)
                    {
                        throw new InvalidOperationException("simulated elicitation handler failure");
                    }

                    var content = new Dictionary<string, JsonElement>
                    {
                        [DumpApproveField] = JsonSerializer.SerializeToElement(approve),
                    };
                    return ValueTask.FromResult(new ElicitResult
                    {
                        Action = approve ? "accept" : "decline",
                        Content = content,
                    });
                },
            },
        };

        return await McpClient.CreateAsync(transport, options, cancellationToken: CancellationToken.None);
    }

    // Mirrors DumpApprovalElicitation.ApproveField (internal to the server assembly).
    private const string DumpApproveField = "approve";

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static T? DeserializeStructured<T>(CallToolResult result)
    {
        var envelope = DeserializeEnvelope<T>(result);
        return envelope is null ? default : envelope.Data;
    }

    private static DotnetDiagnostics.Core.DiagnosticResult<DumpToolResult>? DeserializeEnvelope(CallToolResult result)
        => DeserializeEnvelope<DumpToolResult>(result);

    private static DotnetDiagnostics.Core.DiagnosticResult<T>? DeserializeEnvelope<T>(CallToolResult result)
    {
        string json;
        if (result.StructuredContent is { } structured)
        {
            json = structured.GetRawText();
        }
        else
        {
            var textBlock = result.Content.OfType<TextContentBlock>().FirstOrDefault();
            textBlock.Should().NotBeNull("tool must return either structured content or a text block");
            json = textBlock!.Text;
        }

        var envelope = JsonSerializer.Deserialize<DotnetDiagnostics.Core.DiagnosticResult<T>>(json, DeserializeOptions);
        envelope.Should().NotBeNull("structured payload must deserialize as DiagnosticResult<T>");
        return envelope;
    }

    public sealed class AuthedFactory : WebApplicationFactory<DotnetDiagnostics.Mcp.Program>
    {
        public const string Token = "test-bearer-token-do-not-use-in-prod";

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            Environment.SetEnvironmentVariable("MCP_BEARER_TOKEN", Token);
            base.ConfigureWebHost(builder);
        }
    }
}
