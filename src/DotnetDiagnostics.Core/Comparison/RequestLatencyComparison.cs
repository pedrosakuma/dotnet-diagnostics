using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DotnetDiagnostics.Core.Comparison;

public static class RequestLatencyComparison
{
    public const int MaximumWindowsPerSide = 32;
    public const int MaximumRetainedSamplesPerWindow = 256;
    public const int MaximumSerializedWindowSetBytes = 256 * 1024;
    public const int MinimumCompletedRequestsPerWindow = 8;
    public const int MinimumCompleteWindowPairs = 8;
    public const int BootstrapResamples = 2_048;
    public const double ConfidenceLevel = 0.95;

    private const string SeedDerivation =
        "sha256(request-latency-comparison-v1|compatibility-identity|ordered-pair-deltas)";
    private const string PrngAlgorithm = "xoshiro256-star-star-v1";
    private const string QuantileRule = "linear-r7-v1";
    private static readonly (string Name, double Probability)[] Quantiles =
    [
        ("p50", 0.50),
        ("p95", 0.95),
        ("p99", 0.99),
    ];

    public static RequestLatencyComparisonResult Compare(
        RequestLatencyWindowSet baseline,
        RequestLatencyWindowSet candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        Validate(baseline, nameof(baseline));
        Validate(candidate, nameof(candidate));

        var mismatches = CompareIdentity(baseline.Identity, candidate.Identity);
        var pairs = PairWindows(baseline.Windows, candidate.Windows);
        var baselineCounts = Counts(baseline.Windows);
        var candidateCounts = Counts(candidate.Windows);
        var pairCounts = CountPairs(pairs);
        var availability = DetermineAvailability(
            mismatches,
            baselineCounts,
            candidateCounts,
            pairCounts,
            baseline.Windows,
            candidate.Windows);
        var reason = UnavailableReason(availability, mismatches, baselineCounts, candidateCounts, pairCounts);
        var canonical = Canonicalize(baseline, candidate, pairs);
        var quantiles = Quantiles.Select(quantile => CompareQuantile(
            quantile.Name,
            quantile.Probability,
            availability,
            reason,
            mismatches.Count == 0 && pairs.PairOrderMismatches == 0,
            baseline.Windows,
            candidate.Windows,
            pairs,
            canonical.Seed)).ToArray();

        var limitations = new List<string>
        {
            "Quantiles describe completed retained requests; request events are not treated as independent inferential units.",
            "The bootstrap resamples valid complete non-overlapping window pairs, not correlated request events.",
            "The interval describes deterministic resampling variability under the declared protocol, not systematic bias.",
            "No regression, improvement, significance, causation, equivalent-load, or operational-importance verdict is emitted.",
            "Matched-pairs rank-biserial effect is omitted: it is not registered by the v1 evidence foundation and the scenario A/A evidence produced a large effect for a non-actionable shift.",
        };
        if (mismatches.Count > 0)
        {
            limitations.Add("Compatibility identity differs; comparison deltas and paired calculations are unavailable.");
        }
        if (pairCounts.PairOrderMismatches > 0)
        {
            limitations.Add("Pair order differs within one or more matched pairs; comparison deltas and paired calculations are unavailable.");
        }
        if (HasIncompleteRequests(baselineCounts, candidateCounts))
        {
            limitations.Add("Errors, censored requests, and missing completions remain separate from completed-request quantiles.");
        }
        if (baselineCounts.FailedWindows + baselineCounts.AbortedWindows
            + candidateCounts.FailedWindows + candidateCounts.AbortedWindows > 0)
        {
            limitations.Add("Failed and aborted windows remain in denominators and are never replaced by retries.");
        }
        if (baselineCounts.LostOrDropped > 0 || candidateCounts.LostOrDropped > 0)
        {
            limitations.Add("Known loss invalidates paired-window inference under this contract.");
        }
        if (baseline.Windows.Concat(candidate.Windows).Any(window =>
                window.Representation != RequestLatencyRepresentation.Exact))
        {
            limitations.Add("Approximate, projected, or truncated request samples remain descriptive only.");
        }

        return new(
            RequestLatencyComparisonResult.SchemaV1,
            availability,
            reason,
            baseline.Identity,
            candidate.Identity,
            mismatches,
            baselineCounts,
            candidateCounts,
            pairCounts,
            new(
                Quantile: QuantileRule,
                PairedEstimate: "median-of-candidate-minus-baseline-window-quantile-deltas-v1",
                Interval: "deterministic-percentile-bootstrap-v1",
                IndependenceUnit: "bounded_time_window_pair",
                MinimumCompletedRequestsPerWindow,
                MinimumCompleteWindowPairs,
                BootstrapResamples,
                ConfidenceLevel),
            quantiles,
            new(
                canonical.OrderedInputSha256,
                canonical.IdentitySha256,
                SeedDerivation,
                Convert.ToHexString(SHA256.HashData(canonical.Seed)).ToLowerInvariant(),
                PrngAlgorithm,
                QuantileRule),
            limitations);
    }

    private static RequestLatencyQuantileComparison CompareQuantile(
        string name,
        double probability,
        RequestLatencyEvidenceAvailability availability,
        string? unavailableReason,
        bool compatible,
        IReadOnlyList<RequestLatencyWindow> baselineWindows,
        IReadOnlyList<RequestLatencyWindow> candidateWindows,
        PairingResult pairs,
        byte[] seed)
    {
        var baseline = Estimate(baselineWindows, probability);
        var candidate = Estimate(candidateWindows, probability);
        double? absolute = null;
        double? relative = null;
        var unavailable = new List<RequestLatencyUnavailableField>();

        if (compatible
            && baseline.ValueMilliseconds is double baselineValue
            && candidate.ValueMilliseconds is double candidateValue)
        {
            var absoluteValue = candidateValue - baselineValue;
            absolute = absoluteValue;
            if (baselineValue != 0)
            {
                var relativeValue = absoluteValue / Math.Abs(baselineValue);
                if (double.IsFinite(relativeValue))
                {
                    relative = relativeValue;
                }
                else
                {
                    unavailable.Add(new("relativeDelta", "relative_delta_nonfinite"));
                }
            }
            else
            {
                unavailable.Add(new("relativeDelta", "baseline_estimate_is_zero"));
            }
        }
        else
        {
            var deltaReason = compatible
                ? "baseline_or_candidate_quantile_unavailable"
                : "compatibility_identity_mismatch";
            unavailable.Add(new("absoluteDeltaMilliseconds", deltaReason));
            unavailable.Add(new("relativeDelta", deltaReason));
        }

        double? pairedMedian = null;
        RequestLatencyInterval? interval = null;
        if (availability == RequestLatencyEvidenceAvailability.Available)
        {
            var deltas = pairs.Pairs
                .Where(IsValidIndependentCompletePair)
                .OrderBy(pair => pair.Baseline.PairKey, StringComparer.Ordinal)
                .Select(pair =>
                    QuantileR7(Sorted(pair.Candidate.RetainedLatenciesMilliseconds), probability)
                    - QuantileR7(Sorted(pair.Baseline.RetainedLatenciesMilliseconds), probability))
                .ToArray();
            pairedMedian = Median(deltas);
            interval = BootstrapMedianInterval(deltas, DeriveQuantileSeed(seed, name));
        }
        else
        {
            var reason = unavailableReason ?? "paired_window_evidence_unavailable";
            unavailable.Add(new("pairedWindowMedianDeltaMilliseconds", reason));
            unavailable.Add(new("pairedWindowBootstrapInterval", reason));
        }

        return new(
            name,
            probability,
            availability,
            baseline,
            candidate,
            absolute,
            relative,
            pairedMedian,
            interval,
            unavailable);
    }

    private static RequestLatencyQuantileEstimate Estimate(
        IReadOnlyList<RequestLatencyWindow> windows,
        double probability)
    {
        var complete = windows
            .Where(window => window.Outcome == RequestLatencyWindowOutcome.Complete)
            .ToArray();
        var values = complete
            .SelectMany(window => window.RetainedLatenciesMilliseconds)
            .Order()
            .ToArray();
        var sourceCount = complete.Sum(window => window.SourceCompletedCount);
        if (values.Length == 0)
        {
            return new(
                RequestLatencyEvidenceAvailability.InsufficientObservations,
                null,
                sourceCount,
                0,
                "no_retained_completed_requests");
        }

        return new(
            RequestLatencyEvidenceAvailability.Available,
            QuantileR7(values, probability),
            sourceCount,
            values.Length,
            null);
    }

    private static RequestLatencyInterval BootstrapMedianInterval(double[] values, byte[] seed)
    {
        var random = Xoshiro256StarStar.Create(seed);
        var estimates = new double[BootstrapResamples];
        var sample = new double[values.Length];
        for (var resample = 0; resample < estimates.Length; resample++)
        {
            for (var index = 0; index < sample.Length; index++)
            {
                sample[index] = values[random.NextInt32(values.Length)];
            }

            Array.Sort(sample);
            estimates[resample] = MedianSorted(sample);
        }

        Array.Sort(estimates);
        var alpha = (1 - ConfidenceLevel) / 2;
        return new(
            QuantileR7(estimates, alpha),
            QuantileR7(estimates, 1 - alpha),
            ConfidenceLevel);
    }

    private static RequestLatencyEvidenceAvailability DetermineAvailability(
        List<RequestLatencyIdentityMismatch> mismatches,
        RequestLatencySideCounts baseline,
        RequestLatencySideCounts candidate,
        RequestLatencyPairCounts pairs,
        IReadOnlyList<RequestLatencyWindow> baselineWindows,
        IReadOnlyList<RequestLatencyWindow> candidateWindows)
    {
        if (mismatches.Count > 0 || pairs.PairOrderMismatches > 0)
        {
            return RequestLatencyEvidenceAvailability.Incompatible;
        }
        if (baseline.LostOrDropped > 0 || candidate.LostOrDropped > 0)
        {
            return RequestLatencyEvidenceAvailability.LossDetected;
        }
        if (baselineWindows.Concat(candidateWindows).Any(window =>
                window.Representation != RequestLatencyRepresentation.Exact))
        {
            return RequestLatencyEvidenceAvailability.UnsupportedApproximation;
        }
        if (pairs.ValidIndependentComplete < MinimumCompleteWindowPairs
            || pairs.Unpaired > 0
            || pairs.Incomplete > 0
            || pairs.Failed > 0
            || pairs.Aborted > 0
            || pairs.BelowMinimumCompletedRequests > 0)
        {
            return RequestLatencyEvidenceAvailability.InsufficientObservations;
        }

        return RequestLatencyEvidenceAvailability.Available;
    }

    private static string? UnavailableReason(
        RequestLatencyEvidenceAvailability availability,
        List<RequestLatencyIdentityMismatch> mismatches,
        RequestLatencySideCounts baseline,
        RequestLatencySideCounts candidate,
        RequestLatencyPairCounts pairs)
        => availability switch
        {
            RequestLatencyEvidenceAvailability.Available => null,
            RequestLatencyEvidenceAvailability.Incompatible when mismatches.Count > 0 =>
                "compatibility_identity_mismatch",
            RequestLatencyEvidenceAvailability.Incompatible => "pair_order_mismatch",
            RequestLatencyEvidenceAvailability.LossDetected => "source_loss_detected",
            RequestLatencyEvidenceAvailability.UnsupportedApproximation =>
                "approximate_representation_not_admitted",
            RequestLatencyEvidenceAvailability.InsufficientObservations
                when baseline.FailedWindows + candidate.FailedWindows > 0 => "failed_windows_present",
            RequestLatencyEvidenceAvailability.InsufficientObservations
                when baseline.AbortedWindows + candidate.AbortedWindows > 0 => "aborted_windows_present",
            RequestLatencyEvidenceAvailability.InsufficientObservations
                when HasIncompleteRequests(baseline, candidate) =>
                "incomplete_request_population",
            RequestLatencyEvidenceAvailability.InsufficientObservations when pairs.Unpaired > 0 =>
                "incomplete_window_pairs",
            RequestLatencyEvidenceAvailability.InsufficientObservations
                when pairs.BelowMinimumCompletedRequests > 0 =>
                "minimum_completed_requests_per_window_not_met",
            RequestLatencyEvidenceAvailability.InsufficientObservations =>
                "minimum_complete_window_pairs_not_met",
            _ => "request_latency_evidence_unavailable",
        };

    private static RequestLatencySideCounts Counts(IReadOnlyList<RequestLatencyWindow> windows)
        => new(
            windows.Count,
            windows.Count(window => window.Outcome == RequestLatencyWindowOutcome.Complete),
            windows.Count(window => window.Outcome == RequestLatencyWindowOutcome.Failed),
            windows.Count(window => window.Outcome == RequestLatencyWindowOutcome.Aborted),
            windows.Sum(window => window.SourceCompletedCount),
            windows.Sum(window => window.RetainedLatenciesMilliseconds.Count),
            windows.Sum(window => window.ErrorCount),
            windows.Sum(window => window.CensoredCount),
            windows.Sum(window => window.MissingCompletionCount),
            windows.Sum(window => window.LostOrDropped));

    private static RequestLatencyPairCounts CountPairs(PairingResult result)
    {
        var valid = result.Pairs.Count(IsValidIndependentCompletePair);
        var failed = result.Pairs.Count(pair =>
            pair.Baseline.Outcome == RequestLatencyWindowOutcome.Failed
            || pair.Candidate.Outcome == RequestLatencyWindowOutcome.Failed);
        var aborted = result.Pairs.Count(pair =>
            pair.Baseline.Outcome == RequestLatencyWindowOutcome.Aborted
            || pair.Candidate.Outcome == RequestLatencyWindowOutcome.Aborted);
        var incomplete = result.Pairs.Count(pair =>
            (long)pair.Baseline.ErrorCount + pair.Baseline.CensoredCount
                + pair.Baseline.MissingCompletionCount
                + pair.Candidate.ErrorCount + pair.Candidate.CensoredCount
                + pair.Candidate.MissingCompletionCount > 0);
        var belowMinimum = result.Pairs.Count(pair =>
            pair.Baseline.Outcome == RequestLatencyWindowOutcome.Complete
            && pair.Candidate.Outcome == RequestLatencyWindowOutcome.Complete
            && (pair.Baseline.SourceCompletedCount < MinimumCompletedRequestsPerWindow
                || pair.Candidate.SourceCompletedCount < MinimumCompletedRequestsPerWindow));
        return new(
            result.Pairs.Count,
            valid,
            result.Unpaired,
            result.PairOrderMismatches,
            incomplete,
            failed,
            aborted,
            belowMinimum);
    }

    private static bool IsValidIndependentCompletePair(WindowPair pair)
        => pair.Baseline.PairOrder == pair.Candidate.PairOrder
            && IsValidIndependentCompleteWindow(pair.Baseline)
            && IsValidIndependentCompleteWindow(pair.Candidate);

    private static bool IsValidIndependentCompleteWindow(RequestLatencyWindow window)
        => window.Outcome == RequestLatencyWindowOutcome.Complete
            && window.Representation == RequestLatencyRepresentation.Exact
            && window.LostOrDropped == 0
            && window.ErrorCount == 0
            && window.CensoredCount == 0
            && window.MissingCompletionCount == 0
            && window.SourceCompletedCount >= MinimumCompletedRequestsPerWindow
            && window.SourceCompletedCount == window.RetainedLatenciesMilliseconds.Count;

    private static bool HasIncompleteRequests(
        RequestLatencySideCounts baseline,
        RequestLatencySideCounts candidate)
        => (long)baseline.Errors + baseline.Censored + baseline.MissingCompletions
            + candidate.Errors + candidate.Censored + candidate.MissingCompletions > 0;

    private static PairingResult PairWindows(
        IReadOnlyList<RequestLatencyWindow> baseline,
        IReadOnlyList<RequestLatencyWindow> candidate)
    {
        var baselineByKey = baseline.ToDictionary(window => window.PairKey, StringComparer.Ordinal);
        var candidateByKey = candidate.ToDictionary(window => window.PairKey, StringComparer.Ordinal);
        var keys = baselineByKey.Keys
            .Intersect(candidateByKey.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var pairs = keys
            .Select(key => new WindowPair(baselineByKey[key], candidateByKey[key]))
            .ToArray();
        return new(
            pairs,
            baseline.Count + candidate.Count - pairs.Length * 2,
            pairs.Count(pair => pair.Baseline.PairOrder != pair.Candidate.PairOrder));
    }

    private static List<RequestLatencyIdentityMismatch> CompareIdentity(
        RequestLatencyCompatibilityIdentity baseline,
        RequestLatencyCompatibilityIdentity candidate)
    {
        var mismatches = new List<RequestLatencyIdentityMismatch>();
        Compare(mismatches, "target.runtimeFlavor", baseline.Target.RuntimeFlavor, candidate.Target.RuntimeFlavor);
        Compare(mismatches, "target.runtimeVersionPolicy", baseline.Target.RuntimeVersionPolicy, candidate.Target.RuntimeVersionPolicy);
        Compare(mismatches, "target.operatingSystem", baseline.Target.OperatingSystem, candidate.Target.OperatingSystem);
        Compare(mismatches, "target.architecture", baseline.Target.Architecture, candidate.Target.Architecture);
        Compare(mismatches, "backend.id", baseline.Backend.Id, candidate.Backend.Id);
        Compare(mismatches, "backend.version", baseline.Backend.Version, candidate.Backend.Version);
        Compare(mismatches, "quantity.id", baseline.Quantity.Id, candidate.Quantity.Id);
        Compare(mismatches, "quantity.unit", baseline.Quantity.Unit, candidate.Quantity.Unit);
        Compare(mismatches, "quantity.aggregation", baseline.Quantity.Aggregation, candidate.Quantity.Aggregation);
        Compare(mismatches, "quantity.normalization", baseline.Quantity.Normalization, candidate.Quantity.Normalization);
        Compare(mismatches, "quantity.filters", baseline.Quantity.Filters, candidate.Quantity.Filters);
        Compare(mismatches, "quantity.representation", baseline.Quantity.Representation.ToString(), candidate.Quantity.Representation.ToString());
        Compare(mismatches, "capture.duration", baseline.Capture.Duration, candidate.Capture.Duration);
        Compare(mismatches, "capture.windowing", baseline.Capture.Windowing, candidate.Capture.Windowing);
        Compare(mismatches, "capture.providers", baseline.Capture.Providers, candidate.Capture.Providers);
        Compare(mismatches, "capture.samplingPeriod", baseline.Capture.SamplingPeriod, candidate.Capture.SamplingPeriod);
        Compare(mismatches, "capture.retention", baseline.Capture.Retention, candidate.Capture.Retention);
        Compare(mismatches, "capture.enrichment", baseline.Capture.Enrichment, candidate.Capture.Enrichment);
        Compare(mismatches, "workload.scenario", baseline.Workload.Scenario, candidate.Workload.Scenario);
        Compare(mismatches, "workload.version", baseline.Workload.Version, candidate.Workload.Version);
        Compare(mismatches, "workload.parameters", baseline.Workload.Parameters, candidate.Workload.Parameters);
        Compare(mismatches, "workload.concurrency", baseline.Workload.Concurrency, candidate.Workload.Concurrency);
        Compare(mismatches, "workload.deployment", baseline.Workload.Deployment, candidate.Workload.Deployment);
        Compare(mismatches, "window.independenceUnit", baseline.Window.IndependenceUnit, candidate.Window.IndependenceUnit);
        Compare(mismatches, "window.nesting", baseline.Window.Nesting, candidate.Window.Nesting);
        Compare(mismatches, "window.pairingKey", baseline.Window.PairingKey, candidate.Window.PairingKey);
        Compare(mismatches, "comparisonAxis.name", baseline.ComparisonAxis.Name, candidate.ComparisonAxis.Name);
        return mismatches;
    }

    private static void Compare(
        List<RequestLatencyIdentityMismatch> mismatches,
        string field,
        string baseline,
        string candidate)
    {
        if (!string.Equals(baseline, candidate, StringComparison.Ordinal))
        {
            mismatches.Add(new(field, baseline, candidate, "value_mismatch"));
        }
    }

    private static void Validate(RequestLatencyWindowSet set, string parameterName)
    {
        if (!string.Equals(set.Schema, RequestLatencyWindowSet.SchemaV1, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Expected schema '{RequestLatencyWindowSet.SchemaV1}'.",
                parameterName);
        }
        if (string.IsNullOrWhiteSpace(set.Id))
        {
            throw new ArgumentException("Window-set identity is required.", parameterName);
        }
        if (set.Identity is null)
        {
            throw new ArgumentException("Window-set compatibility identity is required.", parameterName);
        }
        ValidateIdentity(set.Identity, parameterName);
        if (set.Windows is null)
        {
            throw new ArgumentException("Window-set windows are required.", parameterName);
        }
        if (set.Windows.Count is < 1 or > MaximumWindowsPerSide)
        {
            throw new ArgumentException(
                $"Each side must contain 1..{MaximumWindowsPerSide} windows.",
                parameterName);
        }
        if (set.Windows.Any(window => window is null))
        {
            throw new ArgumentException("Window entries cannot be null.", parameterName);
        }
        if (set.Windows.Select(window => window.Id).Distinct(StringComparer.Ordinal).Count()
                != set.Windows.Count
            || set.Windows.Select(window => window.PairKey).Distinct(StringComparer.Ordinal).Count()
                != set.Windows.Count)
        {
            throw new ArgumentException(
                "Window ids and pair keys must be unique within each side.",
                parameterName);
        }

        foreach (var window in set.Windows)
        {
            if (string.IsNullOrWhiteSpace(window.Id) || string.IsNullOrWhiteSpace(window.PairKey))
            {
                throw new ArgumentException("Window id and pair key are required.", parameterName);
            }
            if (window.RetainedLatenciesMilliseconds is null)
            {
                throw new ArgumentException(
                    "Retained completed-request latency values are required.",
                    parameterName);
            }
            if (!Enum.IsDefined(window.PairOrder)
                || !Enum.IsDefined(window.Outcome)
                || !Enum.IsDefined(window.Representation))
            {
                throw new ArgumentException(
                    "Window pair order, outcome, and representation must be recognized v1 values.",
                    parameterName);
            }
            if (window.SourceCompletedCount < 0
                || window.ErrorCount < 0
                || window.CensoredCount < 0
                || window.MissingCompletionCount < 0
                || window.LostOrDropped < 0)
            {
                throw new ArgumentException("Window counts must be non-negative.", parameterName);
            }
            if (window.RetainedLatenciesMilliseconds.Count > MaximumRetainedSamplesPerWindow
                || window.RetainedLatenciesMilliseconds.Any(value =>
                    !double.IsFinite(value) || value < 0))
            {
                throw new ArgumentException(
                    $"Each window may retain at most {MaximumRetainedSamplesPerWindow} finite non-negative latency values.",
                    parameterName);
            }
            if (window.SourceCompletedCount < window.RetainedLatenciesMilliseconds.Count)
            {
                throw new ArgumentException(
                    "Source completed count cannot be lower than retained completed count.",
                    parameterName);
            }
            if (window.Outcome != RequestLatencyWindowOutcome.Complete
                && (window.SourceCompletedCount != 0
                    || window.RetainedLatenciesMilliseconds.Count != 0))
            {
                throw new ArgumentException(
                    "Failed or aborted windows cannot contribute completed-request latency values.",
                    parameterName);
            }
            if (window.Representation == RequestLatencyRepresentation.Exact
                && window.Outcome == RequestLatencyWindowOutcome.Complete
                && window.SourceCompletedCount != window.RetainedLatenciesMilliseconds.Count)
            {
                throw new ArgumentException(
                    "Exact complete windows must retain every completed-request latency value.",
                    parameterName);
            }
            if (window.Representation != set.Identity.Quantity.Representation)
            {
                throw new ArgumentException(
                    "Every window representation must match quantity.representation.",
                    parameterName);
            }
        }

        ValidateAggregateCounts(set.Windows, parameterName);
    }

    private static void ValidateIdentity(
        RequestLatencyCompatibilityIdentity identity,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.Target is null
            || identity.Backend is null
            || identity.Quantity is null
            || identity.Capture is null
            || identity.Workload is null
            || identity.Window is null
            || identity.ComparisonAxis is null)
        {
            throw new ArgumentException(
                "Every request-latency compatibility identity section is required.",
                parameterName);
        }
        var values = new[]
        {
            identity.Target.RuntimeFlavor,
            identity.Target.RuntimeVersionPolicy,
            identity.Target.OperatingSystem,
            identity.Target.Architecture,
            identity.Target.ProcessRunId,
            identity.Backend.Id,
            identity.Backend.Version,
            identity.Quantity.Id,
            identity.Quantity.Unit,
            identity.Quantity.Aggregation,
            identity.Quantity.Normalization,
            identity.Quantity.Filters,
            identity.Capture.Duration,
            identity.Capture.Windowing,
            identity.Capture.Providers,
            identity.Capture.SamplingPeriod,
            identity.Capture.Retention,
            identity.Capture.Enrichment,
            identity.Workload.Scenario,
            identity.Workload.Version,
            identity.Workload.Parameters,
            identity.Workload.Concurrency,
            identity.Workload.Deployment,
            identity.Window.IndependenceUnit,
            identity.Window.Nesting,
            identity.Window.PairingKey,
            identity.ComparisonAxis.Name,
            identity.ComparisonAxis.Value,
        };
        if (values.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Every request-latency compatibility identity field is required.",
                parameterName);
        }
        if (!Enum.IsDefined(identity.Quantity.Representation))
        {
            throw new ArgumentException(
                "Quantity representation must be a recognized v1 value.",
                parameterName);
        }
        if (!string.Equals(identity.Quantity.Id, "request-latency", StringComparison.Ordinal)
            || !string.Equals(identity.Quantity.Unit, "ms", StringComparison.Ordinal)
            || !string.Equals(identity.Window.IndependenceUnit, "bounded_time_window", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The v1 contract requires quantity.id=request-latency, quantity.unit=ms, and window.independenceUnit=bounded_time_window.",
                parameterName);
        }
    }

    private static void ValidateAggregateCounts(
        IReadOnlyList<RequestLatencyWindow> windows,
        string parameterName)
    {
        var sourceCompleted = windows.Sum(window => (long)window.SourceCompletedCount);
        var errors = windows.Sum(window => (long)window.ErrorCount);
        var censored = windows.Sum(window => (long)window.CensoredCount);
        var missing = windows.Sum(window => (long)window.MissingCompletionCount);
        if (sourceCompleted > int.MaxValue
            || errors > int.MaxValue
            || censored > int.MaxValue
            || missing > int.MaxValue)
        {
            throw new ArgumentException(
                "Aggregate request counts must fit a signed 32-bit integer.",
                parameterName);
        }

        try
        {
            _ = windows.Aggregate(
                0L,
                static (total, window) => checked(total + window.LostOrDropped));
        }
        catch (OverflowException exception)
        {
            throw new ArgumentException(
                "Aggregate lost-or-dropped count must fit a signed 64-bit integer.",
                parameterName,
                exception);
        }
    }

    private static CanonicalInput Canonicalize(
        RequestLatencyWindowSet baseline,
        RequestLatencyWindowSet candidate,
        PairingResult pairs)
    {
        var identity = CanonicalIdentity(baseline.Identity, includeAxisValue: false)
            + "\n"
            + CanonicalIdentity(candidate.Identity, includeAxisValue: false);
        var input = new StringBuilder();
        foreach (var pair in pairs.Pairs.OrderBy(pair => pair.Baseline.PairKey, StringComparer.Ordinal))
        {
            input.Append(pair.Baseline.PairKey).Append('|')
                .Append(pair.Baseline.PairOrder).Append('|')
                .Append(pair.Baseline.Outcome).Append('|')
                .Append(pair.Candidate.Outcome).Append('|');
            AppendValues(input, pair.Baseline.RetainedLatenciesMilliseconds);
            input.Append('|');
            AppendValues(input, pair.Candidate.RetainedLatenciesMilliseconds);
            input.AppendLine();
        }

        var identityBytes = Encoding.UTF8.GetBytes(identity);
        var inputBytes = Encoding.UTF8.GetBytes(input.ToString());
        var identityHash = SHA256.HashData(identityBytes);
        var inputHash = SHA256.HashData(inputBytes);
        var seedMaterial = Encoding.UTF8.GetBytes(
            $"{SeedDerivation}\n{Convert.ToHexString(identityHash)}\n{Convert.ToHexString(inputHash)}");
        return new(
            Convert.ToHexString(inputHash).ToLowerInvariant(),
            Convert.ToHexString(identityHash).ToLowerInvariant(),
            SHA256.HashData(seedMaterial));
    }

    private static string CanonicalIdentity(
        RequestLatencyCompatibilityIdentity identity,
        bool includeAxisValue)
    {
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["backend.id"] = identity.Backend.Id,
            ["backend.version"] = identity.Backend.Version,
            ["capture.duration"] = identity.Capture.Duration,
            ["capture.enrichment"] = identity.Capture.Enrichment,
            ["capture.providers"] = identity.Capture.Providers,
            ["capture.retention"] = identity.Capture.Retention,
            ["capture.samplingPeriod"] = identity.Capture.SamplingPeriod,
            ["capture.windowing"] = identity.Capture.Windowing,
            ["comparisonAxis.name"] = identity.ComparisonAxis.Name,
            ["quantity.aggregation"] = identity.Quantity.Aggregation,
            ["quantity.filters"] = identity.Quantity.Filters,
            ["quantity.id"] = identity.Quantity.Id,
            ["quantity.normalization"] = identity.Quantity.Normalization,
            ["quantity.representation"] = identity.Quantity.Representation.ToString(),
            ["quantity.unit"] = identity.Quantity.Unit,
            ["target.architecture"] = identity.Target.Architecture,
            ["target.operatingSystem"] = identity.Target.OperatingSystem,
            ["target.runtimeFlavor"] = identity.Target.RuntimeFlavor,
            ["target.runtimeVersionPolicy"] = identity.Target.RuntimeVersionPolicy,
            ["window.independenceUnit"] = identity.Window.IndependenceUnit,
            ["window.nesting"] = identity.Window.Nesting,
            ["window.pairingKey"] = identity.Window.PairingKey,
            ["workload.concurrency"] = identity.Workload.Concurrency,
            ["workload.deployment"] = identity.Workload.Deployment,
            ["workload.parameters"] = identity.Workload.Parameters,
            ["workload.scenario"] = identity.Workload.Scenario,
            ["workload.version"] = identity.Workload.Version,
        };
        if (includeAxisValue)
        {
            fields["comparisonAxis.value"] = identity.ComparisonAxis.Value;
        }

        return string.Join(
            "\n",
            fields.Select(field => $"{field.Key}={field.Value}"));
    }

    private static void AppendValues(StringBuilder builder, IReadOnlyList<double> values)
    {
        foreach (var value in values.Order())
        {
            builder.Append(value.ToString("R", CultureInfo.InvariantCulture)).Append(',');
        }
    }

    private static byte[] DeriveQuantileSeed(byte[] seed, string quantile)
        => SHA256.HashData(seed.Concat(Encoding.UTF8.GetBytes(quantile)).ToArray());

    private static double[] Sorted(IReadOnlyList<double> values)
        => values.Order().ToArray();

    private static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.Order().ToArray();
        return MedianSorted(sorted);
    }

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
        if (lower == upper)
        {
            return sorted[lower];
        }
        if (sorted[lower] == sorted[upper])
        {
            return sorted[lower];
        }

        var fraction = position - lower;
        return (1 - fraction) * sorted[lower] + fraction * sorted[upper];
    }

    private sealed record WindowPair(
        RequestLatencyWindow Baseline,
        RequestLatencyWindow Candidate);

    private sealed record PairingResult(
        IReadOnlyList<WindowPair> Pairs,
        int Unpaired,
        int PairOrderMismatches);

    private sealed record CanonicalInput(
        string OrderedInputSha256,
        string IdentitySha256,
        byte[] Seed);

    private struct Xoshiro256StarStar
    {
        private ulong _s0;
        private ulong _s1;
        private ulong _s2;
        private ulong _s3;

        private Xoshiro256StarStar(ulong s0, ulong s1, ulong s2, ulong s3)
        {
            _s0 = s0;
            _s1 = s1;
            _s2 = s2;
            _s3 = s3;
        }

        public static Xoshiro256StarStar Create(ReadOnlySpan<byte> seed)
        {
            if (seed.Length < 32)
            {
                throw new ArgumentException("A 32-byte seed is required.", nameof(seed));
            }

            return new(
                BinaryPrimitives.ReadUInt64LittleEndian(seed),
                BinaryPrimitives.ReadUInt64LittleEndian(seed[8..]),
                BinaryPrimitives.ReadUInt64LittleEndian(seed[16..]),
                BinaryPrimitives.ReadUInt64LittleEndian(seed[24..]));
        }

        public int NextInt32(int exclusiveMaximum)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exclusiveMaximum);
            return (int)(NextUInt64() % (uint)exclusiveMaximum);
        }

        private ulong NextUInt64()
        {
            var result = RotateLeft(_s1 * 5, 7) * 9;
            var temporary = _s1 << 17;

            _s2 ^= _s0;
            _s3 ^= _s1;
            _s1 ^= _s2;
            _s0 ^= _s3;
            _s2 ^= temporary;
            _s3 = RotateLeft(_s3, 45);
            return result;
        }

        private static ulong RotateLeft(ulong value, int offset)
            => (value << offset) | (value >> (64 - offset));
    }
}
