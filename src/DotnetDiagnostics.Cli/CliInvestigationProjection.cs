using System.Text.RegularExpressions;
using DotnetDiagnostics.Core.Investigation;

namespace DotnetDiagnostics.Cli;

/// <summary>
/// Projects an <see cref="InvestigationPlan"/> — authored in Core for the <b>MCP audience</b> and
/// therefore full of MCP tool names (<c>collect_events</c>, <c>collect_sample</c>,
/// <c>query_snapshot</c>), MCP argument names (<c>processId</c>) and MCP call syntax embedded in the
/// step rationales — into a CLI-vocabulary shape (<see cref="CliInvestigationPlan"/>). The standalone
/// CLI serializes the projected DTO into <b>both</b> the human table and the <c>--json</c> envelope,
/// so returning the raw <see cref="InvestigationPlan"/> would leak MCP vocabulary the CLI does not own
/// (mirrors <see cref="CliHintProjection"/> for hints).
/// </summary>
internal static partial class CliInvestigationProjection
{
    public static CliInvestigationPlan Project(InvestigationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return new CliInvestigationPlan(
            InvestigationId: plan.InvestigationId,
            CreatedAt: plan.CreatedAt,
            Mode: plan.Mode.ToString(),
            ProcessId: plan.ProcessId,
            Symptom: plan.Symptom,
            Hypothesis: plan.Hypothesis,
            MaxToolCalls: plan.Constraints.MaxToolCalls,
            NextStep: ProjectStep(plan.NextStep),
            AllSteps: plan.AllSteps.Select(ProjectStep).ToArray(),
            EarlyStopConditions: plan.EarlyStopConditions
                .Select(c => new CliEarlyStop(c.ConditionId, Scrub(c.Description), Scrub(c.Action)))
                .ToArray());
    }

    private static CliInvestigationStep ProjectStep(InvestigationStep step)
    {
        var command = CliHintProjection.TryMapToolToCommand(step.ToolName, out var mapped) ? mapped : null;
        return new CliInvestigationStep(
            StepNumber: step.StepNumber,
            StepId: step.StepId,
            Status: step.Status.ToString(),
            Command: command,
            Rationale: Scrub(step.Rationale),
            Branches: step.Branches
                .Select(b => new CliDecisionBranch(b.Condition, b.NextStepId, Scrub(b.Description)))
                .ToArray());
    }

    /// <summary>
    /// Rewrites the parameterized MCP call syntax the Core planner embeds in prose
    /// (<c>collect_events(kind="counters")</c>, <c>query_snapshot(handle, view="call-tree")</c>, …) into
    /// CLI commands, then fails closed: any residual MCP leak token is stripped so a future planner
    /// string can never silently leak through <c>investigate</c>.
    /// </summary>
    internal static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var scrubbed = CollectEventsCall().Replace(text, "collect --kind $1");
        scrubbed = QuerySnapshotCall().Replace(scrubbed, "query --view $1");
        scrubbed = CollectSampleCall().Replace(scrubbed, "collect (CPU sampling via --capture cpu-sample)");
        scrubbed = ThreadSnapshotCall().Replace(scrubbed, "a ptrace-backed thread snapshot");
        scrubbed = scrubbed.Replace("processId", "--pid", StringComparison.Ordinal);

        // Fail-closed backstop: drop any MCP token that survived the structured rewrites above.
        foreach (var token in CliHintProjection.LeakTokens)
        {
            if (scrubbed.Contains(token, StringComparison.Ordinal))
            {
                scrubbed = scrubbed.Replace(token, string.Empty, StringComparison.Ordinal);
            }
        }

        return scrubbed.Trim();
    }

    [GeneratedRegex("""collect_events\(kind="([^"]+)"\)""")]
    private static partial Regex CollectEventsCall();

    [GeneratedRegex("""query_snapshot\([^)]*view="([^"]+)"[^)]*\)""")]
    private static partial Regex QuerySnapshotCall();

    [GeneratedRegex("""collect_sample\([^)]*\)""")]
    private static partial Regex CollectSampleCall();

    [GeneratedRegex("""collect_thread_snapshot\([^)]*\)""")]
    private static partial Regex ThreadSnapshotCall();
}

/// <summary>CLI-vocabulary view of an <see cref="InvestigationPlan"/>.</summary>
public sealed record CliInvestigationPlan(
    string InvestigationId,
    DateTimeOffset CreatedAt,
    string Mode,
    int ProcessId,
    string? Symptom,
    string? Hypothesis,
    int MaxToolCalls,
    CliInvestigationStep NextStep,
    IReadOnlyList<CliInvestigationStep> AllSteps,
    IReadOnlyList<CliEarlyStop> EarlyStopConditions);

/// <summary>CLI-vocabulary view of a single plan step. <see cref="Command"/> is the CLI command that
/// performs the step, or <see langword="null"/> when no one-shot CLI equivalent exists (the neutral
/// <see cref="StepId"/> still conveys intent).</summary>
public sealed record CliInvestigationStep(
    int StepNumber,
    string StepId,
    string Status,
    string? Command,
    string Rationale,
    IReadOnlyList<CliDecisionBranch> Branches);

/// <summary>CLI-vocabulary view of a decision branch.</summary>
public sealed record CliDecisionBranch(string Condition, string NextStepId, string Description);

/// <summary>CLI-vocabulary view of an early-stop condition.</summary>
public sealed record CliEarlyStop(string ConditionId, string Description, string Action);
