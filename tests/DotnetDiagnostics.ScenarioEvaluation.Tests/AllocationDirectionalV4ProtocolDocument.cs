using System.Text.Json;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal static class AllocationDirectionalV4ProtocolDocument
{
    public static JsonElement Build(
        string status,
        string? frozenAtUtc,
        bool includeFrozenHashes)
    {
        var sourceHashes = includeFrozenHashes
            ? Hashes(
                AllocationDirectionalV4Protocol.FrozenSourcePaths,
                canonicalText: true)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var runtimeHashes = includeFrozenHashes
            ? Hashes(
                AllocationDirectionalV4Protocol.FrozenRuntimeArtifactPaths,
                canonicalText: false)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var preservedHashes = Hashes(
            AllocationDirectionalV4Protocol.PreservedArtifactPaths,
            canonicalText: true);
        var document = new
        {
            schemaVersion = 4,
            protocolId = AllocationDirectionalV4Protocol.ProtocolId,
            protocolVersion = AllocationDirectionalV4Protocol.ProtocolVersion,
            status,
            frozenAtUtc,
            sourceBaseCommit = AllocationDirectionalV4Protocol.SourceBaseCommit,
            prerequisites = new
            {
                boundedCalibrationIssue = 1387,
                boundedCalibrationMerge =
                    AllocationDirectionalV4Protocol.SourceBaseCommit,
                boundedCalibrationVersion =
                    AllocationDirectionalBoundedCalibrationPlan.CalibrationVersion,
                preservedFinalProtocolIssue = 1385,
                ledgerSchemaVersion = AllocationDirectionalLedger.CurrentSchemaVersion,
            },
            question = "Under comparable workload conditions, did estimated sampled allocation activity or allocation mix change enough to justify investigating a type or call site without promoting throughput-only, transient, incompatible, fixed-stride-biased, partial, missing, or healthy controls?",
            modelVisibleMeasurementQualityFields = Array.Empty<string>(),
            requiredIndependentPairs = AllocationDirectionalBounds.RequiredPairs,
            inventory = new
            {
                totalPairs = AllocationDirectionalV4CapturePlan.TotalPairs,
                liveMcpPairs = AllocationDirectionalV4CapturePlan.LiveMcpPairs,
                controlledProjectionPairs =
                    AllocationDirectionalV4CapturePlan.ControlledProjectionPairs,
                liveMcpSides = AllocationDirectionalV4CapturePlan.LiveMcpSides,
                controlledProjectionSides =
                    AllocationDirectionalV4CapturePlan.ControlledProjectionSides,
            },
            capture = new
            {
                transport = "repository-built MCP over real stdio",
                durationSeconds =
                    AllocationDirectionalV4CapturePlan.CaptureDurationSeconds,
                armDelayMilliseconds =
                    AllocationDirectionalV4CapturePlan.ArmDelayMilliseconds,
                workloadMilliseconds =
                    AllocationDirectionalV4CapturePlan.WorkloadMilliseconds,
                defaultWarmupMilliseconds =
                    AllocationDirectionalV4CapturePlan.DefaultWarmupMilliseconds,
                burstPauseMilliseconds = 1,
                burstPauseEveryOperations = 8,
                boundedShapes = new[]
                {
                    "low-bounded-v4",
                    "increased-bounded-v4",
                    "periodic-a-bounded-v4",
                    "periodic-b-bounded-v4",
                },
                topN = AllocationDirectionalV4CapturePlan.TopN,
                attemptTimeoutSeconds =
                    AllocationDirectionalV4CapturePlan.AttemptTimeoutSeconds,
                mcpCompletionTimeoutSeconds =
                    AllocationDirectionalV4CapturePlan.McpCompletionTimeoutSeconds,
                maxAllocationEvents =
                    AllocationDirectionalBoundedCalibrationPlan.AllocationEventCap,
                maxAllocationFrames =
                    AllocationDirectionalBoundedCalibrationPlan.AllocationFrameCap,
                allocationEventOperatingLimit =
                    AllocationDirectionalV4CapturePlan.AllocationEventOperatingLimit,
                targetRuntimeEnvironment =
                    AllocationDirectionalV4CapturePlan.RuntimeEnvironment,
            },
            quantityContract = new
            {
                primary = "estimated sampled allocation bytes divided by completed workload operations",
                primaryUnit = "estimated sampled bytes per operation",
                throughputManipulation = "completed operations per capture second",
                throughputUnit = "operations per second",
                sampledBytesPerSecondRole = "descriptive only",
                rankedAbsoluteMovement = "absolute estimated sampled bytes per operation movement normalized by baseline total bytes per operation",
                rankedShareMovement = "independent percentage-point movement within the type or site inventory",
                allocationTicks = "sampler provenance only; never object count",
                requiredObservationFields = new[]
                {
                    "quantity",
                    "unit",
                    "baselineCurrentSemantics",
                    "workloadIdentity",
                    "denominator",
                    "normalization",
                    "assumptions",
                    "availability",
                    "abstentionReason",
                },
            },
            selectors = new
            {
                pairFirst = true,
                repeatedPairSummary = "median and raw MAD",
                outlierRule =
                    "modified z-score 0.6745*(x-median)/MAD with strict abs(z)>3.5; when MAD is zero, unequal values are candidates",
                outliersRemainReported = true,
                minimumValidPairs = AllocationDirectionalBounds.RequiredPairs,
                minimumInlierPairs =
                    AllocationDirectionalCalibrationBounds.MinimumInlierPairs,
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
                movementRule =
                    "median must be strictly beyond the practical/A/A floor and every retained non-outlier sign must agree",
                convergenceRule =
                    "median and every retained non-outlier absolute converged delta may equal but not exceed the equivalence margin",
                mixRule =
                    "total equivalence plus independently normalized absolute and share movement for both type and site",
                fixedStrideRule =
                    "require bounded A/A and both reorder directions, verify total equivalence, and abstain from type/site pressure or regression interpretation",
                insufficientEvidence = "explicit abstention",
            },
            scenarioContracts = new object[]
            {
                Contract(
                    AllocationDirectionalScenario.TrueIncrease,
                    "estimated sampled bytes per operation",
                    "strict positive movement beyond max(robust A/A, 10%)",
                    "abstain when valid-pair/inlier/sign requirements fail"),
                Contract(
                    AllocationDirectionalScenario.MixShiftStableTotal,
                    "relative normalized total plus type/site absolute and share movement",
                    "total equivalent; both type and site exceed independent absolute and share floors",
                    "abstain when either attribution level or any pair summary is unavailable"),
                Contract(
                    AllocationDirectionalScenario.ThroughputIncreaseStablePerOperation,
                    "operations per second and estimated sampled bytes per operation",
                    "throughput strictly above 1.25x; normalized bytes equivalent",
                    "abstain when manipulation or equivalence is not assessable"),
                Contract(
                    AllocationDirectionalScenario.LowerAllocationStableRetained,
                    "estimated sampled bytes per operation and declared retained bytes",
                    "strict negative movement beyond max(robust A/A, 10%); retained bytes equal",
                    "abstain when movement or retained-value parity is unavailable"),
                Contract(
                    AllocationDirectionalScenario.WarmupTransientConverges,
                    "early and converged relative estimated sampled bytes per operation",
                    "early strictly beyond practical/A/A floor; converged inclusively equivalent",
                    "abstain when any phase lacks the registered pair/inlier inventory"),
                Contract(
                    AllocationDirectionalScenario.ChangedWorkload,
                    "workload identity",
                    "incompatible identities prevent directional interpretation",
                    "always abstain from directional claims across incompatible identities"),
                Contract(
                    AllocationDirectionalScenario.FixedStrideBias,
                    "normalized total plus fixed-stride provenance",
                    "both A/A shapes and reorder directions complete; total equivalent; type/site pressure suppressed",
                    "abstain from type/site direction under reorder sensitivity"),
                Contract(
                    AllocationDirectionalScenario.PartialAttribution,
                    "independent type and site availability",
                    "type remains available while site explicitly abstains",
                    "never infer unavailable site evidence from type evidence"),
                Contract(
                    AllocationDirectionalScenario.MissingOrZeroWeight,
                    "operation denominator and sampled weight availability",
                    "explicit unavailable quantity with retained denominator accounting",
                    "never promote zero-denominator or zero-weight evidence"),
                Contract(
                    AllocationDirectionalScenario.HealthyAa,
                    "absolute relative estimated sampled bytes per operation",
                    "median and every retained inlier at or below 10%",
                    "abstain when three valid pairs and two inliers are not retained"),
            },
            capturePlan = AllocationDirectionalV4CapturePlan.Entries.Select(entry => new
            {
                scenario = entry.Scenario.ToString(),
                entry.PairPrefix,
                entry.ComparisonAxis,
                baseline = Side(entry.Baseline),
                current = Side(entry.Current),
            }).ToArray(),
            failurePolicy = "Every predeclared live side and pair starts at most once. All failures and aborts remain in denominators. No retry, replacement, deletion, favorable threshold fitting, or reinterpretation is permitted.",
            errorTaxonomy = new
            {
                permissionDenied =
                    "only an evidence-backed diagnostic-endpoint authorization or socket-access failure may use PermissionDenied",
                resourceLimit =
                    "retains MCP ResourceLimitExceeded plus the public allocation.capture.limit identifier",
                nonPermissionInvalidOperation =
                    "retains its truthful structured unavailable, invalid-state, transport, timeout, cancellation, cleanup, or internal category",
                errorDetail =
                    "bounded length and privacy-safe state/count only; raw exception detail remains private",
                successFallback = "forbidden",
            },
            decisionGates = new
            {
                numericGo =
                    "The complete registered inventory is assessable, every expected contract passes, no unsafe directional evidence is observed, and every successful live side remains at or below 75,000 allocation events.",
                blindedEvaluation =
                    "Freeze and run separately exactly once only after numeric GO, comparing raw/no-directional evidence with compact directional evidence and zero model-visible quality fields.",
                go =
                    "Numeric GO plus blinded improvement in claim support or safe next-step usefulness without increased unsupported certainty, unauthorized actions, or unhelpful next steps.",
                noGo =
                    "Complete valid evidence shows unsafe normalization/effect separation, false control claims, no operational value, worsened certainty, or unacceptable boundedness/resource cost.",
                inconclusive =
                    "Incomplete inventory, incomplete assessability, unresolved harness validity, invalid accounting, or blocked/incomplete blinded evaluation after numeric GO.",
            },
            boundaries = new
            {
                productImplementation = false,
                productSchemaChange = false,
                collectorChange = false,
                retentionChange = false,
                toolOrCatalogChange = false,
                downstreamDesignIssueOnlyAfterExplicitReviewedOperationalGo = true,
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

    private static object Contract(
        AllocationDirectionalScenario scenario,
        string quantityAndUnit,
        string expectation,
        string abstention)
        => new
        {
            scenario = scenario.ToString(),
            quantityAndUnit,
            expectation,
            abstention,
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
               && !File.Exists(Path.Combine(directory.FullName, "DotnetDiagnostics.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException(
                "Could not locate the repository root for the allocation directional v4 protocol document.");
    }
}
