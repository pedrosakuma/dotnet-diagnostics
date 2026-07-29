using System.Text;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class AgentResponseInterpreter
{
    private static readonly IReadOnlyList<string> HedgeMarkers =
    [
        "likely",
        "possibly",
        "appears to",
        "may be",
        "might be",
        "seems",
        "suggests",
        "probably",
        "could be",
    ];

    private static readonly IReadOnlyList<string> AssertiveMarkers =
    [
        "definitely",
        "clearly",
        "confirmed",
        "certainly",
        "root cause is",
        "is caused by",
        "this is",
        "proves",
        "without doubt",
    ];

    private static readonly IReadOnlySet<string> StopWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "a",
        "an",
        "and",
        "by",
        "for",
        "in",
        "is",
        "of",
        "or",
        "the",
        "to",
        "with",
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string[]>> ExtraEvidenceAliases =
        new Dictionary<string, IReadOnlyDictionary<string, string[]>>(StringComparer.Ordinal)
        {
            ["culture-lookup"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["cpu-self-time-signal"] = ["cpu hotspot", "self time", "exclusive cpu"],
                ["globalization-hash-leaf"] = ["globalization hash", "culture aware hash", "InvariantCultureIgnoreCase", "CompareInfo"],
            },
            ["sync-over-async"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["cpu-remains-low"] = ["cpu remains low", "not cpu bound", "cpu is low", "cpu is not saturated"],
                ["threadpool-backlog"] = ["threadpool queue", "queue backlog", "queue keeps growing", "queue growth"],
                ["blocking-wait-frames"] = ["sync over async", "GetAwaiter().GetResult", "GetResult", "blocking wait", "blocked thread"],
            },
            ["lock-storm"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["monitor-wait-concentration"] = ["contended monitor", "monitor contention", "lock contention"],
                ["owner-overlap-signal"] = ["owner overlap", "waiting on the same owner", "thread overlap"],
                ["sleeping-owner-with-waiters"] = ["sleeping owner", "holds the monitor while sleeping", "owner thread sleeps"],
            },
            ["gc-storm"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["gen2-counter-elevated"] = ["gen2 collections", "gen 2 collections", "full gc"],
                ["loh-size-elevated"] = ["loh", "large object heap", "large-object heap"],
                ["gen2-share-signal"] = ["gen2 share", "gen 2 share", "gc pressure", "gc pause pressure"],
            },
        };

    private static readonly IReadOnlyDictionary<string, ScenarioResponseHeuristics> ScenarioHeuristics =
        new Dictionary<string, ScenarioResponseHeuristics>(StringComparer.Ordinal)
        {
            ["culture-lookup"] = new(
                Hypotheses: new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["cpu-expensive-globalization-hash"] = ["globalization hash", "culture aware hash", "CompareInfo", "InvariantCultureIgnoreCase", "IcuGetHashCodeOfString"],
                    ["threadpool-runtime-overhead"] = ["threadpool overhead", "runtime overhead"],
                    ["scale-compute"] = ["scale out", "more compute", "add cpu"],
                },
                Attributions: new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["System.Globalization.CompareInfo.GetHashCodeOfString"] = ["CompareInfo", "GetHashCodeOfString", "IcuGetHashCodeOfString", "NlsGetHashCodeOfString"],
                },
                NextActions: new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["query-cpu-top-methods-exclusive"] = ["top methods", "exclusive", "self time", "cpu top methods"],
                }),
            ["sync-over-async"] = new(
                Hypotheses: new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["threadpool-starvation-from-sync-over-async"] = ["sync over async", "threadpool starvation", "GetAwaiter().GetResult", "blocking threadpool workers"],
                    ["cpu-compute-demand"] = ["cpu bound", "compute demand", "high cpu"],
                    ["scale-out"] = ["scale out", "add instances", "more compute"],
                },
                Attributions: new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["System.Runtime.CompilerServices.TaskAwaiter.GetResult"] = ["GetAwaiter().GetResult", "TaskAwaiter.GetResult", "GetResult", "SpinThenBlockingWait", "Task.Wait"],
                },
                NextActions: new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["inspect-blocked-thread-stacks"] = ["blocked thread stacks", "inspect stacks", "blocked stacks", "thread stacks"],
                }),
            ["lock-storm"] = new(
                Hypotheses: new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["sleeping-monitor-owner-serializes-work"] = ["sleeping owner", "holding the monitor while sleeping", "monitor owner sleeps", "serializes work"],
                    ["external-io-wait"] = ["io wait", "external i/o"],
                    ["gc-pause"] = ["gc pause", "garbage collection pause"],
                },
                Attributions: new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["monitor-owner-thread-sleep"] = ["Thread.Sleep", "owner thread sleep", "sleep while holding the monitor", "sleeping owner"],
                },
                NextActions: new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["query-lock-graph"] = ["lock graph", "owner waiter graph", "query lock graph"],
                }),
            ["gc-storm"] = new(
                Hypotheses: new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["gc-pause-pressure-from-loh-churn"] = ["loh churn", "large object heap", "gen2 collections", "gc pause pressure"],
                    ["cpu-hot-loop"] = ["cpu hot loop", "hot loop"],
                    ["thread-wait-contention"] = ["thread contention", "wait contention"],
                },
                Attributions: new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["BadCodeSample /loh-alloc large-object allocations"] = ["/loh-alloc", "large object allocations", "loh allocations"],
                },
                NextActions: new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["inspect-gc-timeline-and-generation-breakdown"] = ["gc timeline", "generation breakdown", "gc breakdown"],
                }),
        };

    public AgentResponseInterpretation Interpret(string scenarioId, string freeTextResponse)
        => Interpret(new AgentResponseMappingRequest(scenarioId, freeTextResponse));

    public AgentResponseInterpretation Interpret(AgentResponseMappingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var manifest = ScenarioManifestLoader.LoadAll().Single(item => string.Equals(item.Id, request.ScenarioId, StringComparison.Ordinal));
        var evidenceFixturePath = ResolveEvidenceFixturePath(request.ScenarioId, request.EvidenceFixturePath);
        var evidence = ScenarioJson.ReadEvidence(evidenceFixturePath);
        return Interpret(manifest, evidence, evidenceFixturePath, request.FreeTextResponse);
    }

    internal AgentResponseInterpretation Interpret(
        ScenarioManifest manifest,
        ScenarioEvidence evidence,
        string evidenceFixturePath,
        string freeTextResponse)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceFixturePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(freeTextResponse);

        var normalizedResponse = Normalize(freeTextResponse);
        var citations = BuildCitationCandidates(manifest, evidence)
            .Select(candidate => CreateCitation(candidate, normalizedResponse))
            .Where(citation => citation is not null)
            .Cast<AgentEvidenceCitation>()
            .OrderBy(citation => citation.EvidencePath, StringComparer.Ordinal)
            .ToArray();
        var mappedEvidenceIds = citations
            .SelectMany(citation => citation.SupportedEvidenceIds)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var heuristic = ScenarioHeuristics[manifest.Id];
        var hypothesisIds = MapHeuristicSelections(normalizedResponse, heuristic.Hypotheses);
        var attributionIds = MapHeuristicSelections(normalizedResponse, heuristic.Attributions);
        var nextActionIds = MapHeuristicSelections(normalizedResponse, heuristic.NextActions);
        var conclusionIds = manifest.ForbiddenConclusions
            .Where(id => hypothesisIds.Contains(id, StringComparer.Ordinal))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var uncertainty = DetectUncertainty(normalizedResponse);
        var causalityPosture = mappedEvidenceIds.Length > 0 && hypothesisIds.Length > 0
            ? manifest.RequiredCausalityPosture
            : "unmapped";

        return new AgentResponseInterpretation(
            manifest.Id,
            evidenceFixturePath,
            new StructuredInterpretation(
                mappedEvidenceIds,
                hypothesisIds,
                attributionIds,
                nextActionIds,
                causalityPosture,
                conclusionIds),
            citations,
            uncertainty,
            [
                "Prototype only: citations and structured fields come from substring/keyword heuristics over one evidence fixture.",
                "The mapper is advisory and is not wired into any pass/fail CI gate.",
            ]);
    }

    private static IReadOnlyList<CitationCandidate> BuildCitationCandidates(ScenarioManifest manifest, ScenarioEvidence evidence)
        => manifest.ExpectedEvidence
            .SelectMany(invariant => BuildCitationCandidates(manifest.Id, invariant, evidence))
            .ToArray();

    private static IEnumerable<CitationCandidate> BuildCitationCandidates(string scenarioId, EvidenceInvariant invariant, ScenarioEvidence evidence)
    {
        var extraAliases = ExtraEvidenceAliases.TryGetValue(scenarioId, out var byScenario)
            && byScenario.TryGetValue(invariant.Id, out var aliases)
            ? aliases
            : [];

        switch (invariant.Kind)
        {
            case EvidenceInvariantKind.SignalPresent:
                foreach (var signal in evidence.Signals.Where(item => string.Equals(item.Signal, invariant.Signal, StringComparison.Ordinal)))
                {
                    var matchedBuckets = signal.Buckets.Select(bucket => bucket.Key).ToArray();
                    yield return new CitationCandidate(
                        $"signals[signal={signal.Signal}]",
                        $"signal {signal.Signal} (salience={signal.Salience:0.###})",
                        [invariant.Id],
                        BuildAliases(invariant.Signal, extraAliases, matchedBuckets));
                }

                break;

            case EvidenceInvariantKind.SignalBucketMatch:
                foreach (var signal in evidence.Signals.Where(item => string.Equals(item.Signal, invariant.Signal, StringComparison.Ordinal)))
                {
                    foreach (var bucket in signal.Buckets.Where(bucket => MatchesTerms(bucket.Key, invariant.ContainsAny!)))
                    {
                        yield return new CitationCandidate(
                            $"signals[signal={signal.Signal}].buckets[key={bucket.Key}]",
                            $"bucket {bucket.Key}={bucket.Magnitude:0.###}{FormatUnit(bucket.Unit)}",
                            [invariant.Id],
                            BuildAliases(invariant.Signal, extraAliases, [bucket.Key, .. invariant.ContainsAny!]));
                    }
                }

                break;

            case EvidenceInvariantKind.CounterComparison:
                var metric = evidence.Metrics.FirstOrDefault(item => string.Equals(item.Name, invariant.Metric, StringComparison.Ordinal));
                if (metric is not null)
                {
                    yield return new CitationCandidate(
                        $"metrics[name={metric.Name}]",
                        $"metric {metric.Name}={metric.Value:0.###}{FormatUnit(metric.Unit)}",
                        [invariant.Id],
                        BuildAliases(metric.Name, extraAliases));
                }

                break;

            case EvidenceInvariantKind.StackFrameMatch:
                foreach (var frame in evidence.Frames.Where(frame => MatchesTerms(frame.DisplayName, invariant.ContainsAny!)))
                {
                    yield return new CitationCandidate(
                        $"frames[displayName={frame.DisplayName}]",
                        $"frame {frame.DisplayName} matched {frame.MatchCount} time(s)",
                        [invariant.Id],
                        BuildAliases(frame.DisplayName, extraAliases, invariant.ContainsAny!));
                }

                break;

            case EvidenceInvariantKind.ThreadOwnerCorrelation:
                foreach (var relation in evidence.Relations.Where(item =>
                             string.Equals(item.Relation, invariant.Relation, StringComparison.Ordinal)
                             && string.Equals(item.OwnerWaitReason, invariant.OwnerWaitReason, StringComparison.Ordinal)))
                {
                    yield return new CitationCandidate(
                        $"relations[relation={relation.Relation},ownerWaitReason={relation.OwnerWaitReason}]",
                        $"relation {relation.Relation} with {relation.WaitingThreadCount} waiter(s) and owner wait reason {relation.OwnerWaitReason}",
                        [invariant.Id],
                        BuildAliases(relation.Relation, extraAliases, [relation.OwnerWaitReason, "waiters", "owner"]));
                }

                break;
        }
    }

    private static AgentEvidenceCitation? CreateCitation(CitationCandidate candidate, string normalizedResponse)
    {
        var matchedTerms = candidate.Aliases
            .Select(alias => Normalize(alias))
            .Where(alias => alias.Length > 0 && ContainsAlias(normalizedResponse, alias))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(alias => alias, StringComparer.Ordinal)
            .ToArray();
        return matchedTerms.Length == 0
            ? null
            : new AgentEvidenceCitation(candidate.EvidencePath, candidate.Summary, matchedTerms, candidate.SupportedEvidenceIds);
    }

    private static AgentResponseUncertainty DetectUncertainty(string normalizedResponse)
    {
        var matchedHedges = HedgeMarkers
            .Where(marker => normalizedResponse.Contains(Normalize(marker), StringComparison.Ordinal))
            .OrderBy(marker => marker, StringComparer.Ordinal)
            .ToArray();
        var matchedAssertions = AssertiveMarkers
            .Where(marker => normalizedResponse.Contains(Normalize(marker), StringComparison.Ordinal))
            .OrderBy(marker => marker, StringComparer.Ordinal)
            .ToArray();

        var disposition = matchedHedges.Length switch
        {
            > 0 when matchedAssertions.Length > 0 => AgentResponseUncertaintyDisposition.Mixed,
            > 0 => AgentResponseUncertaintyDisposition.Hedged,
            _ when matchedAssertions.Length > 0 => AgentResponseUncertaintyDisposition.Assertive,
            _ => AgentResponseUncertaintyDisposition.NoneDetected,
        };

        return new AgentResponseUncertainty(disposition, matchedHedges, matchedAssertions);
    }

    private static string[] MapHeuristicSelections(string normalizedResponse, IReadOnlyDictionary<string, string[]> candidates)
        => candidates
            .Where(entry => entry.Value.Select(Normalize).Any(alias => ContainsAlias(normalizedResponse, alias)))
            .Select(entry => entry.Key)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

    private static string ResolveEvidenceFixturePath(string scenarioId, string? evidenceFixturePath)
    {
        if (!string.IsNullOrWhiteSpace(evidenceFixturePath))
        {
            return Path.IsPathRooted(evidenceFixturePath)
                ? evidenceFixturePath
                : ScenarioManifestLoader.ScenarioPath("Fixtures", evidenceFixturePath);
        }

        var windowsFixture = ScenarioManifestLoader.ScenarioPath("Fixtures", $"{scenarioId}.windows.evidence.json");
        if (File.Exists(windowsFixture))
        {
            return windowsFixture;
        }

        return Directory
            .EnumerateFiles(ScenarioManifestLoader.ScenarioPath("Fixtures"), $"{scenarioId}.*.evidence.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new FileNotFoundException($"No evidence fixture was found for scenario '{scenarioId}'.");
    }

    private static string[] BuildAliases(string primary, IReadOnlyList<string> extras, IReadOnlyList<string>? secondary = null)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            primary,
            Humanize(primary),
            ShortName(primary),
        };

        foreach (var value in extras.Concat(secondary ?? []))
        {
            aliases.Add(value);
            aliases.Add(Humanize(value));
            aliases.Add(ShortName(value));
        }

        return aliases.Where(alias => !string.IsNullOrWhiteSpace(alias)).ToArray();
    }

    private static bool MatchesTerms(string value, IReadOnlyList<string> terms)
        => terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsAlias(string normalizedResponse, string normalizedAlias)
    {
        if (normalizedResponse.Contains(normalizedAlias, StringComparison.Ordinal))
        {
            return true;
        }

        var tokens = normalizedAlias
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 3 && !StopWords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (tokens.Length == 0)
        {
            return false;
        }

        if (tokens.Length == 1)
        {
            return normalizedResponse.Contains(tokens[0], StringComparison.Ordinal);
        }

        return tokens.Count(token => normalizedResponse.Contains(token, StringComparison.Ordinal)) >= 2;
    }

    private static string Humanize(string value)
        => value
            .Replace(".", " ", StringComparison.Ordinal)
            .Replace("-", " ", StringComparison.Ordinal)
            .Replace("_", " ", StringComparison.Ordinal)
            .Replace("(", " ", StringComparison.Ordinal)
            .Replace(")", " ", StringComparison.Ordinal)
            .Replace(",", " ", StringComparison.Ordinal)
            .Trim();

    private static string ShortName(string value)
    {
        var lastDot = value.LastIndexOf('.');
        var shortName = lastDot >= 0 ? value[(lastDot + 1)..] : value;
        var parameterList = shortName.IndexOf('(');
        return parameterList >= 0 ? shortName[..parameterList] : shortName;
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasSpace = false;
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                previousWasSpace = false;
            }
            else if (!previousWasSpace)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    private static string FormatUnit(string? unit) => string.IsNullOrWhiteSpace(unit) ? string.Empty : $" {unit}";

    private sealed record CitationCandidate(
        string EvidencePath,
        string Summary,
        IReadOnlyList<string> SupportedEvidenceIds,
        IReadOnlyList<string> Aliases);

    private sealed record ScenarioResponseHeuristics(
        IReadOnlyDictionary<string, string[]> Hypotheses,
        IReadOnlyDictionary<string, string[]> Attributions,
        IReadOnlyDictionary<string, string[]> NextActions);
}
