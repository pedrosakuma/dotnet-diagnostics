using DotnetDiagnostics.Mcp.Orchestrator.Investigations;
using DotnetDiagnostics.Mcp.Security;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace DotnetDiagnostics.Mcp.IntegrationTests.Orchestrator.Investigations;

public sealed class InvestigationProxyReadResourceFilterTests
{
    [Theory]
    [InlineData("heap://snapshot/heap-handle")]
    [InlineData("thread://snapshot/thread-handle")]
    [InlineData("trace://session/trace-handle")]
    [InlineData("journey://diff/diff-handle")]
    [InlineData("signals://cpu-sample/cpu-handle")]
    public async Task BoundSession_RejectsDynamicDiagnosticResource(string resourceUri)
    {
        var binder = new MemoryInvestigationSessionBinder();
        binder.Bind("bound-session", "inv-bound");
        var nextCalls = 0;

        Func<Task> act = async () => await InvestigationProxyReadResourceFilter.InvokeAsync(
            new ReadResourceRequestParams { Uri = resourceUri },
            "bound-session",
            (_, _) =>
            {
                nextCalls++;
                return ValueTask.FromResult(new ReadResourceResult { Contents = [] });
            },
            binder,
            () => NullLogger.Instance,
            CancellationToken.None);

        await act.Should().ThrowAsync<McpException>()
            .WithMessage("*query_snapshot*");
        nextCalls.Should().Be(0);
    }

    [Fact]
    public async Task BoundSession_AllowsStaticInvestigationGuide()
    {
        var binder = new MemoryInvestigationSessionBinder();
        binder.Bind("bound-session", "inv-bound");
        var expected = new ReadResourceResult { Contents = [] };

        var result = await InvestigationProxyReadResourceFilter.InvokeAsync(
            new ReadResourceRequestParams
            {
                Uri = InvestigationProxyResourcePolicy.InvestigationGuideUri,
            },
            "bound-session",
            (_, _) => ValueTask.FromResult(expected),
            binder,
            () => NullLogger.Instance,
            CancellationToken.None);

        result.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task UnboundSession_LeavesLocalResourceReadsUnchanged()
    {
        var binder = new MemoryInvestigationSessionBinder();
        var expected = new ReadResourceResult { Contents = [] };

        var result = await InvestigationProxyReadResourceFilter.InvokeAsync(
            new ReadResourceRequestParams { Uri = "heap://snapshot/local-handle" },
            "unbound-session",
            (_, _) => ValueTask.FromResult(expected),
            binder,
            () => NullLogger.Instance,
            CancellationToken.None);

        result.Should().BeSameAs(expected);
    }
}
