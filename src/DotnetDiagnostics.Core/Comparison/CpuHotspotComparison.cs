using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DotnetDiagnostics.Core.CpuSampling;

namespace DotnetDiagnostics.Core.Comparison;

public static class CpuHotspotCaptureProjection
{
    public const int MaximumMethodsPerCapture = 16;

    public static CpuHotspotCaptureProfile Project(CpuSampleTraceArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var rows = CpuSampleComparableProjection.ProjectTyped(artifact, "cpu-sample")
            .Select(row =>
            {
                var key = ComparableKeyFactory.ForMethod("cpu-sample", row.Key.Symbol, row.Key.Identity);
                return new
                {
                    MethodId = key.ExactId ?? key.StableId,
                    DisplayName = row.Key.Symbol.MethodFullName,
                    row.Value.InclusiveSamples,
                    row.Value.ExclusiveSamples,
                };
            })
            .OrderByDescending(static row => row.InclusiveSamples)
            .ThenBy(static row => row.MethodId, StringComparer.Ordinal)
            .ToArray();
        var retained = rows.Take(MaximumMethodsPerCapture)
            .Select(static row => new CpuHotspotMethodObservation(
                row.MethodId,
                row.DisplayName,
                row.InclusiveSamples))
            .ToArray();
        var unresolved = rows
            .Where(static row => CpuSampleComparableProjection.IsUnresolved(row.DisplayName))
            .Sum(static row => row.ExclusiveSamples);

        return new(
            CpuHotspotCaptureProfile.SchemaV1,
            artifact.ProcessId,
            artifact.StartedAt,
            Math.Round(artifact.Duration.TotalSeconds, 4),
            artifact.TotalSamples,
            unresolved,
            artifact.Evidence?.Backend ?? CpuSampleBackend.LegacyUnknown,
            artifact.Evidence?.Kind ?? CpuSampleEvidenceKind.LegacyUnknown,
            CpuHotspotRepresentation.Exact,
            rows.Length,
            rows.Length - retained.Length,
            retained);
    }

    public static CpuSampleTraceArtifact EnsureProfile(CpuSampleTraceArtifact artifact)
        => artifact.HotspotVariability is null
            ? artifact with { HotspotVariability = Project(artifact) }
            : artifact;
}

public static class CpuHotspotComparison
{
    public const int MaximumCapturesPerSide = 12;
    public const int MaximumSerializedCaptureSetBytes = 256 * 1024;
    public const int MinimumCompletePairs = 4;
    public const long MinimumSamplesPerCapture = 80;
    public const int TopK = 2;
    public const double MaximumUnresolvedShare = 0.35;
    public const double MaximumPairedUnresolvedShareDelta = 0.20;

    private const string QuantileRule = "linear-r7-v1";

    public static CpuHotspotComparisonResult Compare(
        CpuHotspotCaptureSet baseline,
        CpuHotspotCaptureSet candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        Validate(baseline, nameof(baseline));
        Validate(candidate, nameof(candidate));

        var setMismatches = CompareIdentity(baseline.Identity, candidate.Identity);
        var pairs = PairCaptures(baseline.Captures, candidate.Captures);
        var pairMismatches = ComparePairs(pairs.Pairs, baseline.Identity, candidate.Identity);
        var independenceMismatches = CompareCaptureIndependence(
            baseline.Captures,
            candidate.Captures);
        var captureMismatches = pairMismatches.Concat(independenceMismatches).ToArray();
        var mismatches = setMismatches.Concat(captureMismatches).ToArray();
        var baselineMeasurements = baseline.Captures
            .OrderBy(static capture => capture.PairKey, StringComparer.Ordinal)
            .Select(capture => Measure(capture, baseline.Identity.Quantity.SelectedMethodId))
            .ToArray();
        var candidateMeasurements = candidate.Captures
            .OrderBy(static capture => capture.PairKey, StringComparer.Ordinal)
            .Select(capture => Measure(capture, candidate.Identity.Quantity.SelectedMethodId))
            .ToArray();
        var baselineCounts = Counts(baselineMeasurements);
        var candidateCounts = Counts(candidateMeasurements);
        var pairCounts = PairCounts(pairs, captureMismatches);
        var availability = DetermineAvailability(
            mismatches,
            pairs,
            baselineMeasurements,
            candidateMeasurements);
        var reason = UnavailableReason(
            availability,
            mismatches,
            pairs,
            baselineMeasurements,
            candidateMeasurements);
        var available = availability == CpuHotspotEvidenceAvailability.Available;

        var baselineSelectedShares = baselineMeasurements
            .Select(static capture => capture.SelectedMethodInclusiveShare)
            .Where(static share => share is not null)
            .Select(static share => share!.Value)
            .ToArray();
        var candidateSelectedShares = candidateMeasurements
            .Select(static capture => capture.SelectedMethodInclusiveShare)
            .Where(static share => share is not null)
            .Select(static share => share!.Value)
            .ToArray();
        var baselineUnresolved = baselineMeasurements
            .Select(static capture => capture.UnresolvedExclusiveShare)
            .Where(static share => share is not null)
            .Select(static share => share!.Value)
            .ToArray();
        var candidateUnresolved = candidateMeasurements
            .Select(static capture => capture.UnresolvedExclusiveShare)
            .Where(static share => share is not null)
            .Select(static share => share!.Value)
            .ToArray();
        var pairedDeltas = available
            ? pairs.Pairs
                .OrderBy(static pair => pair.Baseline.PairKey, StringComparer.Ordinal)
                .Select(pair =>
                {
                    var baselineShare = Share(pair.Baseline, baseline.Identity.Quantity.SelectedMethodId)!.Value;
                    var candidateShare = Share(pair.Candidate, candidate.Identity.Quantity.SelectedMethodId)!.Value;
                    return new CpuHotspotPairedShareDelta(
                        pair.Baseline.PairKey,
                        pair.Baseline.PairOrder,
                        baselineShare,
                        candidateShare,
                        candidateShare - baselineShare);
                })
                .ToArray()
            : [];
        var rankStability = available
            ? RankStability(
                pairs.Pairs,
                baseline.Identity.Quantity.SelectedMethodId,
                candidate.Identity.Quantity.SelectedMethodId)
            : null;
        var unavailableFields = available
            ? Array.Empty<CpuHotspotUnavailableField>()
            :
            [
                new("baselineSelectedShare", reason!),
                new("candidateSelectedShare", reason!),
                new("pairedShareDeltas", reason!),
                new("medianPairedShareDelta", reason!),
                new("rankStability", reason!),
            ];
        var provenance = Canonicalize(baseline, candidate, pairs.Pairs);

        var limitations = new List<string>
        {
            "One separately started and stopped complete capture is the independence unit; samples inside a capture are correlated observations.",
            "Sample counts and shares describe retained observations. They are not elapsed CPU time, throughput, work completed, performance impact, improvement, or regression.",
            "Candidate-minus-baseline share and rank deltas are objective descriptive movement; no significance, causation, health, improvement, or regression verdict is emitted.",
            "Top-K membership and selected-method rank use the bounded exact retained method inventory. A selected method outside that inventory is unavailable, not zero.",
        };
        if (mismatches.Length > 0)
        {
            limitations.Add("Required compatibility dimensions differ; capture observations remain visible but are not pooled.");
        }
        if (baselineMeasurements.Concat(candidateMeasurements).Any(static capture =>
                capture.Representation != CpuHotspotRepresentation.Exact))
        {
            limitations.Add("Projected or truncated method inventories are descriptive only and are not admitted by the v1 comparison.");
        }
        if (ExceedsUnresolvedGate(pairs.Pairs))
        {
            limitations.Add("Method movement and rank are withheld because unresolved exclusive samples could materially change attribution.");
        }

        return new(
            CpuHotspotComparisonResult.SchemaV1,
            availability,
            reason,
            baseline.Identity,
            candidate.Identity,
            mismatches,
            baselineCounts,
            candidateCounts,
            pairCounts,
            new(
                "capture-median-v1",
                "capture-r7-iqr-and-range-v1",
                "median-candidate-minus-baseline-capture-share-delta-v1",
                "capture-top-k-overlap-v1",
                "selected-method-rank-delta-v1",
                "capture",
                MinimumCompletePairs,
                MinimumSamplesPerCapture,
                CpuHotspotCaptureProjection.MaximumMethodsPerCapture,
                TopK,
                MaximumUnresolvedShare,
                MaximumPairedUnresolvedShareDelta,
                QuantileRule),
            baselineMeasurements,
            candidateMeasurements,
            available ? Distribution(baselineSelectedShares) : EmptyDistribution(),
            available ? Distribution(candidateSelectedShares) : EmptyDistribution(),
            Distribution(baselineUnresolved),
            Distribution(candidateUnresolved),
            pairedDeltas,
            available ? Median(pairedDeltas.Select(static delta => delta.CandidateMinusBaseline).ToArray()) : null,
            rankStability,
            unavailableFields,
            provenance,
            [
                "Compatible arms use the same runtime/platform, backend/evidence semantics, capture protocol, selected quantity, offered load, workload identity, and pairing contract.",
                "Each matched baseline/candidate capture pair belongs to the same declared process run.",
                "Capture pairs are non-overlapping and independently started; no underlying EventPipe session is reused across captures.",
            ],
            limitations);
    }

    private static CpuHotspotEvidenceAvailability DetermineAvailability(
        CpuHotspotIdentityMismatch[] mismatches,
        PairingResult pairs,
        CpuHotspotCaptureMeasurement[] baseline,
        CpuHotspotCaptureMeasurement[] candidate)
    {
        if (mismatches.Length > 0)
        {
            return CpuHotspotEvidenceAvailability.Incompatible;
        }
        if (baseline.Concat(candidate).Any(static capture =>
                capture.Representation != CpuHotspotRepresentation.Exact)
            || ExceedsUnresolvedGate(pairs.Pairs))
        {
            return CpuHotspotEvidenceAvailability.UnsupportedApproximation;
        }
        if (pairs.Unpaired > 0
            || pairs.Pairs.Count < MinimumCompletePairs
            || baseline.Concat(candidate).Any(static capture =>
                capture.TotalSamples < MinimumSamplesPerCapture
                || capture.SelectedMethodInclusiveSamples is null)
            || baseline.Concat(candidate).Any(static capture =>
                capture.RetainedMethods < TopK))
        {
            return CpuHotspotEvidenceAvailability.InsufficientObservations;
        }

        return CpuHotspotEvidenceAvailability.Available;
    }

    private static string? UnavailableReason(
        CpuHotspotEvidenceAvailability availability,
        CpuHotspotIdentityMismatch[] mismatches,
        PairingResult pairs,
        CpuHotspotCaptureMeasurement[] baseline,
        CpuHotspotCaptureMeasurement[] candidate)
        => availability switch
        {
            CpuHotspotEvidenceAvailability.Available => null,
            CpuHotspotEvidenceAvailability.Incompatible when mismatches.Any(static mismatch =>
                mismatch.Reason == "legacy_contract_missing") => "legacy_contract_missing",
            CpuHotspotEvidenceAvailability.Incompatible when mismatches.Any(static mismatch =>
                mismatch.Field == "pairOrder") => "pair_order_mismatch",
            CpuHotspotEvidenceAvailability.Incompatible when mismatches.Any(static mismatch =>
                mismatch.Field == "processRunId") => "process_run_pairing_mismatch",
            CpuHotspotEvidenceAvailability.Incompatible when mismatches.Any(static mismatch =>
                mismatch.Field == "processId") => "process_identity_pairing_mismatch",
            CpuHotspotEvidenceAvailability.Incompatible when mismatches.Any(static mismatch =>
                mismatch.Field == "captureInterval") => "overlapping_capture_intervals",
            CpuHotspotEvidenceAvailability.Incompatible => "compatibility_identity_mismatch",
            CpuHotspotEvidenceAvailability.UnsupportedApproximation when ExceedsUnresolvedGate(pairs.Pairs) =>
                "unresolved_symbol_share_exceeds_protocol",
            CpuHotspotEvidenceAvailability.UnsupportedApproximation =>
                "approximate_capture_representation_not_admitted",
            CpuHotspotEvidenceAvailability.InsufficientObservations when pairs.Unpaired > 0 =>
                "incomplete_capture_pairs",
            CpuHotspotEvidenceAvailability.InsufficientObservations when pairs.Pairs.Count < MinimumCompletePairs =>
                "minimum_complete_capture_pairs_not_met",
            CpuHotspotEvidenceAvailability.InsufficientObservations when baseline.Concat(candidate).Any(
                static capture => capture.TotalSamples < MinimumSamplesPerCapture) =>
                "minimum_capture_sample_count_not_met",
            CpuHotspotEvidenceAvailability.InsufficientObservations when baseline.Concat(candidate).Any(
                static capture => capture.SelectedMethodInclusiveSamples is null) =>
                "selected_method_not_observed_in_every_complete_pair",
            CpuHotspotEvidenceAvailability.InsufficientObservations =>
                "minimum_top_k_inventory_not_met",
            _ => "cpu_hotspot_evidence_unavailable",
        };

    private static CpuHotspotCaptureMeasurement Measure(
        CpuHotspotCaptureObservation observation,
        string selectedMethodId)
    {
        var ordered = OrderedMethods(observation.Capture);
        var selectedIndex = Array.FindIndex(
            ordered,
            method => string.Equals(method.MethodId, selectedMethodId, StringComparison.Ordinal));
        var selected = selectedIndex >= 0 ? ordered[selectedIndex] : null;
        return new(
            observation.Id,
            observation.PairKey,
            observation.PairOrder,
            observation.ProcessRunId,
            observation.Capture.ProcessId,
            observation.Capture.StartedAt,
            observation.Capture.DurationSeconds,
            observation.Capture.TotalSamples,
            observation.Capture.UnresolvedExclusiveSamples,
            Share(observation.Capture.UnresolvedExclusiveSamples, observation.Capture.TotalSamples),
            selected?.InclusiveSamples,
            selected is null
                ? null
                : Share(selected.InclusiveSamples, observation.Capture.TotalSamples),
            selectedIndex >= 0 ? selectedIndex + 1 : null,
            observation.Capture.Backend,
            observation.Capture.EvidenceKind,
            observation.Capture.Representation,
            observation.Capture.Methods.Count,
            observation.Capture.OmittedMethods);
    }

    private static CpuHotspotSideCounts Counts(CpuHotspotCaptureMeasurement[] captures)
        => new(
            captures.Length,
            SumChecked(captures.Select(static capture => capture.TotalSamples)),
            SumChecked(captures.Select(static capture => capture.UnresolvedExclusiveSamples)),
            captures.Count(static capture => capture.TotalSamples < MinimumSamplesPerCapture),
            captures.Count(static capture => capture.SelectedMethodInclusiveSamples is null),
            captures.Count(static capture =>
                capture.Representation != CpuHotspotRepresentation.Exact));

    private static CpuHotspotPairCounts PairCounts(
        PairingResult pairs,
        IReadOnlyList<CpuHotspotIdentityMismatch> mismatches)
    {
        var pairedKeys = pairs.Pairs.Select(static pair => pair.Baseline.PairKey)
            .ToHashSet(StringComparer.Ordinal);
        var invalidPairedKeys = mismatches.Select(static mismatch => mismatch.PairKey)
                .Where(static key => key is not null)
                .Select(static key => key!)
                .Where(pairedKeys.Contains)
                .Distinct(StringComparer.Ordinal)
                .Count();
        return new(
            pairs.Pairs.Count,
            pairs.Pairs.Count - invalidPairedKeys,
            pairs.Unpaired,
            mismatches.Count(static mismatch => mismatch.Field == "pairOrder"),
            mismatches.Count(static mismatch => mismatch.Field == "processRunId"),
            mismatches.Count(static mismatch => mismatch.Field == "processId"),
            mismatches.Count(static mismatch => mismatch.Field == "captureInterval"),
            mismatches.Count(static mismatch => mismatch.Field == "durationSeconds"),
            mismatches.Count(static mismatch => mismatch.Field == "backend"),
            mismatches.Count(static mismatch => mismatch.Field == "evidenceKind"));
    }

    private static List<CpuHotspotIdentityMismatch> CompareIdentity(
        CpuHotspotCompatibilityIdentity baseline,
        CpuHotspotCompatibilityIdentity candidate)
    {
        var mismatches = new List<CpuHotspotIdentityMismatch>();
        Compare(mismatches, "target.runtimeFlavor", baseline.Target.RuntimeFlavor, candidate.Target.RuntimeFlavor);
        Compare(mismatches, "target.runtimeVersionPolicy", baseline.Target.RuntimeVersionPolicy, candidate.Target.RuntimeVersionPolicy);
        Compare(mismatches, "target.operatingSystem", baseline.Target.OperatingSystem, candidate.Target.OperatingSystem);
        Compare(mismatches, "target.architecture", baseline.Target.Architecture, candidate.Target.Architecture);
        Compare(mismatches, "backend.id", baseline.Backend.Id, candidate.Backend.Id);
        Compare(mismatches, "backend.version", baseline.Backend.Version, candidate.Backend.Version);
        Compare(mismatches, "backend.evidenceKind", baseline.Backend.EvidenceKind.ToString(), candidate.Backend.EvidenceKind.ToString());
        Compare(mismatches, "quantity.id", baseline.Quantity.Id, candidate.Quantity.Id);
        Compare(mismatches, "quantity.unit", baseline.Quantity.Unit, candidate.Quantity.Unit);
        Compare(mismatches, "quantity.aggregation", baseline.Quantity.Aggregation, candidate.Quantity.Aggregation);
        Compare(mismatches, "quantity.normalization", baseline.Quantity.Normalization, candidate.Quantity.Normalization);
        Compare(mismatches, "quantity.filters", baseline.Quantity.Filters, candidate.Quantity.Filters);
        Compare(mismatches, "quantity.selectedMethodId", baseline.Quantity.SelectedMethodId, candidate.Quantity.SelectedMethodId);
        Compare(mismatches, "quantity.representation", baseline.Quantity.Representation.ToString(), candidate.Quantity.Representation.ToString());
        Compare(mismatches, "capture.durationSeconds", baseline.Capture.DurationSeconds, candidate.Capture.DurationSeconds);
        Compare(mismatches, "capture.samplingPeriod", baseline.Capture.SamplingPeriod, candidate.Capture.SamplingPeriod);
        Compare(mismatches, "capture.retention", baseline.Capture.Retention, candidate.Capture.Retention);
        Compare(mismatches, "capture.enrichment", baseline.Capture.Enrichment, candidate.Capture.Enrichment);
        Compare(mismatches, "workload.scenario", baseline.Workload.Scenario, candidate.Workload.Scenario);
        Compare(mismatches, "workload.version", baseline.Workload.Version, candidate.Workload.Version);
        Compare(mismatches, "workload.parameters", baseline.Workload.Parameters, candidate.Workload.Parameters);
        Compare(mismatches, "workload.offeredLoad", baseline.Workload.OfferedLoad, candidate.Workload.OfferedLoad);
        Compare(mismatches, "workload.deployment", baseline.Workload.Deployment, candidate.Workload.Deployment);
        Compare(mismatches, "independence.unit", baseline.Independence.Unit, candidate.Independence.Unit);
        Compare(mismatches, "independence.nesting", baseline.Independence.Nesting, candidate.Independence.Nesting);
        Compare(mismatches, "independence.pairingKey", baseline.Independence.PairingKey, candidate.Independence.PairingKey);
        Compare(mismatches, "comparisonAxis.name", baseline.ComparisonAxis.Name, candidate.ComparisonAxis.Name);
        return mismatches;
    }

    private static List<CpuHotspotIdentityMismatch> ComparePairs(
        IReadOnlyList<CapturePair> pairs,
        CpuHotspotCompatibilityIdentity baselineIdentity,
        CpuHotspotCompatibilityIdentity candidateIdentity)
    {
        var mismatches = new List<CpuHotspotIdentityMismatch>();
        foreach (var pair in pairs)
        {
            ComparePair(mismatches, pair, "pairOrder", pair.Baseline.PairOrder, pair.Candidate.PairOrder);
            ComparePair(mismatches, pair, "processRunId", pair.Baseline.ProcessRunId, pair.Candidate.ProcessRunId);
            ComparePair(mismatches, pair, "processId", pair.Baseline.Capture.ProcessId, pair.Candidate.Capture.ProcessId);
            ComparePair(mismatches, pair, "durationSeconds", pair.Baseline.Capture.DurationSeconds, pair.Candidate.Capture.DurationSeconds);
            ComparePair(mismatches, pair, "backend", pair.Baseline.Capture.Backend, pair.Candidate.Capture.Backend);
            ComparePair(mismatches, pair, "evidenceKind", pair.Baseline.Capture.EvidenceKind, pair.Candidate.Capture.EvidenceKind);

            ValidateCaptureIdentity(mismatches, pair.Baseline, baselineIdentity);
            ValidateCaptureIdentity(mismatches, pair.Candidate, candidateIdentity);
        }

        return mismatches;
    }

    private static List<CpuHotspotIdentityMismatch> CompareCaptureIndependence(
        IReadOnlyList<CpuHotspotCaptureObservation> baseline,
        IReadOnlyList<CpuHotspotCaptureObservation> candidate)
    {
        var mismatches = new List<CpuHotspotIdentityMismatch>();
        var captures = baseline.Concat(candidate)
            .GroupBy(static capture => capture.Capture.ProcessId);
        foreach (var processCaptures in captures)
        {
            var ordered = processCaptures
                .OrderBy(static capture => capture.Capture.StartedAt)
                .ThenBy(static capture => capture.PairKey, StringComparer.Ordinal)
                .ToArray();
            var active = ordered[0];
            var activeEnd = CaptureEnd(active);
            for (var index = 1; index < ordered.Length; index++)
            {
                var current = ordered[index];
                if (current.Capture.StartedAt < activeEnd)
                {
                    AddOverlapMismatch(mismatches, active, current);
                    if (!string.Equals(active.PairKey, current.PairKey, StringComparison.Ordinal))
                    {
                        AddOverlapMismatch(mismatches, current, active);
                    }
                }

                var currentEnd = CaptureEnd(current);
                if (currentEnd > activeEnd)
                {
                    active = current;
                    activeEnd = currentEnd;
                }
            }
        }

        return mismatches;
    }

    private static DateTimeOffset CaptureEnd(CpuHotspotCaptureObservation observation)
        => observation.Capture.StartedAt
            + TimeSpan.FromSeconds(observation.Capture.DurationSeconds);

    private static void AddOverlapMismatch(
        List<CpuHotspotIdentityMismatch> mismatches,
        CpuHotspotCaptureObservation capture,
        CpuHotspotCaptureObservation overlap)
        => mismatches.Add(new(
            "captureInterval",
            capture.PairKey,
            $"{capture.Capture.ProcessId}@{capture.Capture.StartedAt:O}+{capture.Capture.DurationSeconds:R}s",
            $"{overlap.Capture.ProcessId}@{overlap.Capture.StartedAt:O}+{overlap.Capture.DurationSeconds:R}s",
            "overlapping_capture_intervals"));

    private static void ValidateCaptureIdentity(
        List<CpuHotspotIdentityMismatch> mismatches,
        CpuHotspotCaptureObservation observation,
        CpuHotspotCompatibilityIdentity identity)
    {
        var capture = observation.Capture;
        if (capture.Backend == CpuSampleBackend.LegacyUnknown
            || capture.EvidenceKind == CpuSampleEvidenceKind.LegacyUnknown)
        {
            mismatches.Add(new(
                "capture.evidence",
                observation.PairKey,
                capture.Backend.ToString(),
                capture.EvidenceKind.ToString(),
                "legacy_contract_missing"));
        }
        if (capture.Backend.ToString() != identity.Backend.Id)
        {
            mismatches.Add(new(
                "capture.backend",
                observation.PairKey,
                identity.Backend.Id,
                capture.Backend.ToString(),
                "declared_identity_mismatch"));
        }
        if (capture.EvidenceKind != identity.Backend.EvidenceKind)
        {
            mismatches.Add(new(
                "capture.evidenceKind",
                observation.PairKey,
                identity.Backend.EvidenceKind.ToString(),
                capture.EvidenceKind.ToString(),
                "declared_identity_mismatch"));
        }
        if (capture.DurationSeconds != identity.Capture.DurationSeconds)
        {
            mismatches.Add(new(
                "capture.durationSeconds",
                observation.PairKey,
                identity.Capture.DurationSeconds.ToString("R", CultureInfo.InvariantCulture),
                capture.DurationSeconds.ToString("R", CultureInfo.InvariantCulture),
                "declared_identity_mismatch"));
        }
        if (capture.Representation != identity.Quantity.Representation)
        {
            mismatches.Add(new(
                "capture.representation",
                observation.PairKey,
                identity.Quantity.Representation.ToString(),
                capture.Representation.ToString(),
                "declared_identity_mismatch"));
        }
    }

    private static void Compare(
        List<CpuHotspotIdentityMismatch> mismatches,
        string field,
        string baseline,
        string candidate)
    {
        if (!string.Equals(baseline, candidate, StringComparison.Ordinal))
        {
            mismatches.Add(new(field, null, baseline, candidate, "value_mismatch"));
        }
    }

    private static void Compare(
        List<CpuHotspotIdentityMismatch> mismatches,
        string field,
        double baseline,
        double candidate)
    {
        if (baseline != candidate)
        {
            mismatches.Add(new(
                field,
                null,
                baseline.ToString("R", CultureInfo.InvariantCulture),
                candidate.ToString("R", CultureInfo.InvariantCulture),
                "value_mismatch"));
        }
    }

    private static void Compare(
        List<CpuHotspotIdentityMismatch> mismatches,
        string field,
        int baseline,
        int candidate)
    {
        if (baseline != candidate)
        {
            mismatches.Add(new(
                field,
                null,
                baseline.ToString(CultureInfo.InvariantCulture),
                candidate.ToString(CultureInfo.InvariantCulture),
                "value_mismatch"));
        }
    }

    private static void ComparePair<T>(
        List<CpuHotspotIdentityMismatch> mismatches,
        CapturePair pair,
        string field,
        T baseline,
        T candidate)
    {
        if (!EqualityComparer<T>.Default.Equals(baseline, candidate))
        {
            mismatches.Add(new(
                field,
                pair.Baseline.PairKey,
                Convert.ToString(baseline, CultureInfo.InvariantCulture),
                Convert.ToString(candidate, CultureInfo.InvariantCulture),
                "pair_value_mismatch"));
        }
    }

    private static PairingResult PairCaptures(
        IReadOnlyList<CpuHotspotCaptureObservation> baseline,
        IReadOnlyList<CpuHotspotCaptureObservation> candidate)
    {
        var baselineByKey = baseline.ToDictionary(static capture => capture.PairKey, StringComparer.Ordinal);
        var candidateByKey = candidate.ToDictionary(static capture => capture.PairKey, StringComparer.Ordinal);
        var keys = baselineByKey.Keys
            .Intersect(candidateByKey.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new(
            keys.Select(key => new CapturePair(baselineByKey[key], candidateByKey[key])).ToArray(),
            baseline.Count + candidate.Count - keys.Length * 2);
    }

    private static bool ExceedsUnresolvedGate(IReadOnlyList<CapturePair> pairs)
        => pairs.Any(pair =>
        {
            var baseline = Share(
                pair.Baseline.Capture.UnresolvedExclusiveSamples,
                pair.Baseline.Capture.TotalSamples);
            var candidate = Share(
                pair.Candidate.Capture.UnresolvedExclusiveSamples,
                pair.Candidate.Capture.TotalSamples);
            return baseline is not null
                && candidate is not null
                && (baseline > MaximumUnresolvedShare
                || candidate > MaximumUnresolvedShare
                || Math.Abs(candidate.Value - baseline.Value)
                    > MaximumPairedUnresolvedShareDelta);
        });

    private static CpuHotspotRankStability RankStability(
        IReadOnlyList<CapturePair> pairs,
        string baselineSelectedMethodId,
        string candidateSelectedMethodId)
    {
        var evidence = pairs
            .OrderBy(static pair => pair.Baseline.PairKey, StringComparer.Ordinal)
            .Select(pair =>
            {
                var baselineMethods = OrderedMethods(pair.Baseline.Capture);
                var candidateMethods = OrderedMethods(pair.Candidate.Capture);
                var baselineTop = baselineMethods.Take(TopK)
                    .Select(static method => method.MethodId)
                    .ToHashSet(StringComparer.Ordinal);
                var candidateTop = candidateMethods.Take(TopK)
                    .Select(static method => method.MethodId)
                    .ToHashSet(StringComparer.Ordinal);
                var overlap = baselineTop.Intersect(candidateTop, StringComparer.Ordinal).Count()
                    / (double)TopK;
                var baselineRank = Array.FindIndex(
                    baselineMethods,
                    method => string.Equals(method.MethodId, baselineSelectedMethodId, StringComparison.Ordinal)) + 1;
                var candidateRank = Array.FindIndex(
                    candidateMethods,
                    method => string.Equals(method.MethodId, candidateSelectedMethodId, StringComparison.Ordinal)) + 1;
                return new CpuHotspotPairRankEvidence(
                    pair.Baseline.PairKey,
                    overlap,
                    baselineRank,
                    candidateRank,
                    candidateRank - baselineRank,
                    Math.Abs(candidateRank - baselineRank));
            })
            .ToArray();
        return new(
            TopK,
            evidence.Length,
            Median(evidence.Select(static pair => pair.TopKOverlap).ToArray()),
            evidence.Count(static pair => pair.AbsoluteRankDelta == 0) / (double)evidence.Length,
            Median(evidence.Select(static pair => (double)pair.AbsoluteRankDelta).ToArray()),
            evidence);
    }

    private static CpuHotspotDistribution Distribution(double[] values)
    {
        if (values.Length == 0)
        {
            return EmptyDistribution();
        }

        var sorted = values.Order().ToArray();
        var q1 = QuantileR7(sorted, 0.25);
        var q3 = QuantileR7(sorted, 0.75);
        return new(
            MedianSorted(sorted),
            q1,
            q3,
            q3 - q1,
            sorted[0],
            sorted[^1]);
    }

    private static CpuHotspotDistribution EmptyDistribution()
        => new(null, null, null, null, null, null);

    private static CpuHotspotCalculationProvenance Canonicalize(
        CpuHotspotCaptureSet baseline,
        CpuHotspotCaptureSet candidate,
        IReadOnlyList<CapturePair> pairs)
    {
        var identity = CanonicalIdentity(baseline.Identity) + "\n" + CanonicalIdentity(candidate.Identity);
        var input = new StringBuilder();
        input.AppendLine("baseline");
        foreach (var capture in baseline.Captures.OrderBy(static capture => capture.PairKey, StringComparer.Ordinal))
        {
            AppendCapture(input, capture);
            input.AppendLine();
        }
        input.AppendLine("candidate");
        foreach (var capture in candidate.Captures.OrderBy(static capture => capture.PairKey, StringComparer.Ordinal))
        {
            AppendCapture(input, capture);
            input.AppendLine();
        }

        return new(
            Sha256(input.ToString()),
            Sha256(identity),
            pairs.Select(static pair => pair.Baseline.PairKey)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    private static string CanonicalIdentity(CpuHotspotCompatibilityIdentity identity)
    {
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["backend.evidenceKind"] = identity.Backend.EvidenceKind.ToString(),
            ["backend.id"] = identity.Backend.Id,
            ["backend.version"] = identity.Backend.Version,
            ["capture.durationSeconds"] = identity.Capture.DurationSeconds.ToString("R", CultureInfo.InvariantCulture),
            ["capture.enrichment"] = identity.Capture.Enrichment,
            ["capture.retention"] = identity.Capture.Retention,
            ["capture.samplingPeriod"] = identity.Capture.SamplingPeriod,
            ["comparisonAxis.name"] = identity.ComparisonAxis.Name,
            ["independence.nesting"] = identity.Independence.Nesting,
            ["independence.pairingKey"] = identity.Independence.PairingKey,
            ["independence.unit"] = identity.Independence.Unit,
            ["quantity.aggregation"] = identity.Quantity.Aggregation,
            ["quantity.filters"] = identity.Quantity.Filters,
            ["quantity.id"] = identity.Quantity.Id,
            ["quantity.normalization"] = identity.Quantity.Normalization,
            ["quantity.representation"] = identity.Quantity.Representation.ToString(),
            ["quantity.selectedMethodId"] = identity.Quantity.SelectedMethodId,
            ["quantity.unit"] = identity.Quantity.Unit,
            ["target.architecture"] = identity.Target.Architecture,
            ["target.operatingSystem"] = identity.Target.OperatingSystem,
            ["target.runtimeFlavor"] = identity.Target.RuntimeFlavor,
            ["target.runtimeVersionPolicy"] = identity.Target.RuntimeVersionPolicy,
            ["workload.deployment"] = identity.Workload.Deployment,
            ["workload.offeredLoad"] = identity.Workload.OfferedLoad.ToString(CultureInfo.InvariantCulture),
            ["workload.parameters"] = identity.Workload.Parameters,
            ["workload.scenario"] = identity.Workload.Scenario,
            ["workload.version"] = identity.Workload.Version,
        };
        return string.Join("\n", fields.Select(static field => $"{field.Key}={field.Value}"));
    }

    private static void AppendCapture(StringBuilder builder, CpuHotspotCaptureObservation observation)
    {
        builder.Append(observation.PairKey).Append('|')
            .Append(observation.PairOrder).Append('|')
            .Append(observation.ProcessRunId).Append('|')
            .Append(observation.Capture.TotalSamples).Append('|')
            .Append(observation.Capture.UnresolvedExclusiveSamples).Append('|');
        foreach (var method in OrderedMethods(observation.Capture))
        {
            builder.Append(method.MethodId).Append('=').Append(method.InclusiveSamples).Append(',');
        }
    }

    private static void Validate(CpuHotspotCaptureSet set, string parameterName)
    {
        if (!string.Equals(set.Schema, CpuHotspotCaptureSet.SchemaV1, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Expected schema '{CpuHotspotCaptureSet.SchemaV1}'.",
                parameterName);
        }
        if (string.IsNullOrWhiteSpace(set.Id))
        {
            throw new ArgumentException("Capture-set identity is required.", parameterName);
        }
        ValidateIdentity(set.Identity, parameterName);
        if (set.Captures is null || set.Captures.Count is < 1 or > MaximumCapturesPerSide)
        {
            throw new ArgumentException(
                $"Each side must contain 1..{MaximumCapturesPerSide} captures.",
                parameterName);
        }
        if (set.Captures.Any(static capture => capture is null))
        {
            throw new ArgumentException("Capture entries cannot be null.", parameterName);
        }
        if (set.Captures.Select(static capture => capture.Id).Distinct(StringComparer.Ordinal).Count()
                != set.Captures.Count
            || set.Captures.Select(static capture => capture.PairKey).Distinct(StringComparer.Ordinal).Count()
                != set.Captures.Count)
        {
            throw new ArgumentException(
                "Capture ids and pair keys must be unique within each side.",
                parameterName);
        }

        foreach (var observation in set.Captures)
        {
            if (string.IsNullOrWhiteSpace(observation.Id)
                || string.IsNullOrWhiteSpace(observation.PairKey)
                || string.IsNullOrWhiteSpace(observation.ProcessRunId))
            {
                throw new ArgumentException(
                    "Capture id, pair key, and process-run id are required.",
                    parameterName);
            }
            if (!Enum.IsDefined(observation.PairOrder))
            {
                throw new ArgumentException(
                    "Capture pair order must be a recognized v1 value.",
                    parameterName);
            }
            ValidateProfile(observation.Capture, parameterName);
        }

        _ = SumChecked(set.Captures.Select(static capture => capture.Capture.TotalSamples));
        _ = SumChecked(set.Captures.Select(static capture =>
            capture.Capture.UnresolvedExclusiveSamples));
    }

    private static void ValidateIdentity(
        CpuHotspotCompatibilityIdentity identity,
        string parameterName)
    {
        if (identity is null
            || identity.Target is null
            || identity.Backend is null
            || identity.Quantity is null
            || identity.Capture is null
            || identity.Workload is null
            || identity.Independence is null
            || identity.ComparisonAxis is null)
        {
            throw new ArgumentException(
                "Every CPU hotspot compatibility identity section is required.",
                parameterName);
        }
        var values = new[]
        {
            identity.Target.RuntimeFlavor,
            identity.Target.RuntimeVersionPolicy,
            identity.Target.OperatingSystem,
            identity.Target.Architecture,
            identity.Backend.Id,
            identity.Backend.Version,
            identity.Quantity.Id,
            identity.Quantity.Unit,
            identity.Quantity.Aggregation,
            identity.Quantity.Normalization,
            identity.Quantity.Filters,
            identity.Quantity.SelectedMethodId,
            identity.Capture.SamplingPeriod,
            identity.Capture.Retention,
            identity.Capture.Enrichment,
            identity.Workload.Scenario,
            identity.Workload.Version,
            identity.Workload.Parameters,
            identity.Workload.Deployment,
            identity.Independence.Unit,
            identity.Independence.Nesting,
            identity.Independence.PairingKey,
            identity.ComparisonAxis.Name,
            identity.ComparisonAxis.Value,
        };
        if (values.Any(string.IsNullOrWhiteSpace)
            || identity.Workload.OfferedLoad < 1
            || !double.IsFinite(identity.Capture.DurationSeconds)
            || identity.Capture.DurationSeconds <= 0
            || !Enum.IsDefined(identity.Backend.EvidenceKind)
            || !Enum.IsDefined(identity.Quantity.Representation))
        {
            throw new ArgumentException(
                "Every CPU hotspot compatibility identity field must be valid and non-empty.",
                parameterName);
        }
        if (!string.Equals(identity.Quantity.Id, "cpu-hotspot-inclusive-share", StringComparison.Ordinal)
            || !string.Equals(identity.Quantity.Unit, "sample-share", StringComparison.Ordinal)
            || !string.Equals(identity.Quantity.Aggregation, "inclusive-stack-membership", StringComparison.Ordinal)
            || !string.Equals(identity.Quantity.Normalization, "retained-total-samples", StringComparison.Ordinal)
            || !string.Equals(identity.Independence.Unit, "capture", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The v1 contract requires the inclusive sample-share quantity and capture independence unit.",
                parameterName);
        }
    }

    private static void ValidateProfile(CpuHotspotCaptureProfile profile, string parameterName)
    {
        if (profile is null
            || !string.Equals(profile.Schema, CpuHotspotCaptureProfile.SchemaV1, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Every capture requires schema '{CpuHotspotCaptureProfile.SchemaV1}'.",
                parameterName);
        }
        if (profile.ProcessId <= 0
            || profile.StartedAt == default
            || !double.IsFinite(profile.DurationSeconds)
            || profile.DurationSeconds <= 0
            || !HasValidCaptureEnd(profile)
            || profile.TotalSamples < 0
            || profile.UnresolvedExclusiveSamples < 0
            || profile.UnresolvedExclusiveSamples > profile.TotalSamples
            || profile.TotalMethods < 0
            || profile.OmittedMethods < 0
            || profile.Methods is null
            || profile.Methods.Count > CpuHotspotCaptureProjection.MaximumMethodsPerCapture
            || profile.TotalMethods != profile.Methods.Count + profile.OmittedMethods
            || !Enum.IsDefined(profile.Backend)
            || !Enum.IsDefined(profile.EvidenceKind)
            || !Enum.IsDefined(profile.Representation))
        {
            throw new ArgumentException(
                "CPU hotspot capture counts, duration, representation, and method bounds are invalid.",
                parameterName);
        }
        if (profile.Methods.Any(static method =>
                method is null
                || string.IsNullOrWhiteSpace(method.MethodId)
                || string.IsNullOrWhiteSpace(method.DisplayName)
                || method.InclusiveSamples < 0)
            || profile.Methods.Select(static method => method.MethodId)
                .Distinct(StringComparer.Ordinal).Count() != profile.Methods.Count
            || profile.Methods.Any(method => method.InclusiveSamples > profile.TotalSamples))
        {
            throw new ArgumentException(
                "CPU hotspot methods must have unique identities and bounded non-negative counts.",
                parameterName);
        }
    }

    private static bool HasValidCaptureEnd(CpuHotspotCaptureProfile profile)
    {
        try
        {
            _ = profile.StartedAt + TimeSpan.FromSeconds(profile.DurationSeconds);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static CpuHotspotMethodObservation[] OrderedMethods(CpuHotspotCaptureProfile profile)
        => profile.Methods
            .OrderByDescending(static method => method.InclusiveSamples)
            .ThenBy(static method => method.MethodId, StringComparer.Ordinal)
            .ToArray();

    private static double? Share(long samples, long total)
        => total <= 0 ? null : samples / (double)total;

    private static double? Share(CpuHotspotCaptureObservation observation, string methodId)
    {
        var method = observation.Capture.Methods.FirstOrDefault(
            method => string.Equals(method.MethodId, methodId, StringComparison.Ordinal));
        return method is null ? null : Share(method.InclusiveSamples, observation.Capture.TotalSamples);
    }

    private static long SumChecked(IEnumerable<long> values)
    {
        try
        {
            return values.Aggregate(0L, static (total, value) => checked(total + value));
        }
        catch (OverflowException exception)
        {
            throw new ArgumentException(
                "Aggregate CPU hotspot counts must fit a signed 64-bit integer.",
                exception);
        }
    }

    private static double Median(IReadOnlyList<double> values)
        => MedianSorted(values.Order().ToArray());

    private static double MedianSorted(double[] sorted)
    {
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? sorted[middle - 1] / 2 + sorted[middle] / 2
            : sorted[middle];
    }

    private static double QuantileR7(double[] sorted, double probability)
    {
        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        var position = (sorted.Length - 1) * probability;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper || sorted[lower] == sorted[upper])
        {
            return sorted[lower];
        }

        var fraction = position - lower;
        return (1 - fraction) * sorted[lower] + fraction * sorted[upper];
    }

    private static string Sha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private sealed record CapturePair(
        CpuHotspotCaptureObservation Baseline,
        CpuHotspotCaptureObservation Candidate);

    private sealed record PairingResult(
        IReadOnlyList<CapturePair> Pairs,
        int Unpaired);
}
