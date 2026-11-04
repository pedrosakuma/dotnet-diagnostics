using System.Text.Json;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal static class AllocationThroughputNormalizationV2ProtocolDocument
{
    public static JsonElement Build(
        string status,
        string? frozenAtUtc,
        bool includeFrozenHashes)
    {
        AllocationThroughputNormalizationV2CapturePlan.ValidateConstruction();
        var sourceHashes = includeFrozenHashes
            ? Hashes(
                AllocationThroughputNormalizationV2Protocol.FrozenSourcePaths,
                canonicalText: true)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var runtimeHashes = includeFrozenHashes
            ? Hashes(
                AllocationThroughputNormalizationV2Protocol
                    .FrozenRuntimeArtifactPaths,
                canonicalText: false)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var preservedHashes = Hashes(
            AllocationThroughputNormalizationV2Protocol.PreservedArtifactPaths,
            canonicalText: true);
        var document = new
        {
            schemaVersion = 1,
            protocolId = AllocationThroughputNormalizationV2Protocol.ProtocolId,
            protocolVersion =
                AllocationThroughputNormalizationV2Protocol.ProtocolVersion,
            status,
            frozenAtUtc,
            sourceBaseCommit =
                AllocationThroughputNormalizationV2Protocol.SourceBaseCommit,
            prerequisites = new
            {
                poweredFalsifierIssue = 1393,
                preservedFocusedFalsifierIssue = 1390,
                preservedFocusedFalsifierMerge =
                    AllocationThroughputNormalizationV2Protocol
                        .PreservedV1Merge,
                preservedV3Merge =
                    AllocationThroughputNormalizationV2Protocol
                        .PrerequisiteV3Merge,
                ledgerSchemaVersion =
                    AllocationDirectionalLedger.CurrentSchemaVersion,
                projectorSchema =
                    AllocationDirectionalProjection.SchemaIdentity,
                boundedCalibrationVersion =
                    AllocationDirectionalBoundedCalibrationPlan
                        .CalibrationVersion,
            },
            question = "When allocation bytes, operation code path, payload, worker count, capture geometry, and logical-operation counting remain equal within each pair, does changing only pacing move estimated sampled bytes per logical operation beyond max(workload-specific robust A/A, 10%)?",
            primaryQuantity =
                "estimated sampled allocation bytes divided by completed logical operations",
            primaryUnit = "estimated sampled bytes per logical operation",
            descriptiveOnly = new[]
            {
                "estimated sampled bytes per second",
                "allocation-event count",
            },
            requiredIndependentPairs =
                AllocationThroughputNormalizationV2CapturePlan.RequiredPairs,
            inventory = new
            {
                cohortCount =
                    AllocationThroughputNormalizationV2CapturePlan.Cohorts.Count,
                aaPairsPerCohort =
                    AllocationThroughputNormalizationV2CapturePlan
                        .RequiredPairs,
                effectPairsPerCohort =
                    AllocationThroughputNormalizationV2CapturePlan
                        .RequiredPairs,
                healthyUnchangedRatePairs =
                    AllocationThroughputNormalizationV2CapturePlan
                        .RequiredPairs,
                totalPairs =
                    AllocationThroughputNormalizationV2CapturePlan.TotalPairs,
                liveMcpSides =
                    AllocationThroughputNormalizationV2CapturePlan.LiveMcpSides,
            },
            capture = new
            {
                transport = "repository-built MCP over real stdio",
                durationSeconds =
                    AllocationThroughputNormalizationV2CapturePlan
                        .CaptureDurationSeconds,
                armDelayMilliseconds =
                    AllocationThroughputNormalizationV2CapturePlan
                        .ArmDelayMilliseconds,
                workloadMilliseconds =
                    AllocationThroughputNormalizationV2CapturePlan
                        .WorkloadMilliseconds,
                defaultWarmupMilliseconds =
                    AllocationThroughputNormalizationV2CapturePlan
                        .DefaultWarmupMilliseconds,
                topN = AllocationThroughputNormalizationV2CapturePlan.TopN,
                attemptTimeoutSeconds =
                    AllocationThroughputNormalizationV2CapturePlan
                        .AttemptTimeoutSeconds,
                mcpCompletionTimeoutSeconds =
                    AllocationThroughputNormalizationV2CapturePlan
                        .McpCompletionTimeoutSeconds,
                maxAllocationEvents =
                    AllocationDirectionalBoundedCalibrationPlan
                        .AllocationEventCap,
                maxAllocationFrames =
                    AllocationDirectionalBoundedCalibrationPlan
                        .AllocationFrameCap,
                allocationEventOperatingLimit =
                    AllocationThroughputNormalizationV2CapturePlan
                        .AllocationEventOperatingLimit,
                targetRuntimeEnvironment =
                    AllocationThroughputNormalizationV2CapturePlan
                        .RuntimeEnvironment,
            },
            selectors = new
            {
                pairFirst = true,
                repeatedPairSummary = "median and raw MAD",
                outlierRule =
                    "modified z-score 0.6745*(x-median)/MAD with strict abs(z)>3.5; when MAD is zero, unequal values are candidates",
                outliersRemainReported = true,
                minimumValidPairs =
                    AllocationThroughputNormalizationV2CapturePlan
                        .RequiredPairs,
                minimumInlierPairs =
                    AllocationThroughputNormalizationV2CapturePlan
                        .MinimumInlierPairs,
                healthyAaMaximumRelativeDelta =
                    AllocationDirectionalCalibrationBounds
                        .PracticalRelativeEquivalenceFloor,
                practicalRelativeFloor =
                    AllocationDirectionalCalibrationBounds
                        .PracticalRelativeEquivalenceFloor,
                equivalenceMargin =
                    "max(workload-specific robust A/A inlier envelope, 10%)",
                minimumThroughputMultiplier =
                    AllocationDirectionalCalibrationBounds
                        .MinimumThroughputMultiplier,
                manipulationRule =
                    "the median and every retained inlier operations/second multiplier must be strictly greater than 1.25x",
                normalizationSurvivesRule =
                    "the median and every retained inlier absolute estimated-sampled-bytes/operation delta are at or below the cohort equivalence margin",
                noGoRule =
                    "every registered cohort is assessable, establishes throughput, and has an inlier median absolute bytes/operation delta strictly above its equivalence margin",
                conflictingOrBoundaryRule = "INCONCLUSIVE",
            },
            cohorts =
                AllocationThroughputNormalizationV2CapturePlan.Cohorts.Select(
                    cohort => new
                    {
                        cohort.CohortId,
                        cohort.BaselinePacing.OperationIdentity,
                        baselinePacing = new
                        {
                            cohort.BaselinePacing.Shape,
                            cohort.BaselinePacing.PauseMilliseconds,
                            cohort.BaselinePacing.PauseEveryOperations,
                        },
                        currentPacing = new
                        {
                            cohort.CurrentPacing.Shape,
                            cohort.CurrentPacing.PauseMilliseconds,
                            cohort.CurrentPacing.PauseEveryOperations,
                        },
                        aa = Entry(cohort.AaEntry),
                        effect = Entry(cohort.EffectEntry),
                    }).ToArray(),
            healthyUnchangedRateControl = Entry(
                AllocationThroughputNormalizationV2CapturePlan.HealthyControl),
            equalAllocationInvariant = new
            {
                withinEveryPair = new[]
                {
                    "workload identity",
                    "allocation operation identity",
                    "requested payload bytes per operation",
                    "worker count",
                    "capture duration",
                    "arm delay",
                    "workload duration",
                    "warmup",
                    "retained bytes",
                    "logical operation counting",
                },
                onlyExplicitEffectDifference = "pacing",
                sourceBackedByFrozenHashes = true,
            },
            failurePolicy = "Every predeclared side and pair starts at most once. Failures and aborts remain in denominators. No retry, replacement, deletion, threshold fitting, or reinterpretation is permitted.",
            errorTaxonomy = new
            {
                permissionDenied =
                    "only evidence-backed diagnostic-endpoint authorization or socket access may use PermissionDenied",
                resourceLimit =
                    "retain MCP ResourceLimitExceeded plus allocation.capture.limit",
                otherFailures =
                    "retain truthful bounded stage, category, operation identifier, cleanup state, and privacy-safe digest/count evidence",
                successFallback = "forbidden",
            },
            decisionGates = new
            {
                noGo =
                    "complete valid inventory; healthy unchanged-rate control passes; every A/A/effect/control cell has five valid pairs and at least four inliers; every cohort exceeds 1.25x operations/second; and each cohort's inlier median absolute normalized delta exceeds its equivalence margin",
                normalizationSurvives =
                    "complete valid inventory; healthy unchanged-rate control passes; every cohort exceeds 1.25x operations/second and remains inclusively within its equivalence margin",
                inconclusive =
                    "incomplete inventory, failure or abort, operating-limit breach, failed manipulation, insufficient pairs/inliers, invalid A/A, unresolved equal-allocation or harness validity, boundary/dispersion ambiguity, or conflicting cohorts",
            },
            fixedStrideLimitations = new[]
            {
                "GCAllocationTick estimated bytes are fixed-stride sampled weights.",
                "The study can falsify or sustain only directional stability of the normalized sampled quantity under the registered pacing manipulations.",
                "It cannot establish exact allocation totals, retained memory, object counts, severity, health, regression, or causality.",
            },
            boundaries = new
            {
                blindedEvaluation = false,
                bootstrapResampling = false,
                confidenceIntervalSelector = false,
                weightedScore = false,
                postExecutionThresholdTuning = false,
                productImplementation = false,
                productFormulaOrSchemaChange = false,
                collectorOrRetentionChange = false,
                toolOrCatalogChange = false,
                downstreamDesignIssue = false,
            },
            frozenInputs = new
            {
                textHashCanonicalization =
                    AllocationDirectionalProtocol.TextHashCanonicalization,
                sourceFilesSha256 = sourceHashes,
                runtimeArtifactsSha256 = runtimeHashes,
                preservedArtifactsSha256 = preservedHashes,
            },
        };
        return JsonSerializer.SerializeToElement(
            document,
            AllocationDirectionalJson.Options);
    }

    private static object Entry(AllocationDirectionalPlanEntry entry)
        => new
        {
            entry.PairPrefix,
            entry.ComparisonAxis,
            baseline = Side(entry.Baseline),
            current = Side(entry.Current),
        };

    private static object Side(AllocationDirectionalPlannedSide side)
        => new
        {
            side.WorkloadIdentity,
            side.Shape,
            side.Workers,
            side.Phase,
            side.ExpectedPayloadBytesPerOperation,
            side.WarmupMilliseconds,
            side.RetainedBytes,
            side.DriveWorkload,
        };

    private static Dictionary<string, string> Hashes(
        IReadOnlyList<string> relativePaths,
        bool canonicalText)
    {
        var root = RepositoryRoot();
        return relativePaths.ToDictionary(
            path => path,
            path =>
            {
                var absolute = Path.Combine(
                    root,
                    path.Replace('/', Path.DirectorySeparatorChar));
                return canonicalText
                    ? AllocationDirectionalProtocol.Sha256(absolute)
                    : AllocationDirectionalProtocol.RawSha256(absolute);
            },
            StringComparer.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(
                   directory.FullName,
                   "DotnetDiagnostics.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException(
                "Could not locate the repository root for the throughput-normalization protocol document.");
    }
}
