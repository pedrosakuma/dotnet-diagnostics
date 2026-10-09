using DotnetDiagnostics.Core;

namespace DotnetDiagnostics.Core.CpuSampling;

public static partial class CpuSampleQueryDispatcher
{
    /// <summary>Maximum compiled versions listed in one <c>inlinedInto</c> hint.</summary>
    public const int MaxInlinedIntoPerRow = 5;

    /// <summary>
    /// Renders the <c>inlining</c> view: JIT inlining decisions per compiled code version, optionally
    /// filtered by a substring of the compiled method, inliner or inlinee.
    /// </summary>
    public static DiagnosticResult<InliningView> RenderInlining(
        CpuSampleTraceArtifact artifact, string handle, string? methodFilter, int topN)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (topN < 1) return InvalidArg<InliningView>(nameof(topN), "must be >= 1");

        var profile = artifact.Inlining;
        if (profile is null)
        {
            return DiagnosticResult.Fail<InliningView>(
                $"Handle '{handle}' carries no JIT inlining decisions.",
                new DiagnosticError("NotSupported", "Inlining decisions are collected only when collect_sample kind=\"cpu\" runs on the EventPipe backend with captureInlining=true.", null),
                new NextActionHint("query_snapshot", "Use the code-versions view to see which compiled bodies were sampled.",
                    new Dictionary<string, object?> { ["handle"] = handle, ["view"] = CodeVersionsView }));
        }

        IEnumerable<InliningDecisionRow> rows = profile.Decisions;
        if (!string.IsNullOrWhiteSpace(methodFilter))
        {
            rows = rows.Where(r =>
                r.CompiledMethod.Contains(methodFilter, StringComparison.OrdinalIgnoreCase)
                || r.Inliner.Contains(methodFilter, StringComparison.OrdinalIgnoreCase)
                || r.Inlinee.Contains(methodFilter, StringComparison.OrdinalIgnoreCase));
        }

        var matched = rows.ToList();
        var top = matched.Take(topN).ToList();
        var view = new InliningView(
            artifact.ProcessId, profile.TotalDecisions, profile.AttributedDecisions, profile.UnattributedDecisions,
            profile.SucceededDecisions, profile.RefusedDecisions, profile.VersionsWithDecisions,
            matched.Count, top.Count < matched.Count, top, profile.Notes)
        {
            EvidenceBackend = artifact.Evidence?.Backend,
            EvidenceKind = artifact.Evidence?.Kind,
        };

        var summary = top.Count == 0
            ? "No inlining decisions matched."
            : $"{top.Count} of {matched.Count} inlining decision row(s); {profile.AttributedDecisions}/{profile.TotalDecisions} decisions attributed to a published code version ({profile.SucceededDecisions} inlined, {profile.RefusedDecisions} refused). Zero samples for a method does not mean it did not run: an inlined callee has no frame of its own.";
        return DiagnosticResult.Ok(view, summary,
            new NextActionHint("query_snapshot", "See which compiled bodies were sampled.",
                new Dictionary<string, object?> { ["handle"] = handle, ["view"] = CodeVersionsView }));
    }

    // Explains zero- or low-sample methods: lists the compiled versions that inlined this method. The
    // runtime's inlining events carry names only, so this matching is name-based, not address-based.
    private static List<CodeVersionSampleRow> AnnotateInlinedInto(IEnumerable<CodeVersionSampleRow> rows, InliningProfile? inlining)
    {
        var list = rows.ToList();
        if (inlining is null || inlining.Decisions.Count == 0)
        {
            return list;
        }

        var byInlinee = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var d in inlining.Decisions)
        {
            if (!d.Succeeded)
            {
                continue;
            }

            if (!byInlinee.TryGetValue(d.Inlinee, out var set))
            {
                set = new SortedSet<string>(StringComparer.Ordinal);
                byInlinee[d.Inlinee] = set;
            }

            set.Add($"{d.CompiledMethod} [{d.OptimizationTier}] ({d.VersionId})");
        }

        for (var i = 0; i < list.Count; i++)
        {
            var name = StripSignature(list[i].Method);
            var hits = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var (inlinee, versions) in byInlinee)
            {
                if (NamesMatch(name, inlinee))
                {
                    hits.UnionWith(versions);
                }
            }

            if (hits.Count > 0)
            {
                list[i] = list[i] with { InlinedInto = hits.Take(MaxInlinedIntoPerRow).ToArray() };
            }
        }

        return list;
    }

    private static string StripSignature(string method)
    {
        var paren = method.IndexOf('(', StringComparison.Ordinal);
        return paren < 0 ? method : method[..paren];
    }

    private static bool NamesMatch(string a, string b)
        => string.Equals(a, b, StringComparison.Ordinal)
           || a.EndsWith("." + b, StringComparison.Ordinal)
           || b.EndsWith("." + a, StringComparison.Ordinal);
}
