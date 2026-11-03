using System.Text.Json;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal static class AllocationDirectionalV3ProtocolDocument
{
    public static JsonElement Build(
        string status,
        string? frozenAtUtc,
        bool includeFrozenHashes)
    {
        var sourceHashes = includeFrozenHashes
            ? Hashes(
                AllocationDirectionalV3Protocol.FrozenSourcePaths,
                canonicalText: true)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var runtimeHashes = includeFrozenHashes
            ? Hashes(
                AllocationDirectionalV3Protocol.FrozenRuntimeArtifactPaths,
                canonicalText: false)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var preservedHashes = Hashes(
            AllocationDirectionalV3Protocol.PreservedArtifactPaths,
            canonicalText: true);
        var document = new
        {
            schemaVersion = 3,
            protocolId = AllocationDirectionalV3Protocol.ProtocolId,
            protocolVersion = AllocationDirectionalV3Protocol.ProtocolVersion,
            status,
            frozenAtUtc,
            sourceBaseCommit = AllocationDirectionalV3Protocol.SourceBaseCommit,
            prerequisites = new
            {
                allocationErrorClassificationIssue = 1381,
                allocationErrorClassificationMerge =
                    "9cb6966e249439925ce11cb5871b4f7d808dd9cc",
                calibrationAndSelectorsIssue = 1382,
                calibrationAndSelectorsMerge =
                    AllocationDirectionalV3Protocol.SourceBaseCommit,
            },
            question = "Under comparable workload conditions, did estimated sampled allocation activity or allocation mix change enough to justify investigating a type or call site without promoting throughput-only, transient, incompatible, biased, partial, missing, or healthy controls?",
            modelVisibleMeasurementQualityFields = Array.Empty<string>(),
            requiredIndependentPairs = AllocationDirectionalBounds.RequiredPairs,
            inventory = new
            {
                totalPairs = AllocationDirectionalV3CapturePlan.TotalPairs,
                liveMcpPairs = AllocationDirectionalV3CapturePlan.LiveMcpPairs,
                controlledProjectionPairs =
                    AllocationDirectionalV3CapturePlan.ControlledProjectionPairs,
                liveMcpSides = AllocationDirectionalV3CapturePlan.LiveMcpSides,
                controlledProjectionSides =
                    AllocationDirectionalV3CapturePlan.ControlledProjectionSides,
            },
            capture = new
            {
                transport = "repository-built MCP over real stdio",
                durationSeconds =
                    AllocationDirectionalV3CapturePlan.CaptureDurationSeconds,
                armDelayMilliseconds =
                    AllocationDirectionalV3CapturePlan.ArmDelayMilliseconds,
                workloadMilliseconds =
                    AllocationDirectionalV3CapturePlan.WorkloadMilliseconds,
                defaultWarmupMilliseconds =
                    AllocationDirectionalV3CapturePlan.DefaultWarmupMilliseconds,
                topN = AllocationDirectionalV3CapturePlan.TopN,
                attemptTimeoutSeconds =
                    AllocationDirectionalV3CapturePlan.AttemptTimeoutSeconds,
                mcpCompletionTimeoutSeconds =
                    AllocationDirectionalV3CapturePlan.McpCompletionTimeoutSeconds,
                targetRuntimeEnvironment =
                    AllocationDirectionalV3CapturePlan.RuntimeEnvironment,
            },
            quantityContract = new
            {
                primary = "estimated sampled allocation bytes divided by completed workload operations",
                primaryUnit = "estimated sampled bytes per operation",
                throughputManipulation = "completed operations per capture second",
                throughputUnit = "operations per second",
                sampledBytesPerSecondRole = "descriptive only",
                rankedMovement = "absolute estimated sampled bytes per operation delta and share delta are always shown together",
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
                maximumAaRelativeDelta =
                    AllocationDirectionalBounds.MaximumAllowedAaRelativeDelta,
                maximumAaShareMovement =
                    AllocationDirectionalBounds.MaximumAllowedAaShareMovement,
                minimumThroughputMultiplier =
                    AllocationDirectionalCalibrationBounds.MinimumThroughputMultiplier,
                warmupPracticalFloor =
                    AllocationDirectionalCalibrationBounds.PracticalRelativeEffectFloor,
                warmupDecisionFloor = "max(workload-specific A/A envelope, 10%)",
                signRule = "every retained non-outlier must agree with the intended signed direction",
                convergenceRule =
                    "the median and every retained non-outlier absolute converged delta must return inside the workload-specific A/A envelope",
                insufficientEvidence = "explicit abstention",
            },
            capturePlan = AllocationDirectionalV3CapturePlan.Entries.Select(entry => new
            {
                scenario = entry.Scenario.ToString(),
                entry.PairPrefix,
                entry.ComparisonAxis,
                baseline = Side(entry.Baseline),
                current = Side(entry.Current),
            }).ToArray(),
            failurePolicy = "Every predeclared live side and pair starts at most once. All failures and aborts remain in denominators. No favorable retry, replacement, deletion, or reinterpretation is permitted.",
            errorTaxonomy = new
            {
                permissionDenied =
                    "requires evidence-backed diagnostic-endpoint authorization or socket-access failure",
                nonPermissionInvalidOperation =
                    "retains its truthful structured unavailable, invalid-state, or internal category",
                evidence = "bounded, stage-specific, privacy-safe provenance only",
                successFallback = "forbidden",
            },
            decisionGates = new
            {
                numericGo =
                    "Every scenario is assessable, every expected control behaves safely, signed effects satisfy the frozen pair summaries, and no unsafe directional evidence is observed.",
                blindedEvaluation =
                    "Runs exactly once only after numeric GO and compares blinded raw-total versus directional packets without measurement-quality fields.",
                go =
                    "Numeric GO plus blinded improvement in support or next-step usefulness without any increase in unsupported certainty.",
                noGo =
                    "Normalization failure, overlapping intended effects, unsafe controls, no operational value, worsened certainty, or unacceptable boundedness/catalog cost.",
                inconclusive =
                    "Incomplete inventory, incomplete assessability, unresolved harness validity, invalid accounting, or incomplete blinded evaluation after numeric GO.",
            },
            boundaries = new
            {
                productImplementation = false,
                productSchemaChange = false,
                collectorChange = false,
                retentionChange = false,
                toolOrCatalogChange = false,
                downstreamDesignIssueOnlyAfterReviewedGo = true,
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
                "Could not locate the repository root for the allocation directional v3 protocol document.");
    }
}
