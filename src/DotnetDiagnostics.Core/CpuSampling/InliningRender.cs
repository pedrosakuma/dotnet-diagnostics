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
    // runtime's inlining events carry names only (no signature, no address), so this is a name-based hint:
    // namespace, type and method must match exactly after generic arity markers are normalized. When the
    // name cannot identify one method (overloads sampled in the profile, or several distinct inlinee names
    // collapsing to the same normalized name) the hint is flagged ambiguous instead of asserted.
    private static List<CodeVersionSampleRow> AnnotateInlinedInto(
        IEnumerable<CodeVersionSampleRow> rows, IReadOnlyList<CodeVersionSampleRow> allRows, InliningProfile? inlining)
    {
        var list = rows.ToList();
        if (inlining is null || inlining.Decisions.Count == 0)
        {
            return list;
        }

        var byInlinee = new Dictionary<string, (SortedSet<string> Versions, HashSet<string> RawNames)>(StringComparer.Ordinal);
        foreach (var d in inlining.Decisions)
        {
            if (!d.Succeeded)
            {
                continue;
            }

            var key = NormalizeName(d.Inlinee);
            if (!byInlinee.TryGetValue(key, out var entry))
            {
                entry = (new SortedSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
                byInlinee[key] = entry;
            }

            entry.Versions.Add($"{d.CompiledMethod} [{d.OptimizationTier}] ({d.VersionId})");
            entry.RawNames.Add(d.Inlinee);
        }

        var signaturesByName = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var r in allRows)
        {
            var key = NormalizeName(StripSignature(r.Method));
            if (!signaturesByName.TryGetValue(key, out var sigs))
            {
                sigs = new HashSet<string>(StringComparer.Ordinal);
                signaturesByName[key] = sigs;
            }

            sigs.Add(r.Method);
        }

        for (var i = 0; i < list.Count; i++)
        {
            var key = NormalizeName(StripSignature(list[i].Method));
            if (!byInlinee.TryGetValue(key, out var entry))
            {
                continue;
            }

            var ambiguous = entry.RawNames.Count > 1
                || (signaturesByName.TryGetValue(key, out var sigs) && sigs.Count > 1);
            list[i] = list[i] with
            {
                InlinedInto = entry.Versions.Take(MaxInlinedIntoPerRow).ToArray(),
                InlinedIntoAmbiguous = ambiguous ? true : null,
            };
        }

        return list;
    }

    private static string StripSignature(string method)
    {
        var paren = method.IndexOf('(', StringComparison.Ordinal);
        return paren < 0 ? method : method[..paren];
    }

    // Removes generic arity markers (`1, `2) so App.Svc`1.Run and App.Svc.Run compare equal.
    private static string NormalizeName(string name)
    {
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        if (tick < 0)
        {
            return name;
        }

        var sb = new System.Text.StringBuilder(name.Length);
        for (var i = 0; i < name.Length; i++)
        {
            if (name[i] == '`')
            {
                while (i + 1 < name.Length && char.IsAsciiDigit(name[i + 1]))
                {
                    i++;
                }

                continue;
            }

            sb.Append(name[i]);
        }

        return sb.ToString();
    }
}
