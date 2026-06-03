using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Core.Dump;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Cli.Tests;

/// <summary>
/// Coverage for <see cref="CliHintProjection"/> (issue #301). The shared Core engine authors
/// <see cref="NextActionHint"/>s for the MCP/LLM audience — they name MCP tools and embed MCP call
/// syntax. The CLI must project those into its own vocabulary (or drop them) so a human never sees a
/// hint pointing at a command that does not exist / is <c>NotSupported</c> in the one-shot CLI.
/// </summary>
public sealed class CliHintProjectionTests
{
    [Theory]
    [InlineData("collect_events", "collect")]
    [InlineData("inspect_process", "processes")]
    [InlineData("list_dotnet_processes", "processes")]
    [InlineData("inspect_heap", "inspect-heap")]
    [InlineData("inspect_live_heap", "inspect-heap")]
    [InlineData("inspect_dump", "inspect-heap")]
    [InlineData("collect_process_dump", "dump")]
    public void TryProjectHint_TranslatesKnownMcpTool_ToCliCommand(string mcpTool, string cliCommand)
    {
        var ok = CliHintProjection.TryProjectHint(new NextActionHint(mcpTool, "Cheap first signal."), out var projected);

        ok.Should().BeTrue();
        projected.NextTool.Should().Be(cliCommand);
    }

    [Theory]
    [InlineData("collect_sample")]
    [InlineData("collect_thread_snapshot")]
    [InlineData("collect_off_cpu_sample")]
    [InlineData("query_snapshot")]
    [InlineData("query_heap_snapshot")]
    [InlineData("dotnet-assembly-mcp.get_method")]
    public void TryProjectHint_DropsTools_WithoutCliEquivalent(string mcpTool)
    {
        var ok = CliHintProjection.TryProjectHint(new NextActionHint(mcpTool, "Pivot to assembly inspection."), out _);

        ok.Should().BeFalse();
    }

    [Fact]
    public void TryProjectHint_SanitizesReason_EmbeddingMcpCallSyntax()
    {
        // The EPERM fallback hint the CLI hits on inspect-heap/dump references MCP tools in its reason.
        var hint = new NextActionHint(
            "collect_process_dump",
            "Fall back to dump-based workflow (collect_process_dump then inspect_heap/inspect_dump). " +
            "Dumps use the diagnostic IPC socket (no ptrace) and work across PID namespaces.");

        var ok = CliHintProjection.TryProjectHint(hint, out var projected);

        ok.Should().BeTrue();
        projected.NextTool.Should().Be("dump");
        projected.Reason.Should().Contain("dump then inspect-heap");
        AssertNoLeak(projected.NextTool + " " + projected.Reason);
    }

    [Fact]
    public void TryProjectHint_RewritesDumpTypeAndConfirm_ToCliFlags()
    {
        var miniHint = new NextActionHint(
            "inspect_heap",
            "Mini dump captured — heap walk unavailable. Re-capture with dumpType='WithHeap' for full inspection.");
        var confirmHint = new NextActionHint(
            "collect_process_dump",
            "Re-issue the call with confirm=true after explicit human approval. Required scopes: dump-write + ptrace.");

        CliHintProjection.TryProjectHint(miniHint, out var projectedMini).Should().BeTrue();
        CliHintProjection.TryProjectHint(confirmHint, out var projectedConfirm).Should().BeTrue();

        projectedMini.Reason.Should().Contain("--dump-type WithHeap").And.NotContain("dumpType=");
        projectedConfirm.Reason.Should().Contain("--confirm").And.NotContain("confirm=true");
    }

    [Fact]
    public void TryProjectHint_FailsClosed_WhenReasonStillLeaksAfterRewrite()
    {
        // Mapped tool, but the reason carries an MCP fragment with no rewrite rule -> must be dropped,
        // never rendered with a leak.
        var hint = new NextActionHint("inspect_heap", "Use query_snapshot(handle=\"h1\", view=\"top-types\") to drill in.");

        var ok = CliHintProjection.TryProjectHint(hint, out _);

        ok.Should().BeFalse();
    }

    [Fact]
    public void TryProjectHint_DropsSuggestedArguments()
    {
        var hint = new NextActionHint(
            "collect_events",
            "GC pressure detected.",
            new Dictionary<string, object?> { ["processId"] = 1234, ["kind"] = "gc", ["durationSeconds"] = 10 });

        CliHintProjection.TryProjectHint(hint, out var projected).Should().BeTrue();

        projected.SuggestedArguments.Should().BeNull();
    }

    [Fact]
    public void Project_KeepsCliHints_AndDropsNonCliHints()
    {
        var result = DiagnosticResult.Ok(
            new object(),
            "summary",
            new NextActionHint("collect_events", "Cheap first signal."),
            new NextActionHint("collect_sample", "cpu-usage=85.0% — investigate the hot path."),
            new NextActionHint("inspect_heap", "GC pressure — inspect heap for allocation patterns."));

        var projected = CliHintProjection.Project(result);

        projected.Hints.Should().HaveCount(2);
        projected.Hints.Select(h => h.NextTool).Should().BeEquivalentTo(new[] { "collect", "inspect-heap" });
    }

    [Fact]
    public void Project_RenderedHints_NeverContainAnyLeakToken()
    {
        // A representative cross-section of the real Core hints (tool + reason), projected, must be
        // free of every MCP-only token the guard list tracks.
        var result = DiagnosticResult.Ok(
            new object(),
            "summary",
            new NextActionHint("collect_events", "threadpool-queue-length=50 — possible ThreadPool starvation."),
            new NextActionHint("inspect_process", "List attachable .NET processes and pick a valid pid."),
            new NextActionHint("inspect_heap", "Inspect the dump's managed heap for top-retained types + handoff payload to dotnet-assembly-mcp."),
            new NextActionHint("collect_process_dump", "Fall back to dump-based workflow: collect_process_dump then inspect_heap(source=\"dump\")."),
            new NextActionHint("list_dotnet_processes", "Confirm the module/dump exists and the target is still alive."),
            new NextActionHint("get-bytes", "Re-issue with --out pointing at a writable destination."));

        var projected = CliHintProjection.Project(result);

        foreach (var hint in projected.Hints)
        {
            AssertNoLeak(hint.NextTool + " " + hint.Reason);
        }
    }

    [Fact]
    public void TryProjectHint_RewritesProcessIdVocabulary_ToPidFlag()
    {
        // The AmbiguousDotnetProcess hint Core authors names the MCP/JSON argument `processId`,
        // which does not exist in the CLI (it uses --pid).
        var hint = new NextActionHint(
            "list_dotnet_processes",
            "Inspect the candidate list inline below and re-issue the call with the chosen processId.");

        CliHintProjection.TryProjectHint(hint, out var projected).Should().BeTrue();

        projected.NextTool.Should().Be("processes");
        projected.Reason.Should().Contain("--pid").And.NotContain("processId");
        AssertNoLeak(projected.NextTool + " " + projected.Reason);
    }

    [Fact]
    public void Project_RewritesProcessIdInSummary_ToPidFlag()
    {
        // The AmbiguousDotnetProcess summary itself leaks `processId`; projection rewrites the whole
        // envelope so the human is told to pass --pid, the flag that actually exists.
        var result = DiagnosticResult.Ok(
            new object(),
            "3 .NET processes visible — pass processId explicitly.");

        var projected = CliHintProjection.Project(result);

        projected.Summary.Should().Contain("--pid").And.NotContain("processId");
    }

    [Fact]
    public void RewriteDumpPreview_ConfirmationRequired_ReplacesMcpVocabulary()
    {
        var preview = new DumpToolResult
        {
            Kind = DumpToolResultKinds.ConfirmationRequired,
            Message = "collect_process_dump writes a heap dump to disk. Pass confirm=true to proceed.",
            TargetPid = 4242,
            DumpType = ProcessDumpType.WithHeap,
        };
        var result = DiagnosticResult.Ok(
            preview,
            "confirmation_required: collect_process_dump would write a WithHeap dump for pid 4242. Pass confirm=true to proceed.");

        var rewritten = CliHintProjection.RewriteDumpPreview(result);

        rewritten.Summary.Should().Contain("--confirm").And.Contain("pid 4242");
        rewritten.Summary.Should().NotContain("collect_process_dump").And.NotContain("confirm=true");
        rewritten.Data!.Message.Should().Contain("--confirm").And.NotContain("collect_process_dump");
    }

    [Fact]
    public void RewriteDumpPreview_AutoResolvedPreview_DescribesAutoSelection()
    {
        var preview = new DumpToolResult
        {
            Kind = DumpToolResultKinds.ConfirmationRequired,
            TargetPid = null,
            DumpType = ProcessDumpType.Mini,
        };
        var result = DiagnosticResult.Ok(preview, "confirmation_required: collect_process_dump ...");

        var rewritten = CliHintProjection.RewriteDumpPreview(result);

        rewritten.Summary.Should().Contain("auto-resolved").And.Contain("--confirm");
    }

    [Fact]
    public void RewriteDumpPreview_WrittenDump_PassesThroughUnchanged()
    {
        var written = new DumpToolResult { Kind = DumpToolResultKinds.DumpWritten, TargetPid = 7 };
        var result = DiagnosticResult.Ok(written, "Dump written.");

        var rewritten = CliHintProjection.RewriteDumpPreview(result);

        rewritten.Should().BeSameAs(result);
    }

    private static void AssertNoLeak(string rendered)
    {
        foreach (var token in CliHintProjection.LeakTokens)
        {
            rendered.Should().NotContain(token, $"projected CLI hint output must not leak the MCP token '{token}'");
        }
    }
}
