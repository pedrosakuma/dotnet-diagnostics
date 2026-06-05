using System.Globalization;

namespace DotnetDiagnosticsMcp.Core.OffCpu;

/// <summary>
/// Host-neutral drill-down engine for <see cref="OffCpuSnapshotArtifact"/> handles — the off-CPU
/// analogue of <see cref="DotnetDiagnosticsMcp.Core.Dump.HeapSnapshotQueryDispatcher"/> and
/// <see cref="DotnetDiagnosticsMcp.Core.CpuSampling.CpuSampleQueryDispatcher"/>. Every off-CPU view
/// (<c>topStacks</c>, <c>byThread</c>, <c>stack</c>) renders purely from the already-captured
/// artifact — no live perf re-run, no authorization — so both the MCP server's
/// <c>query_off_cpu_snapshot</c> tool and the standalone CLI <c>session</c> REPL (issue #300) share
/// one implementation.
/// </summary>
public static class OffCpuQueryDispatcher
{
    /// <summary>The view names this dispatcher renders from a snapshot alone (drill-down without re-running perf).</summary>
    public static IReadOnlyList<string> SessionViews { get; } = new[] { "topStacks", "byThread", "stack" };

    /// <summary>
    /// Renders <paramref name="view"/> from <paramref name="artifact"/>. Mirrors the MCP server's
    /// original <c>QueryOffCpuSnapshot</c> switch byte-for-byte: <paramref name="view"/> is matched
    /// case-insensitively and any unrecognized name falls through to <c>topStacks</c> (the original
    /// server behavior). The raw <paramref name="view"/> string is preserved in the returned
    /// <see cref="OffCpuQueryView.View"/>.
    /// </summary>
    public static DiagnosticResult<OffCpuQueryView> Dispatch(
        OffCpuSnapshotArtifact artifact, string view, int topN, int? stackRank)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        // Defensive guard for non-server callers (the MCP server validates topN before delegating, so
        // this is unreachable on the server path and keeps its behavior byte-identical).
        if (topN < 1) return InvalidArg<OffCpuQueryView>(nameof(topN), "must be >= 1");

        return view.ToLowerInvariant() switch
        {
            "bythread" => DiagnosticResult.Ok(
                new OffCpuQueryView(view, artifact.ProcessId, artifact.TotalOffCpuMicros,
                    Stacks: null,
                    Threads: artifact.Threads.Take(topN).ToList(),
                    Stack: null),
                $"{Math.Min(topN, artifact.Threads.Count)} of {artifact.Threads.Count} threads ranked by off-CPU micros."),
            "stack" => RenderStack(artifact, stackRank),
            "topstacks" or _ => DiagnosticResult.Ok(
                new OffCpuQueryView(view, artifact.ProcessId, artifact.TotalOffCpuMicros,
                    Stacks: artifact.Stacks.Take(topN).ToList(),
                    Threads: null,
                    Stack: null),
                $"Top {Math.Min(topN, artifact.Stacks.Count)} blocking stacks of {artifact.Stacks.Count} distinct."),
        };
    }

    private static DiagnosticResult<OffCpuQueryView> RenderStack(OffCpuSnapshotArtifact artifact, int? stackRank)
    {
        if (stackRank is null || stackRank < 1)
        {
            return InvalidArg<OffCpuQueryView>(nameof(stackRank), "is required for view='stack' and must be >= 1");
        }
        var idx = stackRank.Value - 1;
        if (idx >= artifact.Stacks.Count)
        {
            return DiagnosticResult.Fail<OffCpuQueryView>(
                $"stackRank={stackRank} exceeds available {artifact.Stacks.Count} stacks.",
                new DiagnosticError("OutOfRange", "Pick a rank within the topStacks list.", stackRank.Value.ToString(CultureInfo.InvariantCulture)),
                new NextActionHint("query_snapshot", "List the top stacks first.",
                    new Dictionary<string, object?> { ["view"] = "topStacks" }));
        }
        var s = artifact.Stacks[idx];
        return DiagnosticResult.Ok(
            new OffCpuQueryView("stack", artifact.ProcessId, artifact.TotalOffCpuMicros,
                Stacks: null, Threads: null, Stack: s),
            $"Rank {stackRank}/{artifact.Stacks.Count}: {s.LeafFrame} — {s.OffCpuMicros / 1000.0:F1} ms across {s.OccurrenceCount} switches (state={s.DominantState}).");
    }

    private static DiagnosticResult<T> InvalidArg<T>(string parameterName, string requirement)
        => DiagnosticResult.Fail<T>(
            $"Argument '{parameterName}' {requirement}.",
            new DiagnosticError("InvalidArgument", $"Argument '{parameterName}' {requirement}.", parameterName),
            new NextActionHint("inspect_process", "Re-issue with valid arguments. See tool schema for ranges and defaults."));
}
