using System.Globalization;
using System.Text.Json;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public static class CpuHotspotMovementEvaluator
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumFixtureBytes = 512 * 1024;
    public const string ProtocolId = "cpu-hotspot-movement-characterization";
    public const string ProtocolVersion = "1";

    private static readonly string[] RequiredCaseIds =
    [
        "aa-control",
        "deterministic-hotspot-shift",
        "changed-offered-load",
        "unresolved-symbol-degradation",
        "backend-incompatibility",
        "low-sample-count",
    ];

    public static CpuHotspotMovementReport Evaluate(CpuHotspotMovementFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ValidateFixture(fixture);

        var cases = fixture.Cases.Select(EvaluateCase).ToArray();
        var byId = cases.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var matrixComplete = RequiredCaseIds.All(byId.ContainsKey)
            && byId.Count == RequiredCaseIds.Length;
        var aa = matrixComplete ? byId["aa-control"] : null;
        var shift = matrixComplete ? byId["deterministic-hotspot-shift"] : null;
        var aaEnvelope = aa?.PairedShareDeltas.Select(Math.Abs).DefaultIfEmpty().Max();
        var shiftMagnitude = Math.Abs(shift?.PairedShareDelta?.Delta?.Absolute ?? 0);
        var edgeCasesAbstain = matrixComplete
            && byId["changed-offered-load"].Availability
                == ScenarioStatisticalAvailability.Incompatible
            && byId["unresolved-symbol-degradation"].Availability
                == ScenarioStatisticalAvailability.UnsupportedApproximation
            && byId["backend-incompatibility"].Availability
                == ScenarioStatisticalAvailability.Incompatible
            && byId["low-sample-count"].Availability
                == ScenarioStatisticalAvailability.InsufficientObservations
            && byId.Values
                .Where(item => item.Id is not ("aa-control" or "deterministic-hotspot-shift"))
                .All(item => item.NextAction == CpuHotspotNextAction.Inconclusive);
        var go = matrixComplete
            && aa!.Availability == ScenarioStatisticalAvailability.Available
            && aa.NextAction == CpuHotspotNextAction.NoMovementEstablished
            && aa.RankStability.SameSelectedRankFraction == 1
            && shift!.Availability == ScenarioStatisticalAvailability.Available
            && shift.NextAction == CpuHotspotNextAction.MovementObservedCollectDrilldown
            && shift.RankStability.SameSelectedRankFraction is < 1
            && shift.RankStability.MedianAbsoluteSelectedRankDelta is >= 1
            && aaEnvelope is not null
            && shiftMagnitude >= aaEnvelope.Value
                + CpuHotspotMovementBounds.MinimumSeparationBeyondAaRange
            && edgeCasesAbstain;

        return new(
            CurrentSchemaVersion,
            fixture.Id,
            go ? CpuHotspotMovementVerdict.Go : CpuHotspotMovementVerdict.NoGo,
            cases,
            go
                ?
                [
                    "The deterministic hotspot shift exceeded the complete paired-capture A/A delta envelope under the declared capture protocol.",
                    "Absolute capture sample counts, unresolved share, share medians, IQR/range, paired deltas, top-K overlap, and selected-method rank movement remain visible as separate evidence.",
                    "Changed offered load, unresolved-symbol degradation, backend/evidence-kind mismatch, and low sample count abstained before pooling.",
                ]
                :
                [
                    "The controlled hotspot shift was not separated from repeated A/A variability under the declared compatibility and quality gates.",
                ],
            [
                "One complete capture is the independence unit; individual samples are correlated observations and are never treated as independent trials.",
                "Sample counts and method shares describe retained observations. They do not measure elapsed CPU time, throughput, work completed, performance impact, improvement, or regression.",
                "The GO/NO-GO verdict evaluates this bounded scenario protocol, not an application or product-wide threshold.",
            ]);
    }

    public static CpuHotspotMovementFixture ReadFixture(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length is < 1 or > MaximumFixtureBytes)
        {
            throw new InvalidDataException(
                $"CPU hotspot fixture must be between 1 and {MaximumFixtureBytes} bytes.");
        }

        try
        {
            var fixture = JsonSerializer.Deserialize(
                bytes,
                CpuHotspotMovementJsonContext.Default.CpuHotspotMovementFixture)
                ?? throw new InvalidDataException($"CPU hotspot fixture '{path}' was empty.");
            ValidateFixture(fixture);
            return fixture;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"CPU hotspot fixture '{path}' is invalid: {exception.Message}",
                exception);
        }
    }

    public static void WriteFixture(string path, CpuHotspotMovementFixture fixture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(fixture);
        ValidateFixture(fixture);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            fixture,
            CpuHotspotMovementJsonContext.Default.CpuHotspotMovementFixture);
        if (bytes.Length > MaximumFixtureBytes)
        {
            throw new InvalidDataException(
                $"CPU hotspot fixture exceeds {MaximumFixtureBytes} bytes.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    public static void WriteReport(string path, CpuHotspotMovementReport report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(report);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(
            path,
            JsonSerializer.SerializeToUtf8Bytes(
                report,
                CpuHotspotMovementJsonContext.Default.CpuHotspotMovementReport));
    }

    private static CpuHotspotMovementCaseResult EvaluateCase(CpuHotspotMovementCase evidenceCase)
    {
        var pairs = PairCaptures(evidenceCase);
        var mismatches = CompatibilityMismatches(pairs);
        var limitations = new List<string>
        {
            "Capture-level sample shares use each capture's retained sample population as the denominator.",
            "Top-K membership and rank are bounded descriptive stability indicators, not CPU-time or performance-impact estimates.",
        };

        var availability = ScenarioStatisticalAvailability.Available;
        string? reason = null;
        if (mismatches.Count > 0)
        {
            availability = ScenarioStatisticalAvailability.Incompatible;
            reason = "capture_compatibility_mismatch";
            limitations.Add("Evidence was not pooled because required capture identity dimensions differed.");
        }
        else if (pairs.Length < CpuHotspotMovementBounds.MinimumCompletePairs)
        {
            availability = ScenarioStatisticalAvailability.InsufficientObservations;
            reason = "minimum_complete_capture_pairs_not_met";
        }
        else if (pairs.SelectMany(pair => new[] { pair.Baseline, pair.Candidate })
            .Any(capture => capture.Representation != ScenarioStatisticalRepresentation.Exact))
        {
            availability = ScenarioStatisticalAvailability.UnsupportedApproximation;
            reason = "approximate_capture_representation_not_admitted";
        }
        else if (pairs.SelectMany(pair => new[] { pair.Baseline, pair.Candidate })
            .Any(capture => capture.TotalSamples < CpuHotspotMovementBounds.MinimumSamplesPerCapture))
        {
            availability = ScenarioStatisticalAvailability.InsufficientObservations;
            reason = "minimum_capture_sample_count_not_met";
        }
        else if (pairs.Any(pair =>
            UnresolvedShare(pair.Baseline) > CpuHotspotMovementBounds.MaximumUnresolvedShare
            || UnresolvedShare(pair.Candidate) > CpuHotspotMovementBounds.MaximumUnresolvedShare
            || Math.Abs(UnresolvedShare(pair.Candidate) - UnresolvedShare(pair.Baseline))
                > CpuHotspotMovementBounds.MaximumPairedUnresolvedShareDelta))
        {
            availability = ScenarioStatisticalAvailability.UnsupportedApproximation;
            reason = "unresolved_symbol_share_exceeds_protocol";
            limitations.Add("Method-level movement abstained because unresolved exclusive samples could materially change attribution or rank.");
        }
        else if (pairs.Any(pair =>
            MethodShare(pair.Baseline, evidenceCase.SelectedMethodId) is null
            || MethodShare(pair.Candidate, evidenceCase.SelectedMethodId) is null))
        {
            availability = ScenarioStatisticalAvailability.InsufficientObservations;
            reason = "selected_method_not_observed_in_every_complete_pair";
        }

        var baselineShares = evidenceCase.Baseline.Captures
            .Select(capture => MethodShare(capture, evidenceCase.SelectedMethodId))
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToArray();
        var candidateShares = evidenceCase.Candidate.Captures
            .Select(capture => MethodShare(capture, evidenceCase.SelectedMethodId))
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToArray();
        var baselineUnresolved = evidenceCase.Baseline.Captures.Select(UnresolvedShare).ToArray();
        var candidateUnresolved = evidenceCase.Candidate.Captures.Select(UnresolvedShare).ToArray();
        var pairedDeltas = pairs
            .Select(pair =>
            {
                var baseline = MethodShare(pair.Baseline, evidenceCase.SelectedMethodId);
                var candidate = MethodShare(pair.Candidate, evidenceCase.SelectedMethodId);
                return baseline is null || candidate is null
                    ? (double?)null
                    : candidate.Value - baseline.Value;
            })
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToArray();
        var stability = RankStability(pairs, evidenceCase.SelectedMethodId);

        ScenarioStatisticalEvidenceResult? baselineMedian = null;
        ScenarioStatisticalEvidenceResult? candidateMedian = null;
        ScenarioStatisticalEvidenceResult? pairedDelta = null;
        if (availability == ScenarioStatisticalAvailability.Available)
        {
            baselineMedian = EvaluateMedian(evidenceCase, evidenceCase.Baseline);
            candidateMedian = EvaluateMedian(evidenceCase, evidenceCase.Candidate);
            pairedDelta = EvaluatePairedDelta(evidenceCase);
            if (baselineMedian.Availability != ScenarioStatisticalAvailability.Available
                || candidateMedian.Availability != ScenarioStatisticalAvailability.Available
                || pairedDelta.Availability != ScenarioStatisticalAvailability.Available)
            {
                availability = new[]
                {
                    baselineMedian.Availability,
                    candidateMedian.Availability,
                    pairedDelta.Availability,
                }.First(value => value != ScenarioStatisticalAvailability.Available);
                reason = "shared_statistical_harness_abstained";
            }
        }

        var movement = pairedDelta?.Delta is { Absolute: var delta }
            && Math.Abs(delta) >= CpuHotspotMovementBounds.MinimumMovementShareDelta;
        var action = availability != ScenarioStatisticalAvailability.Available
            ? CpuHotspotNextAction.Inconclusive
            : movement
                ? CpuHotspotNextAction.MovementObservedCollectDrilldown
                : CpuHotspotNextAction.NoMovementEstablished;

        return new(
            evidenceCase.Id,
            availability,
            reason,
            mismatches,
            evidenceCase.Baseline.Captures,
            evidenceCase.Candidate.Captures,
            baselineMedian,
            candidateMedian,
            pairedDelta,
            Distribution(baselineShares),
            Distribution(candidateShares),
            Distribution(baselineUnresolved),
            Distribution(candidateUnresolved),
            pairedDeltas,
            stability,
            action,
            limitations);
    }

    private static ScenarioStatisticalEvidenceResult EvaluateMedian(
        CpuHotspotMovementCase evidenceCase,
        CpuHotspotArm arm)
    {
        var attempts = arm.Captures.Select(capture => new ScenarioStatisticalAttempt(
            capture.Id,
            "attempt-1",
            ScenarioStatisticalAttemptOutcome.Passed)).ToArray();
        var observations = arm.Captures.Select(capture => new ScenarioStatisticalObservation(
            $"{capture.Id}-{evidenceCase.SelectedMethodId}",
            capture.Id,
            "attempt-1",
            "current-environment",
            capture.Id,
            null,
            capture.Id,
            MethodShare(capture, evidenceCase.SelectedMethodId)!.Value,
            null,
            capture.TotalSamples,
            0,
            capture.Representation)).ToArray();
        var request = new ScenarioStatisticalEvidenceRequest(
            ScenarioStatisticalEvidenceJson.CurrentSchemaVersion,
            Protocol(ScenarioStatisticalMethodIds.WindowMedian),
            [new ScenarioStatisticalArm(
                arm.Id,
                arm.Role,
                Identity(evidenceCase, arm.Captures),
                attempts,
                observations)],
            arm.Id);
        return ScenarioStatisticalEvidenceHarness.Evaluate(request);
    }

    private static ScenarioStatisticalEvidenceResult EvaluatePairedDelta(
        CpuHotspotMovementCase evidenceCase)
    {
        ScenarioStatisticalArm Project(CpuHotspotArm arm)
        {
            var attempts = arm.Captures.Select(capture => new ScenarioStatisticalAttempt(
                capture.Id,
                "attempt-1",
                ScenarioStatisticalAttemptOutcome.Passed)).ToArray();
            var observations = arm.Captures.Select(capture => new ScenarioStatisticalObservation(
                $"{capture.Id}-{evidenceCase.SelectedMethodId}",
                capture.Id,
                "attempt-1",
                "current-environment",
                capture.Id,
                null,
                capture.Id,
                MethodShare(capture, evidenceCase.SelectedMethodId)!.Value,
                null,
                capture.TotalSamples,
                0,
                capture.Representation,
                capture.PairKey,
                capture.PairOrder)).ToArray();
            return new(
                arm.Id,
                arm.Role,
                Identity(evidenceCase, arm.Captures),
                attempts,
                observations);
        }

        var request = new ScenarioStatisticalEvidenceRequest(
            ScenarioStatisticalEvidenceJson.CurrentSchemaVersion,
            Protocol(ScenarioStatisticalMethodIds.PairedDelta),
            [Project(evidenceCase.Baseline), Project(evidenceCase.Candidate)]);
        return ScenarioStatisticalEvidenceHarness.Evaluate(request);
    }

    private static ScenarioStatisticalProtocol Protocol(string methodId)
        => new(
            ProtocolId,
            ProtocolVersion,
            new ScenarioStatisticalMethod(methodId, 1),
            ScenarioStatisticalIndependenceUnit.Capture,
            CpuHotspotMovementBounds.MinimumCompletePairs,
            AdmitLoss: false,
            AdmitApproximation: false);

    private static ScenarioStatisticalCompatibilityIdentity Identity(
        CpuHotspotMovementCase evidenceCase,
        IReadOnlyList<CpuHotspotCaptureEvidence> captures)
    {
        var capture = captures[0];
        return new(
        [
            new("target.runtime-flavor", capture.RuntimeFlavor),
            new("target.runtime-version-policy", capture.RuntimeVersionPolicy),
            new("target.os", capture.OperatingSystem),
            new("target.architecture", capture.Architecture),
            new("target.process-run-id", "paired-capture-process-run"),
            new("backend.id", capture.Backend),
            new("backend.version", "1"),
            new("quantity.id", $"inclusive-share:{evidenceCase.SelectedMethodId}"),
            new("quantity.unit", "sample-share"),
            new("quantity.aggregation", "capture-inclusive-share"),
            new("quantity.normalization", "retained-total-samples"),
            new("quantity.filters", "BadCodeSample.CpuHotspotWorkload"),
            new("quantity.representation", "exact"),
            new("capture.duration", capture.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture)),
            new("capture.windowing", "one-complete-capture"),
            new("capture.providers", capture.EvidenceKind),
            new("capture.sampling-period", capture.SamplingPeriod),
            new("capture.retention", $"methods<={CpuHotspotMovementBounds.MaximumMethodsPerCapture};top-k={CpuHotspotMovementBounds.TopK}"),
            new("capture.enrichment", "none"),
            new("workload.scenario", capture.WorkloadScenario),
            new("workload.version", capture.WorkloadVersion),
            new("workload.parameters", $"offered-load={capture.OfferedLoad}"),
            new("workload.concurrency", capture.OfferedLoad.ToString(CultureInfo.InvariantCulture)),
            new("workload.deployment", "local-process"),
            new("workload.variant", capture.WorkloadVariant),
            new("independence.unit", "capture"),
            new("independence.nesting", "samples->capture->paired-capture"),
            new("independence.pairing-key", "pair-key"),
            new("comparison.axis", "workload.variant"),
        ]);
    }

    private static List<CpuHotspotCompatibilityMismatch> CompatibilityMismatches(
        IReadOnlyList<CapturePair> pairs)
    {
        var mismatches = new List<CpuHotspotCompatibilityMismatch>();
        foreach (var pair in pairs)
        {
            Compare("target.runtime-flavor", pair.Baseline.RuntimeFlavor, pair.Candidate.RuntimeFlavor);
            Compare("target.runtime-version-policy", pair.Baseline.RuntimeVersionPolicy, pair.Candidate.RuntimeVersionPolicy);
            Compare("target.os", pair.Baseline.OperatingSystem, pair.Candidate.OperatingSystem);
            Compare("target.architecture", pair.Baseline.Architecture, pair.Candidate.Architecture);
            Compare("target.process-run-id", pair.Baseline.ProcessRunId, pair.Candidate.ProcessRunId);
            Compare("backend.id", pair.Baseline.Backend, pair.Candidate.Backend);
            Compare("backend.evidence-kind", pair.Baseline.EvidenceKind, pair.Candidate.EvidenceKind);
            Compare(
                "capture.duration",
                pair.Baseline.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                pair.Candidate.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            Compare("capture.sampling-period", pair.Baseline.SamplingPeriod, pair.Candidate.SamplingPeriod);
            Compare("workload.scenario", pair.Baseline.WorkloadScenario, pair.Candidate.WorkloadScenario);
            Compare("workload.version", pair.Baseline.WorkloadVersion, pair.Candidate.WorkloadVersion);
            Compare(
                "workload.offered-load",
                pair.Baseline.OfferedLoad.ToString(CultureInfo.InvariantCulture),
                pair.Candidate.OfferedLoad.ToString(CultureInfo.InvariantCulture));

            void Compare(string dimension, string baseline, string candidate)
            {
                if (!string.Equals(baseline, candidate, StringComparison.Ordinal))
                {
                    mismatches.Add(new(
                        pair.PairKey,
                        dimension,
                        baseline,
                        candidate,
                        "required_identity_dimension_mismatch"));
                }
            }
        }

        return mismatches;
    }

    private static CapturePair[] PairCaptures(CpuHotspotMovementCase evidenceCase)
    {
        var baseline = evidenceCase.Baseline.Captures.ToLookup(
            capture => capture.PairKey,
            StringComparer.Ordinal);
        var candidate = evidenceCase.Candidate.Captures.ToLookup(
            capture => capture.PairKey,
            StringComparer.Ordinal);
        return baseline.Select(group => group.Key)
            .Concat(candidate.Select(group => group.Key))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(key =>
            {
                var left = baseline[key].ToArray();
                var right = candidate[key].ToArray();
                if (left.Length != 1 || right.Length != 1)
                {
                    return null;
                }
                if (left[0].PairOrder != right[0].PairOrder)
                {
                    throw new InvalidDataException(
                        $"CPU hotspot pair '{key}' does not retain one consistent pair order.");
                }
                return new CapturePair(key, left[0], right[0]);
            })
            .Where(pair => pair is not null)
            .Select(pair => pair!)
            .ToArray();
    }

    private static CpuHotspotRankStability RankStability(
        IReadOnlyList<CapturePair> pairs,
        string selectedMethodId)
    {
        var overlap = new List<double>();
        var rankDeltas = new List<double>();
        var sameRank = 0;
        foreach (var pair in pairs)
        {
            var baselineRanks = Ranks(pair.Baseline);
            var candidateRanks = Ranks(pair.Candidate);
            var baselineTop = baselineRanks.Keys.Take(CpuHotspotMovementBounds.TopK).ToHashSet(StringComparer.Ordinal);
            var candidateTop = candidateRanks.Keys.Take(CpuHotspotMovementBounds.TopK).ToHashSet(StringComparer.Ordinal);
            overlap.Add(baselineTop.Intersect(candidateTop, StringComparer.Ordinal).Count()
                / (double)CpuHotspotMovementBounds.TopK);
            if (!baselineRanks.TryGetValue(selectedMethodId, out var baselineRank)
                || !candidateRanks.TryGetValue(selectedMethodId, out var candidateRank))
            {
                continue;
            }

            var delta = Math.Abs(candidateRank - baselineRank);
            rankDeltas.Add(delta);
            if (delta == 0)
            {
                sameRank++;
            }
        }

        return new(
            pairs.Count,
            overlap.Count == 0 ? null : Median(overlap),
            rankDeltas.Count == 0 ? null : sameRank / (double)rankDeltas.Count,
            rankDeltas.Count == 0 ? null : Median(rankDeltas));
    }

    private static Dictionary<string, int> Ranks(CpuHotspotCaptureEvidence capture)
        => capture.Methods
            .OrderByDescending(method => method.InclusiveSamples)
            .ThenBy(method => method.MethodId, StringComparer.Ordinal)
            .Select((method, index) => (method.MethodId, Rank: index + 1))
            .ToDictionary(item => item.MethodId, item => item.Rank, StringComparer.Ordinal);

    private static double? MethodShare(CpuHotspotCaptureEvidence capture, string methodId)
    {
        if (capture.TotalSamples <= 0)
        {
            return null;
        }
        var method = capture.Methods.SingleOrDefault(item =>
            string.Equals(item.MethodId, methodId, StringComparison.Ordinal));
        return method is null ? null : method.InclusiveSamples / (double)capture.TotalSamples;
    }

    private static double UnresolvedShare(CpuHotspotCaptureEvidence capture)
        => capture.TotalSamples <= 0
            ? 0
            : capture.UnresolvedExclusiveSamples / (double)capture.TotalSamples;

    private static CpuHotspotCaptureDistribution Distribution(double[] values)
    {
        if (values.Length == 0)
        {
            return new(null, null, null, null, null, null);
        }

        var sorted = values.Order().ToArray();
        var first = QuantileR7(sorted, 0.25);
        var third = QuantileR7(sorted, 0.75);
        return new(
            Median(sorted),
            first,
            third,
            third - first,
            sorted[0],
            sorted[^1]);
    }

    private static double Median(IEnumerable<double> source)
    {
        var values = source.Order().ToArray();
        var midpoint = values.Length / 2;
        return values.Length % 2 == 1
            ? values[midpoint]
            : (values[midpoint - 1] + values[midpoint]) / 2;
    }

    private static double QuantileR7(double[] sortedValues, double probability)
    {
        var position = (sortedValues.Length - 1) * probability;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return sortedValues[lower];
        }
        var weight = position - lower;
        return sortedValues[lower] + (sortedValues[upper] - sortedValues[lower]) * weight;
    }

    private static void ValidateFixture(CpuHotspotMovementFixture fixture)
    {
        if (fixture.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"CPU hotspot fixture schema {fixture.SchemaVersion} is unsupported.");
        }
        if (fixture.Cases.Count is < 1 or > CpuHotspotMovementBounds.MaximumCases)
        {
            throw new InvalidDataException("CPU hotspot fixture case count exceeds its fixed bound.");
        }
        if (fixture.Cases.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count()
            != fixture.Cases.Count)
        {
            throw new InvalidDataException("CPU hotspot fixture case ids must be unique.");
        }

        foreach (var evidenceCase in fixture.Cases)
        {
            ValidateArm(evidenceCase.Baseline, ScenarioStatisticalArmRole.Baseline);
            ValidateArm(evidenceCase.Candidate, ScenarioStatisticalArmRole.Candidate);
            if (string.IsNullOrWhiteSpace(evidenceCase.SelectedMethodId))
            {
                throw new InvalidDataException(
                    $"CPU hotspot case '{evidenceCase.Id}' requires a selected method.");
            }
        }
    }

    private static void ValidateArm(CpuHotspotArm arm, ScenarioStatisticalArmRole expectedRole)
    {
        if (arm.Role != expectedRole)
        {
            throw new InvalidDataException(
                $"CPU hotspot arm '{arm.Id}' must have role '{expectedRole}'.");
        }
        if (arm.Captures.Count is < 1 or > CpuHotspotMovementBounds.MaximumCapturesPerArm)
        {
            throw new InvalidDataException(
                $"CPU hotspot arm '{arm.Id}' capture count exceeds its fixed bound.");
        }
        if (arm.Captures.Select(capture => capture.Id).Distinct(StringComparer.Ordinal).Count()
            != arm.Captures.Count)
        {
            throw new InvalidDataException(
                $"CPU hotspot arm '{arm.Id}' capture ids must be unique.");
        }

        foreach (var capture in arm.Captures)
        {
            if (capture.TotalSamples < 0
                || capture.UnresolvedExclusiveSamples < 0
                || capture.UnresolvedExclusiveSamples > capture.TotalSamples)
            {
                throw new InvalidDataException(
                    $"CPU hotspot capture '{capture.Id}' has invalid sample counts.");
            }
            if (capture.Methods.Count is < 1 or > CpuHotspotMovementBounds.MaximumMethodsPerCapture
                || capture.Methods.Select(method => method.MethodId)
                    .Distinct(StringComparer.Ordinal).Count() != capture.Methods.Count
                || capture.Methods.Any(method => method.InclusiveSamples < 0)
                || capture.Methods.Sum(method => method.InclusiveSamples)
                    > capture.TotalSamples)
            {
                throw new InvalidDataException(
                    $"CPU hotspot capture '{capture.Id}' violates the bounded method inventory.");
            }
            if (capture.DurationSeconds <= 0 || capture.OfferedLoad <= 0)
            {
                throw new InvalidDataException(
                    $"CPU hotspot capture '{capture.Id}' has invalid protocol metadata.");
            }
        }
    }

    private sealed record CapturePair(
        string PairKey,
        CpuHotspotCaptureEvidence Baseline,
        CpuHotspotCaptureEvidence Candidate);
}
