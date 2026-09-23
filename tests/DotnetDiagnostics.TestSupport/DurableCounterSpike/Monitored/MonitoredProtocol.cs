using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.Monitored;

internal static class MonitoredProtocolVersions
{
    internal const string PlanSchema = "durable-monitored-plan/1";
    internal const string ManifestSchema = "durable-monitored-run-manifest/1";
    internal const string AuthorizationSchema = "durable-monitored-run-authorization/1";
    internal const string AttributionSchema = "durable-monitored-attribution-map/2";
    internal const string EvidenceEncodingSchema = "durable-monitored-evidence-encoding/2";
    internal const string ComponentEvidenceSchema = "durable-monitored-component-evidence/3";
    internal const string WorkerDescriptorSchema = "durable-monitored-worker-descriptor/1";
    internal const string WorkerResultSchema = "durable-monitored-worker-result/2";
    internal const string BaselineProtocolSha256 =
        "faaced68f26a53b7841713d4b1fe1044ce0dd3b277bbac2a9c6f9b6a09f498dd";
    internal const string SuccessorProtocolSha256 =
        "e37c44917f738f8d798399258961aa28548b0f4df351111cec047b29c0a50e71";
    internal const string BaselineProtocolPath = "docs/design/durable-capture-comparison-protocol.json";
    internal const string ProposalPath = "docs/design/durable-monitored-comparison-proposal.json";
    internal const string SuccessorProtocolPath = "docs/design/durable-capture-monitored-protocol.json";
}

internal sealed record MonitoredExecutionSpec(
    int Ordinal,
    string CaseId,
    string Candidate,
    string CaseClass,
    int MaximumSeconds,
    int MaximumAttempts);

internal sealed record MonitoredExecutionPlan(
    string Schema,
    int ProtocolRevision,
    string ProtocolJson,
    string ProtocolJsonSha256,
    int PlannedExecutions,
    int MaximumCampaignSeconds,
    IReadOnlyList<MonitoredExecutionSpec> Executions);

internal static class MonitoredExecutionPlanner
{
    private static readonly (string Id, string Class)[] CandidateCases =
    [
        ("Q1", "semantic"),
        ("Q2", "semantic"),
        ("N1", "stress"),
        ("B1", "stress"),
        ("M1", "stress"),
        ("O1", "stress"),
        ("C1", "stress"),
        ("F1", "isolated-fault"),
        ("F2", "isolated-fault"),
        ("F3", "isolated-fault"),
        ("F4", "isolated-fault"),
        ("F5", "isolated-fault"),
    ];

    internal static IReadOnlyList<MonitoredExecutionSpec> Expand()
    {
        var executions = new List<MonitoredExecutionSpec>(35)
        {
            new(1, "P0", "shared", "candidate-blind-readiness", 120, 1),
            new(2, "P1", "E", "candidate-blind-readiness", 120, 1),
        };

        foreach (var (item, index) in CandidateCases.Select(static (value, index) => (value, index)))
        {
            var candidates = index % 2 == 0 ? new[] { "A", "B" } : new[] { "B", "A" };
            foreach (var candidate in candidates)
            {
                executions.Add(new(
                    executions.Count + 1,
                    item.Id,
                    candidate,
                    item.Class,
                    120,
                    1));
            }
        }

        AppendLiveBlock(executions, "L1", ["E", "A", "B"]);
        AppendLiveBlock(executions, "L2", ["B", "E", "A"]);
        AppendLiveBlock(executions, "L3", ["A", "B", "E"]);

        if (executions.Count != 35
            || executions.Select(static execution => execution.Ordinal)
                .Where(static ordinal => ordinal is < 1 or > 35)
                .Any())
        {
            throw new InvalidOperationException("The frozen monitored execution plan did not expand to exactly 35 entries.");
        }
        return executions;
    }

    internal static MonitoredExecutionPlan CreatePlan(
        string repositoryRoot,
        string protocolPath)
    {
        var validation = MonitoredSuccessorProtocolValidator.Validate(repositoryRoot, protocolPath);
        return new MonitoredExecutionPlan(
            MonitoredProtocolVersions.PlanSchema,
            4,
            Path.GetRelativePath(Path.GetFullPath(repositoryRoot), validation.ProtocolPath)
                .Replace(Path.DirectorySeparatorChar, '/'),
            validation.ProtocolSha256,
            35,
            4_200,
            Expand());
    }

    internal static void ValidatePlan(MonitoredExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!string.Equals(plan.Schema, MonitoredProtocolVersions.PlanSchema, StringComparison.Ordinal)
            || plan.ProtocolRevision != 4
            || plan.PlannedExecutions != 35
            || plan.MaximumCampaignSeconds != 4_200
            || !plan.Executions.SequenceEqual(Expand()))
        {
            throw Error(
                "ExecutionPlanMismatch",
                "The resolved manifest must carry the exact frozen 35-execution expansion and order.");
        }
    }

    private static void AppendLiveBlock(
        List<MonitoredExecutionSpec> executions,
        string caseId,
        IReadOnlyList<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            executions.Add(new(executions.Count + 1, caseId, candidate, "live", 120, 1));
        }
    }

    private static DurableStorageExperimentException Error(string code, string message) => new(code, message);
}

internal sealed record MonitoredProtocolValidation(
    string ProtocolPath,
    string ProtocolSha256,
    JsonElement Monitoring,
    JsonElement StorageSemantics);

internal static class MonitoredSuccessorProtocolValidator
{
    private static readonly string[] InheritedSections =
    [
        "scope",
        "limits",
        "recordSemantics",
        "fixture",
        "durabilityProfile",
        "cases",
        "liveProfile",
        "execution",
        "contextCost",
    ];
    private static readonly HashSet<string> SuccessorProperties = new(
        [
            "revision",
            "status",
            "tracking",
            .. InheritedSections,
            "decision",
            "monitoring",
            "storageSemantics",
            "baseline",
            "freeze",
            "readiness",
        ],
        StringComparer.Ordinal);

    internal static MonitoredProtocolValidation Validate(
        string repositoryRoot,
        string protocolPath)
    {
        var validation = ValidateShape(repositoryRoot, protocolPath);
        if (!string.Equals(
                validation.ProtocolSha256,
                MonitoredProtocolVersions.SuccessorProtocolSha256,
                StringComparison.Ordinal))
        {
            throw Error(
                "SuccessorProtocolHashMismatch",
                "The revision-4 protocol does not match the independently accepted JSON.");
        }
        return validation;
    }

    internal static MonitoredProtocolValidation ValidateShape(
        string repositoryRoot,
        string protocolPath)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var successorPath = MonitoredPathRules.ResolveRepositoryFile(root, protocolPath);
        var baselinePath = MonitoredPathRules.ResolveRepositoryFile(
            root,
            MonitoredProtocolVersions.BaselineProtocolPath);
        var proposalPath = MonitoredPathRules.ResolveRepositoryFile(
            root,
            MonitoredProtocolVersions.ProposalPath);

        var baselineBytes = MonitoredFile.ReadBounded(baselinePath, 1_048_576);
        var proposalBytes = MonitoredFile.ReadBounded(proposalPath, 1_048_576);
        var successorBytes = MonitoredFile.ReadBounded(successorPath, 1_048_576);
        var baselineHash = MonitoredFile.HashBytes(baselineBytes);
        if (!string.Equals(
                baselineHash,
                MonitoredProtocolVersions.BaselineProtocolSha256,
                StringComparison.Ordinal))
        {
            throw Error(
                "BaselineProtocolHashMismatch",
                "The immutable revision-3 protocol hash no longer matches the adopted baseline.");
        }

        using var baselineDocument = JsonDocument.Parse(baselineBytes);
        using var proposalDocument = JsonDocument.Parse(proposalBytes);
        using var successorDocument = JsonDocument.Parse(successorBytes);
        var baseline = baselineDocument.RootElement;
        var proposal = proposalDocument.RootElement;
        var successor = successorDocument.RootElement;

        RequireInteger(successor, "revision", 4, "SuccessorRevisionMismatch");
        RequireString(successor, "status", "adopted-awaiting-runner-readiness", "SuccessorStatusMismatch");
        if (successor.EnumerateObject().Any(property => !SuccessorProperties.Contains(property.Name))
            || SuccessorProperties.Any(property => !successor.TryGetProperty(property, out _)))
        {
            throw Error(
                "SuccessorShapeMismatch",
                "The revision-4 successor has missing or additional top-level fields.");
        }

        ValidateTracking(successor.GetProperty("tracking"));
        foreach (var section in InheritedSections)
        {
            RequireDeepEqual(
                baseline.GetProperty(section),
                successor.GetProperty(section),
                "InheritedProtocolDrift",
                $"Successor section '{section}' must be exactly revision 3.");
        }

        ValidateDecision(baseline.GetProperty("decision"), successor.GetProperty("decision"));
        RequireDeepEqual(
            proposal.GetProperty("monitoring"),
            successor.GetProperty("monitoring"),
            "MonitoringContractDrift",
            "The successor monitoring object must exactly match the adopted proposal.");
        RequireDeepEqual(
            proposal.GetProperty("semanticOverrides"),
            successor.GetProperty("storageSemantics"),
            "StorageSemanticsDrift",
            "The successor storage semantics must exactly match the adopted proposal.");
        ValidateBaseline(successor.GetProperty("baseline"));
        ValidateFreeze(
            baseline.GetProperty("freeze"),
            successor.GetProperty("freeze"));
        ValidateReadiness(successor.GetProperty("readiness"));
        ValidateFrozenExecution(successor.GetProperty("execution"), successor.GetProperty("cases"));

        return new MonitoredProtocolValidation(
            successorPath,
            MonitoredFile.HashBytes(successorBytes),
            successor.GetProperty("monitoring").Clone(),
            successor.GetProperty("storageSemantics").Clone());
    }

    private static void ValidateTracking(JsonElement tracking)
    {
        RequireInteger(tracking, "issue", 1_008, "TrackingContractDrift");
        RequireInteger(tracking, "parent", 999, "TrackingContractDrift");
        RequireInteger(tracking, "dc4", 1_009, "TrackingContractDrift");
        RequireInteger(tracking, "dc5", 1_003, "TrackingContractDrift");
        RequireInteger(tracking, "proposal", 1_022, "TrackingContractDrift");
        if (tracking.EnumerateObject().Count() != 5)
        {
            throw Error(
                "TrackingContractDrift",
                "The revision-4 tracking object has missing or additional fields.");
        }
    }

    private static void ValidateDecision(JsonElement baseline, JsonElement successor)
    {
        foreach (var property in baseline.EnumerateObject())
        {
            if (!successor.TryGetProperty(property.Name, out var actual))
            {
                throw Error("DecisionContractDrift", $"Successor decision is missing '{property.Name}'.");
            }
            RequireDeepEqual(
                property.Value,
                actual,
                "DecisionContractDrift",
                $"Successor decision field '{property.Name}' changed.");
        }
        RequireString(
            successor,
            "recommendationScope",
            "monitored-scope-only",
            "DecisionContractDrift");
        if (successor.EnumerateObject().Count() != baseline.EnumerateObject().Count() + 1)
        {
            throw Error(
                "DecisionContractDrift",
                "Successor decision may add only recommendationScope to revision 3.");
        }
    }

    private static void ValidateBaseline(JsonElement baseline)
    {
        if (baseline.EnumerateObject().Count() != 3)
        {
            throw Error("BaselineIdentityMismatch", "The successor baseline identity has unexpected fields.");
        }
        RequireInteger(baseline, "revision", 3, "BaselineIdentityMismatch");
        RequireString(
            baseline,
            "path",
            "durable-capture-comparison-protocol.json",
            "BaselineIdentityMismatch");
        RequireString(
            baseline,
            "sha256",
            MonitoredProtocolVersions.BaselineProtocolSha256,
            "BaselineIdentityMismatch");
    }

    private static void ValidateFreeze(
        JsonElement baseline,
        JsonElement successor)
    {
        foreach (var property in baseline.EnumerateObject())
        {
            if (string.Equals(property.Name, "requiredIdentities", StringComparison.Ordinal))
            {
                continue;
            }
            if (!successor.TryGetProperty(property.Name, out var actual))
            {
                throw Error("FreezeContractDrift", $"Successor freeze is missing '{property.Name}'.");
            }
            RequireDeepEqual(
                property.Value,
                actual,
                "FreezeContractDrift",
                $"Successor freeze field '{property.Name}' changed.");
        }

        var baselineIdentities = baseline.GetProperty("requiredIdentities")
            .EnumerateArray()
            .Select(static item => item.GetString()!)
            .ToArray();
        var expectedIdentities = baselineIdentities.Concat(
        [
            "runnerCommit",
            "monitorCommit",
            "monitorComponentEvidenceSha256",
            "attributionMapSha256",
            "evidenceEncodingSha256",
        ]);
        var actualIdentities = successor.GetProperty("requiredIdentities")
            .EnumerateArray()
            .Select(static item => item.GetString())
            .ToArray();
        if (!actualIdentities.SequenceEqual(expectedIdentities, StringComparer.Ordinal)
            || successor.EnumerateObject().Count() != baseline.EnumerateObject().Count())
        {
            throw Error(
                "FreezeContractDrift",
                "The successor freeze must retain the revision-3 rules and append the five monitored identities.");
        }
    }

    private static void ValidateReadiness(JsonElement readiness)
    {
        foreach (var name in new[]
        {
            "maintainerAdopted",
            "executionConditionallyAuthorized",
            "runnerReadinessRequired",
            "independentRunnerReviewRequired",
        })
        {
            if (!readiness.TryGetProperty(name, out var value)
                || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !value.GetBoolean())
            {
                throw Error("SuccessorReadinessMismatch", $"Successor readiness '{name}' must be true.");
            }
        }
        if (!readiness.TryGetProperty("anyExecutionPerformed", out var performed)
            || performed.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || performed.GetBoolean())
        {
            throw Error(
                "SuccessorReadinessMismatch",
                "The adopted successor must state that no execution has yet been performed.");
        }
        if (readiness.EnumerateObject().Count() != 5)
        {
            throw Error(
                "SuccessorReadinessMismatch",
                "The successor readiness object has unexpected fields.");
        }
    }

    private static void ValidateFrozenExecution(JsonElement execution, JsonElement cases)
    {
        RequireInteger(execution, "plannedExecutions", 35, "ExecutionContractDrift");
        RequireInteger(execution, "maxAttemptsPerExecution", 1, "ExecutionContractDrift");
        RequireInteger(execution, "maxSecondsPerExecution", 120, "ExecutionContractDrift");
        RequireInteger(execution, "maxCampaignSeconds", 4_200, "ExecutionContractDrift");
        if (cases.ValueKind != JsonValueKind.Array || cases.GetArrayLength() != 17)
        {
            throw Error("ExecutionContractDrift", "The successor must retain all 17 frozen case definitions.");
        }
    }

    private static void RequireInteger(
        JsonElement parent,
        string name,
        int expected,
        string code)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number
            || value.GetInt32() != expected)
        {
            throw Error(code, $"Protocol field '{name}' must equal {expected}.");
        }
    }

    private static void RequireString(
        JsonElement parent,
        string name,
        string expected,
        string code)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || !string.Equals(value.GetString(), expected, StringComparison.Ordinal))
        {
            throw Error(code, $"Protocol field '{name}' must equal '{expected}'.");
        }
    }

    private static void RequireDeepEqual(
        JsonElement expected,
        JsonElement actual,
        string code,
        string message)
    {
        if (!JsonEquals(expected, actual))
        {
            throw Error(code, message);
        }
    }

    private static bool JsonEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }
        return left.ValueKind switch
        {
            JsonValueKind.Object => ObjectEquals(left, right),
            JsonValueKind.Array => left.GetArrayLength() == right.GetArrayLength()
                && left.EnumerateArray().Zip(right.EnumerateArray())
                    .All(static pair => JsonEquals(pair.First, pair.Second)),
            JsonValueKind.String => string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal),
            JsonValueKind.Number => NumbersEqual(left, right),
            JsonValueKind.True or JsonValueKind.False => left.GetBoolean() == right.GetBoolean(),
            JsonValueKind.Null or JsonValueKind.Undefined => true,
            _ => false,
        };
    }

    private static bool NumbersEqual(JsonElement left, JsonElement right)
    {
        if (left.TryGetInt64(out var leftInteger)
            && right.TryGetInt64(out var rightInteger))
        {
            return leftInteger == rightInteger;
        }
        if (left.TryGetDecimal(out var leftDecimal)
            && right.TryGetDecimal(out var rightDecimal))
        {
            return leftDecimal == rightDecimal;
        }
        return left.TryGetDouble(out var leftDouble)
            && right.TryGetDouble(out var rightDouble)
            && leftDouble.Equals(rightDouble);
    }

    private static bool ObjectEquals(JsonElement left, JsonElement right)
    {
        var leftProperties = left.EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => property.Value,
            StringComparer.Ordinal);
        var rightProperties = right.EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => property.Value,
            StringComparer.Ordinal);
        return leftProperties.Count == rightProperties.Count
            && leftProperties.All(pair =>
                rightProperties.TryGetValue(pair.Key, out var value)
                && JsonEquals(pair.Value, value));
    }

    private static DurableStorageExperimentException Error(string code, string message) => new(code, message);
}

internal sealed record MonitoredSourceCommits(
    string ProtocolCommit,
    string PipelineCommit,
    string AdapterCommit,
    string RunnerCommit,
    string MonitorCommit);

internal sealed record MonitoredBinaryIdentity(
    string Path,
    string Sha256);

internal sealed record MonitoredCgroupLevelFacts(
    string HierarchyPath,
    string FileSystemPath,
    string? MemoryMax,
    long? MemoryCurrentBytes,
    string? CpuMax,
    string? CpusetCpusEffective);

internal sealed record MonitoredCgroupFacts(
    string Version,
    string ProcSelfCgroup,
    string? MembershipPath,
    string? MountRoot,
    string? MountPoint,
    IReadOnlyList<MonitoredCgroupLevelFacts> MembershipAndAncestors,
    long? EffectiveAvailableMemoryBytes);

internal sealed record MonitoredHostFacts(
    DateTimeOffset ObservedAt,
    string OperatingSystem,
    string KernelRelease,
    string CpuModel,
    int LogicalCpuCount,
    long HostMemoryTotalBytes,
    long HostMemoryAvailableBytes,
    MonitoredCgroupFacts Cgroup,
    long EffectiveMemoryBytes,
    string FileSystem,
    string ArtifactDevice,
    string ArtifactMountSource,
    string ArtifactMountPoint,
    long ArtifactAvailableFreeBytes);

internal sealed record MonitoredClockDefinition(
    string MonotonicClock,
    long StopwatchFrequency,
    string SourceTimestampApi,
    string CheckedConversion,
    string Rounding);

internal sealed record MonitoredRunManifest(
    string Schema,
    string CampaignId,
    MonitoredExecutionPlan Plan,
    string FixtureManifest,
    string FixtureManifestSha256,
    string Q1InputSha256,
    string Q1OracleSha256,
    string Q2InputSha256,
    string Q2OracleSha256,
    MonitoredSourceCommits SourceCommits,
    MonitoredBinaryIdentity RuntimeBinary,
    MonitoredBinaryIdentity ToolBinary,
    MonitoredBinaryIdentity SampleBinary,
    IReadOnlyList<MonitoredBinaryIdentity> NativeBinaries,
    MonitoredHostFacts Host,
    MonitoredClockDefinition Clock,
    string AttributionMap,
    string AttributionMapSha256,
    string EvidenceEncoding,
    string EvidenceEncodingSha256,
    string MonitorComponentEvidence,
    string MonitorComponentEvidenceSha256,
    string EvidenceRoot,
    string HistoryRoot,
    string WorkspaceRoot,
    string OutputRoot,
    string AuthorizationReceipt);

internal sealed record MonitoredAuthorizationReceipt(
    string Schema,
    string CampaignId,
    string ManifestSha256,
    string ProtocolJsonSha256,
    string RunnerCommit,
    string MonitorCommit,
    string IndependentReviewer,
    DateTimeOffset AuthorizedAt,
    bool ExecutionConditionallyAuthorized,
    bool RunnerReadinessApproved,
    bool SingleCampaignOnly);

internal sealed record MonitoredAttributionRoot(
    string Role,
    string Path,
    bool Charged);

internal sealed record MonitoredRuntimeOnlyDescriptorProof(
    string Schema,
    string ResourceKind,
    string DescriptorTarget,
    string BackingFileSystem,
    long BackingFileSystemMagic,
    MonitoredBinaryIdentity RuntimeNativeBinary,
    bool RequireDeletedDescriptor,
    bool RequireZeroLinks,
    bool RequireExecutableBackingMapping,
    bool RequireExecutableRuntimeMapping,
    string Rationale);

internal sealed record MonitoredAttributionMap(
    string Schema,
    IReadOnlyList<MonitoredAttributionRoot> Roots,
    IReadOnlyList<string> PackageDirectoryNames,
    IReadOnlyList<string> RecoveryDirectoryNames,
    IReadOnlyList<string> ReadOnlyDependencyRoots,
    IReadOnlyList<string> RuntimeOnlyDescriptorTargets,
    IReadOnlyList<MonitoredRuntimeOnlyDescriptorProof> RuntimeOnlyDescriptorProofs,
    int MaximumTrackedIdentitiesPerSweep,
    int MaximumObservedPathUtf8Bytes,
    int MaximumDescriptorOnlyIdentitiesPerSweep);

internal sealed record MonitoredEvidenceEncoding(
    string Schema,
    string Format,
    string TextEncoding,
    string SummarySchema,
    string SummaryFieldMapSha256,
    int MaximumSummaryUtf8Bytes,
    int MaximumSummaryRecordsPerExecution,
    int MaximumStdoutUtf8Bytes,
    int MaximumStderrUtf8Bytes,
    bool FullPathListsRepeatedPerSweep,
    bool NonAtomicSweepsClaimInstantaneousPeaks);

internal sealed record MonitoredComponentEvidence(
    string Schema,
    DateTimeOffset ObservedAt,
    string Platform,
    string RunnerCommit,
    string MonitorCommit,
    string AttributionMapSha256,
    string SummarySchema,
    string SummaryFieldMapSha256,
    int DerivedMaximumSimultaneousIdentitiesEnforced,
    int DerivedMaximumRootedIdentities,
    int MaximumDescriptorOnlyIdentitiesEnforced,
    int RepresentativeMaximumRootedIdentitiesObserved,
    int RepresentativeMaximumDescriptorOnlyIdentitiesObserved,
    bool DescriptorOnlyCampaignFeasibilityEstablished,
    int MaximumSummaryUtf8BytesObserved,
    int WorstCaseSummaryUtf8Bytes,
    string WorstCaseSummarySha256,
    string WorstCaseSummaryRelativePath,
    int MaximumWorkerControlUtf8BytesObserved,
    int DerivedMaximumSummaryRecordsRequired,
    int MaximumSummaryRecordsEncoded,
    long DerivedMaximumCombinedSummaryControlBytes,
    int SourceRecoveryObservedSummaryRecords,
    long SourceRecoveryObservedSummaryBytes,
    int SourceRecoveryObservedControlRecords,
    long SourceRecoveryObservedControlBytes,
    bool SourceRecoverySharedBudgetExhaustionObserved,
    bool SourceRecoverySharedCancellationObserved,
    int PeriodicSummariesObserved,
    double MaximumPeriodicGapMillisecondsObserved,
    long MaximumObservedSweepBytes,
    bool PositiveMonitoringComplete,
    bool ManagedProcessObserved,
    bool RuntimeMemoryClassificationObserved,
    long RuntimeMemoryExcludedBytes,
    bool RootFileObserved,
    bool WritableDescriptorOutsideRootRejected,
    bool OpenUnlinkedDescriptorRejected,
    bool ProcessIdentityReuseRejected,
    bool KillHandoffReleasedObserverReferences,
    bool RepresentativeCandidatePackageInventoriesObserved,
    int RepresentativeMaximumFinalPackageFilesObserved,
    int RepresentativeMaximumTransientPackageFilesObserved,
    bool RepresentativeSqliteWalShmObserved,
    bool OutputLimitEnforced,
    bool IdentityLimitEnforced,
    string RawEvidenceRoot,
    int RawEvidenceFiles,
    string EvidenceSha256);

internal sealed record MonitoredManifestValidation(
    string Schema,
    bool Ready,
    string ManifestSha256,
    string AuthorizationReceiptSha256,
    string ProtocolJsonSha256,
    string CampaignRoot,
    int PlannedExecutions,
    IReadOnlyList<string> ReadinessChecks);

internal sealed record MonitoredValidatedManifest(
    MonitoredRunManifest Manifest,
    string RepositoryRoot,
    string ManifestPath,
    string ManifestSha256,
    string CampaignRoot,
    MonitoredAttributionMap Attribution,
    MonitoredEvidenceEncoding Encoding,
    MonitoredComponentEvidence ComponentEvidence,
    MonitoredAuthorizationReceipt Authorization,
    string AuthorizationSha256,
    MonitoredManifestValidation Summary);

internal static class MonitoredRunManifestValidator
{
    private static readonly string[] PackageDirectoryNames = ["package", "package-staging"];
    private static readonly string[] RecoveryDirectoryNames = ["recovery", "recovery-staging"];
    private static readonly string[] RuntimeOnlyDescriptorTargets = ["/dev/null", "/dev/urandom"];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };

    internal static MonitoredValidatedManifest Validate(
        string repositoryRoot,
        string manifestPath,
        bool requireAuthorization)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw Error("UnsupportedPlatform", "The monitored comparison runner is Linux-only.");
        }

        var root = Path.GetFullPath(repositoryRoot);
        var resolvedManifestPath = MonitoredPathRules.ResolveExistingFile(manifestPath);
        var manifestBytes = MonitoredFile.ReadBounded(resolvedManifestPath, 1_048_576);
        var manifestHash = MonitoredFile.HashBytes(manifestBytes);
        var manifest = JsonSerializer.Deserialize<MonitoredRunManifest>(manifestBytes, JsonOptions)
            ?? throw Error("InvalidMonitoredManifest", "The monitored run manifest was empty.");
        if (!string.Equals(manifest.Schema, MonitoredProtocolVersions.ManifestSchema, StringComparison.Ordinal))
        {
            throw Error(
                "UnsupportedMonitoredManifestSchema",
                "Revision-3 foundation manifests cannot authorize monitored execution.");
        }

        RequireSafeIdentity(manifest.CampaignId, nameof(manifest.CampaignId), allowSlash: false);
        MonitoredExecutionPlanner.ValidatePlan(manifest.Plan);
        var protocol = MonitoredSuccessorProtocolValidator.Validate(root, manifest.Plan.ProtocolJson);
        RequireHash(manifest.Plan.ProtocolJsonSha256, protocol.ProtocolSha256, "SuccessorProtocolHashMismatch");

        var fixturePath = MonitoredPathRules.ResolveRepositoryFile(root, manifest.FixtureManifest);
        RequireHash(
            manifest.FixtureManifestSha256,
            MonitoredFile.HashFile(fixturePath),
            "FixtureManifestHashMismatch");
        ValidateFixtureManifest(fixturePath, manifest);
        ValidateCommits(manifest.SourceCommits);
        ValidateBinary(manifest.RuntimeBinary, "RuntimeBinary");
        ValidateBinary(manifest.ToolBinary, "ToolBinary");
        ValidateBinary(manifest.SampleBinary, "SampleBinary");
        if (manifest.NativeBinaries.Count == 0)
        {
            throw Error("MissingNativeBinaryIdentity", "At least one native storage/runtime binary identity is required.");
        }
        foreach (var native in manifest.NativeBinaries)
        {
            ValidateBinary(native, "NativeBinary");
        }

        ValidateHost(manifest.Host, manifest.EvidenceRoot);
        ValidateClock(manifest.Clock);

        var attributionPath = MonitoredPathRules.ResolveExistingFile(manifest.AttributionMap);
        var encodingPath = MonitoredPathRules.ResolveExistingFile(manifest.EvidenceEncoding);
        var componentPath = MonitoredPathRules.ResolveExistingFile(manifest.MonitorComponentEvidence);
        RequireHash(
            manifest.AttributionMapSha256,
            MonitoredFile.HashFile(attributionPath),
            "AttributionMapHashMismatch");
        RequireHash(
            manifest.EvidenceEncodingSha256,
            MonitoredFile.HashFile(encodingPath),
            "EvidenceEncodingHashMismatch");
        RequireHash(
            manifest.MonitorComponentEvidenceSha256,
            MonitoredFile.HashFile(componentPath),
            "MonitorComponentEvidenceHashMismatch");
        var attribution = DeserializeBounded<MonitoredAttributionMap>(attributionPath);
        var encoding = DeserializeBounded<MonitoredEvidenceEncoding>(encodingPath);
        var component = DeserializeBounded<MonitoredComponentEvidence>(componentPath);
        ValidateAttribution(manifest, attribution);
        ValidateEncoding(encoding);
        ValidateComponentEvidence(manifest, encoding, component);

        var evidenceRoot = MonitoredPathRules.ResolveAbsoluteDirectory(manifest.EvidenceRoot, mustExist: true);
        var evidenceMode = File.GetUnixFileMode(evidenceRoot);
        var requiredOwnerMode = UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute;
        var forbiddenMode = UnixFileMode.GroupRead
            | UnixFileMode.GroupWrite
            | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead
            | UnixFileMode.OtherWrite
            | UnixFileMode.OtherExecute;
        if ((evidenceMode & requiredOwnerMode) != requiredOwnerMode
            || (evidenceMode & forbiddenMode) != 0)
        {
            throw Error(
                "EvidenceRootNotPrivate",
                "The unique evidence root must have owner-only read/write/execute permissions.");
        }
        var campaignRoot = MonitoredPathRules.CombineContained(evidenceRoot, manifest.CampaignId);
        ValidateOwnedRoot(manifest.HistoryRoot, campaignRoot, "history");
        ValidateOwnedRoot(manifest.WorkspaceRoot, campaignRoot, "workspace");
        ValidateOwnedRoot(manifest.OutputRoot, campaignRoot, "outputs");

        var authorization = requireAuthorization
            ? ValidateAuthorization(manifest, manifestHash)
            : new MonitoredAuthorizationReceipt(
                MonitoredProtocolVersions.AuthorizationSchema,
                manifest.CampaignId,
                manifestHash,
                manifest.Plan.ProtocolJsonSha256,
                manifest.SourceCommits.RunnerCommit,
                manifest.SourceCommits.MonitorCommit,
                "validation-without-authorization",
                DateTimeOffset.UnixEpoch,
                ExecutionConditionallyAuthorized: false,
                RunnerReadinessApproved: false,
                SingleCampaignOnly: true);
        var authorizationHash = requireAuthorization
            ? MonitoredFile.HashFile(MonitoredPathRules.ResolveExistingFile(manifest.AuthorizationReceipt))
            : string.Empty;

        var checks = new[]
        {
            "revision4-successor-shape",
            "frozen-35-execution-plan",
            "protocol-fixture-oracle-hashes",
            "source-and-binary-identities",
            "host-and-clock-facts",
            "attribution-and-evidence-encoding",
            "monitor-component-evidence",
            "private-root-containment",
            requireAuthorization ? "immutable-parent-authorization" : "authorization-not-requested",
        };
        var summary = new MonitoredManifestValidation(
            MonitoredProtocolVersions.ManifestSchema,
            Ready: requireAuthorization,
            manifestHash,
            authorizationHash,
            manifest.Plan.ProtocolJsonSha256,
            campaignRoot,
            35,
            checks);
        return new MonitoredValidatedManifest(
            manifest,
            root,
            resolvedManifestPath,
            manifestHash,
            campaignRoot,
            attribution,
            encoding,
            component,
            authorization,
            authorizationHash,
            summary);
    }

    private static void ValidateFixtureManifest(string fixturePath, MonitoredRunManifest manifest)
    {
        using var fixture = JsonDocument.Parse(MonitoredFile.ReadBounded(fixturePath, 1_048_576));
        var root = fixture.RootElement;
        RequireHash(
            manifest.Q1InputSha256,
            root.GetProperty("q1InputSha256").GetString() ?? string.Empty,
            "Q1InputHashMismatch");
        RequireHash(
            manifest.Q1OracleSha256,
            root.GetProperty("q1OracleSha256").GetString() ?? string.Empty,
            "Q1OracleHashMismatch");
        RequireHash(
            manifest.Q2InputSha256,
            root.GetProperty("q2InputSha256").GetString() ?? string.Empty,
            "Q2InputHashMismatch");
        RequireHash(
            manifest.Q2OracleSha256,
            root.GetProperty("q2OracleSha256").GetString() ?? string.Empty,
            "Q2OracleHashMismatch");
        if (root.GetProperty("protocolRevision").GetInt32() != 3
            || !string.Equals(
                root.GetProperty("protocolJsonSha256").GetString(),
                MonitoredProtocolVersions.BaselineProtocolSha256,
                StringComparison.Ordinal))
        {
            throw Error(
                "FixtureBaselineMismatch",
                "The shared deterministic fixture must remain pinned to frozen revision 3.");
        }
    }

    private static void ValidateCommits(MonitoredSourceCommits commits)
    {
        foreach (var (name, value) in new[]
        {
            (nameof(commits.ProtocolCommit), commits.ProtocolCommit),
            (nameof(commits.PipelineCommit), commits.PipelineCommit),
            (nameof(commits.AdapterCommit), commits.AdapterCommit),
            (nameof(commits.RunnerCommit), commits.RunnerCommit),
            (nameof(commits.MonitorCommit), commits.MonitorCommit),
        })
        {
            RequireCommit(value, name);
        }
    }

    private static void ValidateBinary(MonitoredBinaryIdentity identity, string name)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var path = MonitoredPathRules.ResolveExistingFile(identity.Path);
        RequireHash(identity.Sha256, MonitoredFile.HashFile(path), $"{name}HashMismatch");
    }

    internal static MonitoredHostFacts ValidateCurrentHost(
        MonitoredHostFacts declared,
        string evidenceRoot)
    {
        ValidateDeclaredHostFacts(declared);
        var actual = MonitoredHostFactsReader.Read(evidenceRoot);
        if (!StableHostFactsMatch(declared, actual))
        {
            throw Error(
                "HostFactsMismatch",
                "Resolved host/kernel/CPU/cgroup/filesystem facts do not match the current execution host.");
        }
        if (declared.EffectiveMemoryBytes < 2_147_483_648
            || actual.EffectiveMemoryBytes < 2_147_483_648)
        {
            throw Error(
                "EffectiveMemoryPreflightFailed",
                "Effective available memory is below the frozen 2 GiB preflight minimum.");
        }
        if (declared.ArtifactAvailableFreeBytes < 8_589_934_592
            || actual.ArtifactAvailableFreeBytes < 8_589_934_592)
        {
            throw Error(
                "ArtifactRootFreeSpacePreflightFailed",
                "The artifact root has less than the frozen 8 GiB free-space minimum.");
        }
        return actual;
    }

    private static void ValidateDeclaredHostFacts(MonitoredHostFacts declared)
    {
        if (declared.HostMemoryTotalBytes <= 0
            || declared.HostMemoryAvailableBytes < 0
            || declared.HostMemoryAvailableBytes > declared.HostMemoryTotalBytes
            || declared.ArtifactAvailableFreeBytes < 0
            || declared.Cgroup.MembershipAndAncestors.Count > 64)
        {
            throw Error(
                "HostFactsInvalid",
                "Resolved host capacity or cgroup geometry is invalid.");
        }
        var cgroupAvailable = MonitoredHostFactsReader.CalculateEffectiveCgroupAvailable(
            declared.Cgroup.MembershipAndAncestors);
        if (declared.Cgroup.EffectiveAvailableMemoryBytes != cgroupAvailable
            || declared.EffectiveMemoryBytes != Math.Min(
                declared.HostMemoryAvailableBytes,
                cgroupAvailable ?? long.MaxValue))
        {
            throw Error(
                "HostFactsInvalid",
                "Resolved effective-memory facts are inconsistent with the recorded host and cgroup observations.");
        }
    }

    private static void ValidateHost(MonitoredHostFacts declared, string evidenceRoot)
        => ValidateCurrentHost(declared, evidenceRoot);

    private static bool StableHostFactsMatch(
        MonitoredHostFacts declared,
        MonitoredHostFacts actual)
    {
        if (!string.Equals(declared.OperatingSystem, actual.OperatingSystem, StringComparison.Ordinal)
            || !string.Equals(declared.KernelRelease, actual.KernelRelease, StringComparison.Ordinal)
            || !string.Equals(declared.CpuModel, actual.CpuModel, StringComparison.Ordinal)
            || declared.LogicalCpuCount != actual.LogicalCpuCount
            || declared.HostMemoryTotalBytes != actual.HostMemoryTotalBytes
            || !string.Equals(declared.FileSystem, actual.FileSystem, StringComparison.Ordinal)
            || !string.Equals(declared.ArtifactDevice, actual.ArtifactDevice, StringComparison.Ordinal)
            || !string.Equals(
                declared.ArtifactMountSource,
                actual.ArtifactMountSource,
                StringComparison.Ordinal)
            || !string.Equals(declared.ArtifactMountPoint, actual.ArtifactMountPoint, StringComparison.Ordinal))
        {
            return false;
        }
        var declaredCgroup = declared.Cgroup;
        var actualCgroup = actual.Cgroup;
        if (!string.Equals(declaredCgroup.Version, actualCgroup.Version, StringComparison.Ordinal)
            || !string.Equals(
                declaredCgroup.ProcSelfCgroup,
                actualCgroup.ProcSelfCgroup,
                StringComparison.Ordinal)
            || !string.Equals(
                declaredCgroup.MembershipPath,
                actualCgroup.MembershipPath,
                StringComparison.Ordinal)
            || !string.Equals(declaredCgroup.MountRoot, actualCgroup.MountRoot, StringComparison.Ordinal)
            || !string.Equals(declaredCgroup.MountPoint, actualCgroup.MountPoint, StringComparison.Ordinal)
            || declaredCgroup.MembershipAndAncestors.Count
                != actualCgroup.MembershipAndAncestors.Count)
        {
            return false;
        }
        return declaredCgroup.MembershipAndAncestors
            .Zip(actualCgroup.MembershipAndAncestors)
            .All(static pair =>
                string.Equals(
                    pair.First.HierarchyPath,
                    pair.Second.HierarchyPath,
                    StringComparison.Ordinal)
                && string.Equals(
                    pair.First.FileSystemPath,
                    pair.Second.FileSystemPath,
                    StringComparison.Ordinal)
                && string.Equals(pair.First.MemoryMax, pair.Second.MemoryMax, StringComparison.Ordinal)
                && string.Equals(pair.First.CpuMax, pair.Second.CpuMax, StringComparison.Ordinal)
                && string.Equals(
                    pair.First.CpusetCpusEffective,
                    pair.Second.CpusetCpusEffective,
                    StringComparison.Ordinal));
    }

    private static void ValidateClock(MonitoredClockDefinition clock)
    {
        if (!string.Equals(clock.MonotonicClock, "System.Diagnostics.Stopwatch", StringComparison.Ordinal)
            || clock.StopwatchFrequency != System.Diagnostics.Stopwatch.Frequency
            || !string.Equals(clock.SourceTimestampApi, "TraceEvent.TimeStampRelativeMSec", StringComparison.Ordinal)
            || !string.Equals(
                clock.CheckedConversion,
                "checked(round(relativeMilliseconds*10000)) to 100ns ticks",
                StringComparison.Ordinal)
            || !string.Equals(clock.Rounding, "MidpointRounding.AwayFromZero", StringComparison.Ordinal))
        {
            throw Error(
                "ClockDefinitionMismatch",
                "The manifest must pin the implemented monotonic and EventPipe timestamp conversion.");
        }
    }

    private static void ValidateAttribution(
        MonitoredRunManifest manifest,
        MonitoredAttributionMap attribution)
    {
        if (!string.Equals(
                attribution.Schema,
                MonitoredProtocolVersions.AttributionSchema,
                StringComparison.Ordinal)
            || attribution.MaximumTrackedIdentitiesPerSweep != 4_096
            || attribution.MaximumObservedPathUtf8Bytes != 4_096)
        {
            throw Error("AttributionMapMismatch", "The attribution map does not use the adopted monitor caps.");
        }
        if (attribution.Roots.Count != 4)
        {
            throw Error(
                "AttributionMapMismatch",
                "The attribution map must contain exactly the four charged evidence, history, workspace, and output roots.");
        }
        if (!attribution.PackageDirectoryNames.SequenceEqual(
                PackageDirectoryNames,
                StringComparer.Ordinal)
            || !attribution.RecoveryDirectoryNames.SequenceEqual(
                RecoveryDirectoryNames,
                StringComparer.Ordinal))
        {
            throw Error(
                "AttributionMapMismatch",
                "Package and recovery directory classifications must use the fixed runner-owned names.");
        }

        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["history"] = Path.GetFullPath(manifest.HistoryRoot),
            ["workspace"] = Path.GetFullPath(manifest.WorkspaceRoot),
            ["outputs"] = Path.GetFullPath(manifest.OutputRoot),
            ["evidence"] = Path.GetFullPath(manifest.EvidenceRoot),
        };
        foreach (var pair in expected)
        {
            var matches = attribution.Roots.Where(root =>
                string.Equals(root.Role, pair.Key, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1
                || matches[0] is not { } match
                || !match.Charged
                || !MonitoredPathRules.PathsEqual(Path.GetFullPath(match.Path), pair.Value))
            {
                throw Error(
                    "AttributionMapMismatch",
                    $"The charged attribution root '{pair.Key}' does not match the manifest.");
            }
        }
        if (attribution.ReadOnlyDependencyRoots.Count == 0
            || attribution.ReadOnlyDependencyRoots.Count > 16
            || attribution.ReadOnlyDependencyRoots.Any(static path => !Path.IsPathRooted(path)))
        {
            throw Error(
                "AttributionMapMismatch",
                "Read-only dependency exclusions must be explicit absolute roots.");
        }
        foreach (var dependencyRoot in attribution.ReadOnlyDependencyRoots)
        {
            var resolved = MonitoredPathRules.ResolveAbsoluteDirectory(
                dependencyRoot,
                mustExist: true);
            if (MonitoredPathRules.IsContained(resolved, manifest.EvidenceRoot))
            {
                throw Error(
                    "AttributionMapMismatch",
                    "A read-only dependency exclusion cannot contain the private evidence root.");
            }
        }
        if (attribution.RuntimeOnlyDescriptorTargets.Any(static value => string.IsNullOrWhiteSpace(value)))
        {
            throw Error(
                "AttributionMapMismatch",
                "Runtime-only descriptor target exclusions cannot be blank.");
        }
        if (!attribution.RuntimeOnlyDescriptorTargets.SequenceEqual(
                RuntimeOnlyDescriptorTargets,
                StringComparer.Ordinal))
        {
            throw Error(
                "AttributionMapMismatch",
                "Runtime-only descriptor exclusions are fixed to /dev/null and /dev/urandom.");
        }
        if (attribution.MaximumDescriptorOnlyIdentitiesPerSweep
                != MonitoredRunnerGeometry.MaximumOwnedWritableDescriptorOnlyFiles
            || attribution.RuntimeOnlyDescriptorProofs.Count != 1)
        {
            throw Error(
                "AttributionMapMismatch",
                "The attribution map must pin the enforced descriptor-only cap and one .NET double-mapper proof.");
        }
        var runtimeProof = attribution.RuntimeOnlyDescriptorProofs[0];
        if (!string.Equals(runtimeProof.Schema, "dotnet-runtime-memory-proof/1", StringComparison.Ordinal)
            || !string.Equals(runtimeProof.ResourceKind, "dotnet-doublemapper", StringComparison.Ordinal)
            || !string.Equals(runtimeProof.DescriptorTarget, "/memfd:doublemapper (deleted)", StringComparison.Ordinal)
            || !string.Equals(runtimeProof.BackingFileSystem, "tmpfs", StringComparison.Ordinal)
            || runtimeProof.BackingFileSystemMagic != LinuxRuntimeMemoryClassifier.TmpfsMagic
            || !runtimeProof.RequireDeletedDescriptor
            || !runtimeProof.RequireZeroLinks
            || !runtimeProof.RequireExecutableBackingMapping
            || !runtimeProof.RequireExecutableRuntimeMapping
            || string.IsNullOrWhiteSpace(runtimeProof.Rationale)
            || !string.Equals(
                Path.GetFileName(runtimeProof.RuntimeNativeBinary.Path),
                "libcoreclr.so",
                StringComparison.Ordinal)
            || !manifest.NativeBinaries.Any(native =>
                MonitoredPathRules.PathsEqual(
                    native.Path,
                    runtimeProof.RuntimeNativeBinary.Path)
                && string.Equals(
                    native.Sha256,
                    runtimeProof.RuntimeNativeBinary.Sha256,
                    StringComparison.Ordinal)))
        {
            throw Error(
                "AttributionMapMismatch",
                "The runtime-only descriptor proof must pin the actual libcoreclr binary and exact double-mapper filesystem/mapping requirements.");
        }
        ValidateBinary(runtimeProof.RuntimeNativeBinary, "RuntimeNativeBinary");
    }

    private static void ValidateEncoding(MonitoredEvidenceEncoding encoding)
    {
        if (!string.Equals(
                encoding.Schema,
                MonitoredProtocolVersions.EvidenceEncodingSchema,
                StringComparison.Ordinal)
            || !string.Equals(encoding.Format, "json-lines", StringComparison.Ordinal)
            || !string.Equals(encoding.TextEncoding, "UTF-8 without BOM", StringComparison.Ordinal)
            || !string.Equals(
                encoding.SummarySchema,
                MonitoredSweepSummaryEncoding.Schema,
                StringComparison.Ordinal)
            || !string.Equals(
                encoding.SummaryFieldMapSha256,
                MonitoredSweepSummaryEncoding.FieldMapSha256,
                StringComparison.Ordinal)
            || encoding.MaximumSummaryUtf8Bytes != 1_024
            || encoding.MaximumSummaryRecordsPerExecution != 2_048
            || encoding.MaximumStdoutUtf8Bytes != 8_388_608
            || encoding.MaximumStderrUtf8Bytes != 8_388_608
            || encoding.FullPathListsRepeatedPerSweep
            || encoding.NonAtomicSweepsClaimInstantaneousPeaks)
        {
            throw Error(
                "EvidenceEncodingMismatch",
                "Evidence encoding must use the adopted bounded JSON-lines geometry.");
        }
    }

    private static void ValidateComponentEvidence(
        MonitoredRunManifest manifest,
        MonitoredEvidenceEncoding encoding,
        MonitoredComponentEvidence evidence)
    {
        if (!string.Equals(
                evidence.Schema,
                MonitoredProtocolVersions.ComponentEvidenceSchema,
                StringComparison.Ordinal)
            || !string.Equals(evidence.Platform, "Linux", StringComparison.Ordinal)
            || !string.Equals(
                evidence.RunnerCommit,
                manifest.SourceCommits.RunnerCommit,
                StringComparison.Ordinal)
            || !string.Equals(
                evidence.MonitorCommit,
                manifest.SourceCommits.MonitorCommit,
                StringComparison.Ordinal)
            || !string.Equals(
                evidence.AttributionMapSha256,
                manifest.AttributionMapSha256,
                StringComparison.Ordinal)
            || !string.Equals(
                evidence.SummarySchema,
                encoding.SummarySchema,
                StringComparison.Ordinal)
            || !string.Equals(
                evidence.SummaryFieldMapSha256,
                encoding.SummaryFieldMapSha256,
                StringComparison.Ordinal)
            || evidence.DerivedMaximumSimultaneousIdentitiesEnforced
                != MonitoredRunnerGeometry.MaximumSimultaneousIdentities
            || evidence.DerivedMaximumRootedIdentities
                != MonitoredRunnerGeometry.MaximumRootedIdentities
            || evidence.MaximumDescriptorOnlyIdentitiesEnforced
                != MonitoredRunnerGeometry.MaximumOwnedWritableDescriptorOnlyFiles
            || evidence.RepresentativeMaximumRootedIdentitiesObserved < 1
            || evidence.RepresentativeMaximumRootedIdentitiesObserved
                > evidence.DerivedMaximumRootedIdentities
            || evidence.RepresentativeMaximumDescriptorOnlyIdentitiesObserved < 0
            || evidence.RepresentativeMaximumDescriptorOnlyIdentitiesObserved
                > evidence.MaximumDescriptorOnlyIdentitiesEnforced
            || !evidence.DescriptorOnlyCampaignFeasibilityEstablished
            || evidence.MaximumSummaryUtf8BytesObserved is < 1 or > 1_024
            || evidence.WorstCaseSummaryUtf8Bytes
                < evidence.MaximumSummaryUtf8BytesObserved
            || evidence.WorstCaseSummaryUtf8Bytes > 1_024
            || !MonitoredFile.IsResolvedSha256(evidence.WorstCaseSummarySha256)
            || string.IsNullOrWhiteSpace(evidence.WorstCaseSummaryRelativePath)
            || evidence.MaximumWorkerControlUtf8BytesObserved is < 1 or > 1_024
            || evidence.DerivedMaximumSummaryRecordsRequired
                != MonitoredRunnerGeometry.MaximumRequiredSummaryRecords
            || evidence.MaximumSummaryRecordsEncoded != 2_048
            || evidence.DerivedMaximumCombinedSummaryControlBytes
                != checked(MonitoredRunnerGeometry.MaximumMonitorSummaryBytes
                    + MonitoredRunnerGeometry.MaximumWorkerControlRecordsPerExecution
                        * (long)evidence.MaximumWorkerControlUtf8BytesObserved)
            || evidence.DerivedMaximumCombinedSummaryControlBytes
                > encoding.MaximumStdoutUtf8Bytes
            || evidence.SourceRecoveryObservedSummaryRecords < 2
            || evidence.SourceRecoveryObservedSummaryRecords
                > evidence.DerivedMaximumSummaryRecordsRequired
            || evidence.SourceRecoveryObservedSummaryBytes < 1
            || evidence.SourceRecoveryObservedControlRecords < 2
            || evidence.SourceRecoveryObservedControlRecords
                > MonitoredRunnerGeometry.MaximumWorkerControlRecordsPerExecution
            || evidence.SourceRecoveryObservedControlBytes < 1
            || evidence.SourceRecoveryObservedSummaryBytes
                + evidence.SourceRecoveryObservedControlBytes
                > encoding.MaximumStdoutUtf8Bytes
            || !evidence.SourceRecoverySharedBudgetExhaustionObserved
            || !evidence.SourceRecoverySharedCancellationObserved
            || evidence.PeriodicSummariesObserved < 2
            || !double.IsFinite(evidence.MaximumPeriodicGapMillisecondsObserved)
            || evidence.MaximumPeriodicGapMillisecondsObserved is < 0 or > 1_000
            || evidence.MaximumObservedSweepBytes < 1
            || !evidence.PositiveMonitoringComplete
            || !evidence.ManagedProcessObserved
            || !evidence.RuntimeMemoryClassificationObserved
            || evidence.RuntimeMemoryExcludedBytes < 1
            || !evidence.RootFileObserved
            || !evidence.WritableDescriptorOutsideRootRejected
            || !evidence.OpenUnlinkedDescriptorRejected
            || !evidence.ProcessIdentityReuseRejected
            || !evidence.KillHandoffReleasedObserverReferences
            || !evidence.RepresentativeCandidatePackageInventoriesObserved
            || evidence.RepresentativeMaximumFinalPackageFilesObserved != 4
            || evidence.RepresentativeMaximumTransientPackageFilesObserved < 1
            || !evidence.RepresentativeSqliteWalShmObserved
            || !evidence.OutputLimitEnforced
            || !evidence.IdentityLimitEnforced
            || evidence.RawEvidenceFiles is < 1 or > 4_096
            || !MonitoredFile.IsResolvedSha256(evidence.EvidenceSha256))
        {
            throw Error(
                "MonitorComponentEvidenceInsufficient",
                "Monitor component evidence does not establish the required identity, encoding, FD, kill-handoff, and cap behavior.");
        }
        var rawRoot = MonitoredPathRules.ResolveAbsoluteDirectory(
            evidence.RawEvidenceRoot,
            mustExist: true);
        var rawInventory = MonitoredFile.HashTreeInventory(
            rawRoot,
            maximumEntries: 4_096,
            maximumPathUtf8Bytes: 4_096);
        if (!MonitoredFile.IsReadOnlyTree(rawRoot, 4_096, 4_096)
            || rawInventory.FileCount != evidence.RawEvidenceFiles
            || !string.Equals(
                rawInventory.Sha256,
                evidence.EvidenceSha256,
                StringComparison.Ordinal))
        {
            throw Error(
                "MonitorComponentEvidenceMismatch",
                "The component evidence does not match its preserved raw artifact inventory.");
        }
        var worstCasePath = MonitoredPathRules.ResolveContainedExistingFile(
            rawRoot,
            evidence.WorstCaseSummaryRelativePath);
        var worstCaseBytes = MonitoredFile.ReadBounded(worstCasePath, 1_024);
        if (worstCaseBytes.Length != evidence.WorstCaseSummaryUtf8Bytes
            || !string.Equals(
                MonitoredFile.HashBytes(worstCaseBytes),
                evidence.WorstCaseSummarySha256,
                StringComparison.Ordinal))
        {
            throw Error(
                "MonitorSummaryEncodingEvidenceMismatch",
                "The preserved worst-case real monitor summary did not match its declared length and hash.");
        }
    }

    private static MonitoredAuthorizationReceipt ValidateAuthorization(
        MonitoredRunManifest manifest,
        string manifestHash)
    {
        var receiptPath = MonitoredPathRules.ResolveExistingFile(manifest.AuthorizationReceipt);
        if (!OperatingSystem.IsLinux())
        {
            throw Error("UnsupportedPlatform", "Monitored authorization validation is Linux-only.");
        }
        var writableBits = UnixFileMode.UserWrite
            | UnixFileMode.GroupWrite
            | UnixFileMode.OtherWrite;
        if ((File.GetUnixFileMode(receiptPath) & writableBits) != 0)
        {
            throw Error(
                "ExecutionAuthorizationMutable",
                "The parent authorization receipt must be made read-only before validation.");
        }
        using (var handle = File.OpenHandle(
                   receiptPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            var native = LinuxStatxHandleMetadataObserver.Instance.Observe(handle);
            if (native.LinkCount != 1)
            {
                throw Error(
                    "ExecutionAuthorizationMutable",
                    "The parent authorization receipt must be a single-link regular file.");
            }
        }
        var receipt = DeserializeBounded<MonitoredAuthorizationReceipt>(receiptPath);
        if (!string.Equals(
                receipt.Schema,
                MonitoredProtocolVersions.AuthorizationSchema,
                StringComparison.Ordinal)
            || !string.Equals(receipt.CampaignId, manifest.CampaignId, StringComparison.Ordinal)
            || !string.Equals(receipt.ManifestSha256, manifestHash, StringComparison.Ordinal)
            || !string.Equals(
                receipt.ProtocolJsonSha256,
                manifest.Plan.ProtocolJsonSha256,
                StringComparison.Ordinal)
            || !string.Equals(
                receipt.RunnerCommit,
                manifest.SourceCommits.RunnerCommit,
                StringComparison.Ordinal)
            || !string.Equals(
                receipt.MonitorCommit,
                manifest.SourceCommits.MonitorCommit,
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(receipt.IndependentReviewer)
            || receipt.AuthorizedAt == default
            || !receipt.ExecutionConditionallyAuthorized
            || !receipt.RunnerReadinessApproved
            || !receipt.SingleCampaignOnly)
        {
            throw Error(
                "ExecutionAuthorizationInvalid",
                "A reviewed immutable authorization receipt matching this exact manifest and runner is required.");
        }
        return receipt;
    }

    private static void ValidateOwnedRoot(string path, string campaignRoot, string expectedLeaf)
    {
        var resolved = Path.GetFullPath(path);
        if (!MonitoredPathRules.IsContained(campaignRoot, resolved)
            || !string.Equals(Path.GetFileName(resolved), expectedLeaf, StringComparison.Ordinal))
        {
            throw Error(
                "OwnedRootContainmentFailed",
                $"The '{expectedLeaf}' root must be the declared leaf below the private campaign root.");
        }
    }

    private static T DeserializeBounded<T>(string path)
        => JsonSerializer.Deserialize<T>(MonitoredFile.ReadBounded(path, 1_048_576), JsonOptions)
            ?? throw Error("InvalidReadinessArtifact", $"Readiness artifact '{path}' was empty.");

    private static void RequireSafeIdentity(string value, string name, bool allowSlash)
    {
        if (string.IsNullOrWhiteSpace(value)
            || Encoding.UTF8.GetByteCount(value) > 128
            || value.Contains("..", StringComparison.Ordinal)
            || (!allowSlash && (value.Contains('/') || value.Contains('\\')))
            || value.Any(char.IsControl))
        {
            throw Error("InvalidIdentity", $"Manifest identity '{name}' is invalid.");
        }
    }

    private static void RequireCommit(string value, string name)
    {
        if (value.Length is < 40 or > 64
            || !value.All(Uri.IsHexDigit)
            || value.Distinct().Count() < 2)
        {
            throw Error("InvalidSourceCommit", $"Source commit '{name}' must be a resolved hexadecimal identity.");
        }
    }

    private static void RequireHash(string declared, string actual, string code)
    {
        if (!MonitoredFile.IsSha256(declared)
            || !string.Equals(declared, actual, StringComparison.Ordinal))
        {
            throw Error(code, "A resolved SHA-256 identity does not match its referenced artifact.");
        }
    }

    private static DurableStorageExperimentException Error(string code, string message) => new(code, message);
}

internal static class MonitoredHostFactsReader
{
    private const int MaximumKernelFactsBytes = 1_048_576;
    private const int MaximumCgroupAncestors = 64;

    internal static MonitoredHostFacts Read(string artifactRoot)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Monitored host facts are implemented only for Linux.");
        }
        var root = MonitoredPathRules.ResolveAbsoluteDirectory(artifactRoot, mustExist: true);
        var mounts = ReadMountInfo("/proc/self/mountinfo");
        var artifactMount = FindContainingMount(root, mounts)
            ?? throw Error(
                "ArtifactMountUnavailable",
                "The filesystem mount containing the artifact root could not be resolved.");
        var drive = new DriveInfo(artifactMount.MountPoint);
        var memory = ReadHostMemory();
        var cgroup = ReadCgroupFacts(
            "/proc/self/cgroup",
            mounts);
        var effectiveMemory = Math.Min(
            memory.AvailableBytes,
            cgroup.EffectiveAvailableMemoryBytes ?? long.MaxValue);
        return new MonitoredHostFacts(
            DateTimeOffset.UtcNow,
            "Linux",
            ReadFirstLine("/proc/sys/kernel/osrelease"),
            ReadCpuModel(),
            Environment.ProcessorCount,
            memory.TotalBytes,
            memory.AvailableBytes,
            cgroup,
            effectiveMemory,
            artifactMount.FileSystem,
            artifactMount.Device,
            artifactMount.Source,
            artifactMount.MountPoint,
            drive.AvailableFreeSpace);
    }

    internal static long ReadEffectiveAvailableMemory()
    {
        var memory = ReadHostMemory();
        var cgroup = ReadCgroupFacts(
            "/proc/self/cgroup",
            ReadMountInfo("/proc/self/mountinfo"));
        return Math.Min(
            memory.AvailableBytes,
            cgroup.EffectiveAvailableMemoryBytes ?? long.MaxValue);
    }

    internal static MonitoredCgroupFacts ReadCgroupFacts(
        string procSelfCgroupPath,
        string procSelfMountInfoPath)
        => ReadCgroupFacts(
            procSelfCgroupPath,
            ReadMountInfo(procSelfMountInfoPath));

    private static (long TotalBytes, long AvailableBytes) ReadHostMemory()
    {
        long? total = null;
        long? available = null;
        foreach (var line in ReadBoundedText("/proc/meminfo", MaximumKernelFactsBytes)
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
            {
                total = ParseMemInfoBytes(line, "MemTotal");
            }
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
            {
                available = ParseMemInfoBytes(line, "MemAvailable");
            }
        }
        if (total is not null && available is not null)
        {
            return (total.Value, available.Value);
        }
        throw new DurableStorageExperimentException(
            "HostMemoryFactsUnavailable",
            "Linux MemTotal and MemAvailable could not both be resolved.");
    }

    private static long ParseMemInfoBytes(string line, string name)
    {
        var pieces = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (pieces.Length < 2
            || !long.TryParse(
                pieces[1],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var kibibytes)
            || kibibytes < 0)
        {
            throw Error(
                "HostMemoryFactsUnavailable",
                $"Linux {name} was not a non-negative KiB value.");
        }
        return checked(kibibytes * 1_024);
    }

    private static MonitoredCgroupFacts ReadCgroupFacts(
        string procSelfCgroupPath,
        IReadOnlyList<MountInfoEntry> mounts)
    {
        var cgroupLines = ReadBoundedText(procSelfCgroupPath, 16_384)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var procSelfCgroup = cgroupLines.Length == 0
            ? "none"
            : string.Join('|', cgroupLines.Order(StringComparer.Ordinal));
        var unifiedLines = cgroupLines.Where(static line =>
            line.StartsWith("0::", StringComparison.Ordinal)).ToArray();
        if (unifiedLines.Length == 0)
        {
            return new MonitoredCgroupFacts(
                "none",
                procSelfCgroup,
                MembershipPath: null,
                MountRoot: null,
                MountPoint: null,
                MembershipAndAncestors: [],
                EffectiveAvailableMemoryBytes: null);
        }
        if (unifiedLines.Length != 1)
        {
            throw Error(
                "CgroupMembershipAmbiguous",
                "Exactly one unified cgroup-v2 membership is required.");
        }

        var unified = unifiedLines[0];
        var membership = NormalizeCgroupPath(unified[3..]);
        var mount = mounts
            .Where(static item => string.Equals(item.FileSystem, "cgroup2", StringComparison.Ordinal))
            .Where(item => TryGetCgroupRelativePath(membership, item.Root, out _))
            .OrderByDescending(static item => item.Root.Length)
            .FirstOrDefault()
            ?? throw Error(
                "CgroupV2MountUnavailable",
                "The unified cgroup membership could not be mapped through /proc/self/mountinfo.");
        _ = TryGetCgroupRelativePath(membership, mount.Root, out var relative);
        var membershipDirectory = string.IsNullOrEmpty(relative)
            ? mount.MountPoint
            : Path.GetFullPath(relative, mount.MountPoint);
        if (!MonitoredPathRules.IsContained(mount.MountPoint, membershipDirectory)
            || !Directory.Exists(membershipDirectory))
        {
            throw Error(
                "CgroupMembershipUnavailable",
                "The resolved unified cgroup membership directory is unavailable.");
        }

        var levels = new List<MonitoredCgroupLevelFacts>();
        var currentDirectory = membershipDirectory;
        for (var depth = 0; depth < MaximumCgroupAncestors; depth++)
        {
            var hierarchyPath = ToHierarchyPath(currentDirectory, mount);
            var memoryMax = ReadOptionalFirstLine(Path.Combine(currentDirectory, "memory.max"));
            var memoryCurrent = ParseOptionalNonNegativeInt64(
                ReadOptionalFirstLine(Path.Combine(currentDirectory, "memory.current")),
                "memory.current");
            var cpuMax = ReadOptionalFirstLine(Path.Combine(currentDirectory, "cpu.max"));
            var cpuset = ReadOptionalFirstLine(Path.Combine(currentDirectory, "cpuset.cpus.effective"));
            levels.Add(new MonitoredCgroupLevelFacts(
                hierarchyPath,
                currentDirectory,
                memoryMax,
                memoryCurrent,
                cpuMax,
                cpuset));

            if (MonitoredPathRules.PathsEqual(currentDirectory, mount.MountPoint))
            {
                return new MonitoredCgroupFacts(
                    "v2",
                    procSelfCgroup,
                    membership,
                    mount.Root,
                    mount.MountPoint,
                    levels,
                    CalculateEffectiveCgroupAvailable(levels));
            }
            currentDirectory = Directory.GetParent(currentDirectory)?.FullName
                ?? throw Error(
                    "CgroupAncestorResolutionFailed",
                    "The cgroup membership ancestry ended before its mount point.");
        }
        throw Error(
            "CgroupAncestorLimitExceeded",
            $"The cgroup hierarchy exceeded the {MaximumCgroupAncestors}-level safety limit.");
    }

    internal static long? CalculateEffectiveCgroupAvailable(
        IReadOnlyList<MonitoredCgroupLevelFacts> levels)
    {
        long? effectiveAvailable = null;
        foreach (var level in levels)
        {
            if (level.MemoryMax is null
                || string.Equals(level.MemoryMax, "max", StringComparison.Ordinal))
            {
                continue;
            }
            var limit = ParseNonNegativeInt64(level.MemoryMax, "memory.max");
            if (level.MemoryCurrentBytes is null)
            {
                throw Error(
                    "CgroupMemoryFactsUnavailable",
                    $"A finite memory.max at '{level.HierarchyPath}' had no readable memory.current.");
            }
            var available = Math.Max(0, limit - level.MemoryCurrentBytes.Value);
            effectiveAvailable = effectiveAvailable is null
                ? available
                : Math.Min(effectiveAvailable.Value, available);
        }
        return effectiveAvailable;
    }

    private static string ReadCpuModel()
    {
        foreach (var line in ReadBoundedText("/proc/cpuinfo", MaximumKernelFactsBytes)
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("model name", StringComparison.Ordinal))
            {
                continue;
            }
            var separator = line.IndexOf(':');
            if (separator >= 0)
            {
                return line[(separator + 1)..].Trim();
            }
        }
        return "unavailable";
    }

    private static List<MountInfoEntry> ReadMountInfo(string path)
    {
        var entries = new List<MountInfoEntry>();
        foreach (var line in ReadBoundedText(path, MaximumKernelFactsBytes)
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(" - ", StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }
            var left = line[..separator].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var right = line[(separator + 3)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (left.Length < 6 || right.Length < 2)
            {
                continue;
            }
            entries.Add(new MountInfoEntry(
                DecodeMountInfoField(left[3]),
                Path.GetFullPath(DecodeMountInfoField(left[4])),
                right[0],
                DecodeMountInfoField(right[1]),
                left[2]));
        }
        return entries;
    }

    private static MountInfoEntry? FindContainingMount(
        string path,
        IReadOnlyList<MountInfoEntry> mounts)
        => mounts
            .Where(mount => MonitoredPathRules.IsContained(mount.MountPoint, path))
            .OrderByDescending(static mount => mount.MountPoint.Length)
            .FirstOrDefault();

    private static bool TryGetCgroupRelativePath(
        string membership,
        string mountRoot,
        out string relative)
    {
        membership = NormalizeCgroupPath(membership);
        mountRoot = NormalizeCgroupPath(mountRoot);
        if (string.Equals(mountRoot, "/", StringComparison.Ordinal))
        {
            relative = membership.TrimStart('/');
            return true;
        }
        if (string.Equals(membership, mountRoot, StringComparison.Ordinal))
        {
            relative = string.Empty;
            return true;
        }
        if (membership.StartsWith(mountRoot + "/", StringComparison.Ordinal))
        {
            relative = membership[(mountRoot.Length + 1)..];
            return true;
        }
        relative = string.Empty;
        return false;
    }

    private static string ToHierarchyPath(string directory, MountInfoEntry mount)
    {
        var relative = Path.GetRelativePath(mount.MountPoint, directory)
            .Replace(Path.DirectorySeparatorChar, '/');
        if (string.Equals(relative, ".", StringComparison.Ordinal))
        {
            return mount.Root;
        }
        return string.Equals(mount.Root, "/", StringComparison.Ordinal)
            ? $"/{relative}"
            : $"{mount.Root.TrimEnd('/')}/{relative}";
    }

    private static string NormalizeCgroupPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path[0] != '/')
        {
            throw Error("CgroupMembershipInvalid", "A cgroup hierarchy path must be absolute.");
        }
        var normalized = path.Length > 1 ? path.TrimEnd('/') : path;
        if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(static segment => segment is "." or ".."))
        {
            throw Error("CgroupMembershipInvalid", "A cgroup hierarchy path cannot contain traversal segments.");
        }
        return normalized;
    }

    private static long? ParseOptionalNonNegativeInt64(string? value, string field)
        => value is null ? null : ParseNonNegativeInt64(value, field);

    private static long ParseNonNegativeInt64(string value, string field)
    {
        if (!long.TryParse(
                value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed)
            || parsed < 0)
        {
            throw Error(
                "CgroupControllerValueInvalid",
                $"{field} was not a non-negative 64-bit integer.");
        }
        return parsed;
    }

    private static string? ReadOptionalFirstLine(string path)
        => File.Exists(path) ? ReadFirstLine(path) : null;

    private static string ReadFirstLine(string path)
    {
        var text = ReadBoundedText(path, 4_096);
        var newline = text.IndexOf('\n');
        return (newline >= 0 ? text[..newline] : text).Trim();
    }

    private static string ReadBoundedText(string path, int maximumBytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var output = new MemoryStream(capacity: Math.Min(maximumBytes, 16_384));
        var buffer = new byte[4_096];
        while (true)
        {
            var read = stream.Read(buffer);
            if (read == 0)
            {
                return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
            }
            if (read > maximumBytes - output.Length)
            {
                throw Error(
                    "HostFactsTooLarge",
                    $"Host facts file '{path}' exceeded the {maximumBytes}-byte limit.");
            }
            output.Write(buffer, 0, read);
        }
    }

    private static string DecodeMountInfoField(string value)
        => value.Replace("\\040", " ", StringComparison.Ordinal)
            .Replace("\\011", "\t", StringComparison.Ordinal)
            .Replace("\\012", "\n", StringComparison.Ordinal)
            .Replace("\\134", "\\", StringComparison.Ordinal);

    private static DurableStorageExperimentException Error(string code, string message)
        => new(code, message);

    private sealed record MountInfoEntry(
        string Root,
        string MountPoint,
        string FileSystem,
        string Source,
        string Device);
}

internal static class MonitoredPathRules
{
    internal static IReadOnlyList<string> EnumerateFilesRejectingLinks(
        string root,
        int maximumEntries,
        int maximumPathUtf8Bytes)
    {
        var resolvedRoot = ResolveAbsoluteDirectory(root, mustExist: true);
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(resolvedRoot);
        var entries = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                entries++;
                if (entries > maximumEntries)
                {
                    throw Error(
                        "TrackedPathLimitExceeded",
                        $"Tree enumeration exceeded the {maximumEntries}-entry bound.");
                }
                if (Encoding.UTF8.GetByteCount(path) > maximumPathUtf8Bytes)
                {
                    throw Error(
                        "ObservedPathLimitExceeded",
                        $"Tree entry exceeded the {maximumPathUtf8Bytes}-byte UTF-8 path bound.");
                }
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw Error("LinkRejected", $"Tree entry '{path}' is a symbolic link.");
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(path);
                }
                else
                {
                    files.Add(path);
                }
            }
        }
        return files;
    }

    internal static string ResolveRepositoryFile(string repositoryRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || Path.IsPathRooted(path)
            || path.Split('/', '\\').Any(static part => part is "" or "." or ".."))
        {
            throw Error(
                "InvalidRepositoryPath",
                "Repository artifacts must use normalized repository-relative paths.");
        }
        var root = Path.GetFullPath(repositoryRoot);
        var resolved = Path.GetFullPath(Path.Combine(root, path));
        if (!IsContained(root, resolved))
        {
            throw Error("InvalidRepositoryPath", "Repository artifact path escapes the repository.");
        }
        RejectLinks(root, resolved);
        if (!File.Exists(resolved))
        {
            throw Error("MissingFile", $"Required repository file '{resolved}' does not exist.");
        }
        return resolved;
    }

    internal static string ResolveExistingFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            throw Error("InvalidAbsolutePath", "Resolved runtime artifact paths must be absolute.");
        }
        var resolved = Path.GetFullPath(path);
        RejectLinks(Path.GetPathRoot(resolved)!, resolved);
        if (!File.Exists(resolved))
        {
            throw Error("MissingFile", $"Required file '{resolved}' does not exist.");
        }
        return resolved;
    }

    internal static string ResolveContainedExistingFile(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || relativePath.Split('/', '\\').Any(static part => part is "" or "." or ".."))
        {
            throw Error(
                "InvalidContainedPath",
                "Contained evidence paths must use normalized relative paths.");
        }
        var resolvedRoot = Path.GetFullPath(root);
        var resolved = Path.GetFullPath(Path.Combine(resolvedRoot, relativePath));
        if (!IsContained(resolvedRoot, resolved))
        {
            throw Error("InvalidContainedPath", "Contained evidence path escapes its root.");
        }
        RejectLinks(resolvedRoot, resolved);
        if (!File.Exists(resolved))
        {
            throw Error("MissingFile", $"Required contained file '{resolved}' does not exist.");
        }
        return resolved;
    }

    internal static string ResolveAbsoluteDirectory(string path, bool mustExist)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            throw Error("InvalidAbsolutePath", "Private evidence roots must be absolute.");
        }
        var resolved = Path.GetFullPath(path);
        if (mustExist && !Directory.Exists(resolved))
        {
            throw Error("MissingDirectory", $"Required directory '{resolved}' does not exist.");
        }
        RejectLinks(Path.GetPathRoot(resolved)!, resolved);
        return resolved;
    }

    internal static string CombineContained(string root, string leaf)
    {
        var combined = Path.GetFullPath(Path.Combine(root, leaf));
        if (!IsContained(root, combined))
        {
            throw Error("PathContainmentFailed", "A private evidence path escaped its declared root.");
        }
        return combined;
    }

    internal static bool IsContained(string root, string path)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedPath = Path.GetFullPath(path);
        var volumeRoot = Path.GetPathRoot(normalizedRoot);
        if (volumeRoot is not null && PathsEqual(normalizedRoot, volumeRoot))
        {
            return normalizedPath.StartsWith(
                volumeRoot,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }
        return PathsEqual(normalizedRoot, normalizedPath)
            || normalizedPath.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
    }

    internal static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    internal static void RejectLinks(string root, string path)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedPath = Path.GetFullPath(path);
        if (!IsContained(normalizedRoot, normalizedPath))
        {
            throw Error("PathContainmentFailed", "Path escapes its declared root.");
        }
        var relative = Path.GetRelativePath(normalizedRoot, normalizedPath);
        var current = normalizedRoot;
        if (new DirectoryInfo(current).LinkTarget is not null)
        {
            throw Error("LinkRejected", $"Path root '{current}' is a symbolic link.");
        }
        foreach (var part in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if ((Directory.Exists(current) && new DirectoryInfo(current).LinkTarget is not null)
                || (File.Exists(current) && new FileInfo(current).LinkTarget is not null))
            {
                throw Error("LinkRejected", $"Path component '{current}' is a symbolic link.");
            }
        }
    }

    private static DurableStorageExperimentException Error(string code, string message) => new(code, message);
}

internal static class MonitoredFile
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    internal static byte[] ReadBounded(string path, int maximumBytes)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new DurableStorageExperimentException("MissingFile", $"Required file '{path}' does not exist.");
        }
        if (info.Length > maximumBytes)
        {
            throw new DurableStorageExperimentException(
                "InputLimitExceeded",
                $"File '{path}' exceeds the {maximumBytes}-byte input limit.");
        }
        return File.ReadAllBytes(path);
    }

    internal static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    internal static string HashBytes(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static bool IsSha256(string value)
        => value.Length == 64 && value.All(Uri.IsHexDigit);

    internal static bool IsResolvedSha256(string value)
        => IsSha256(value) && value.Distinct().Count() >= 2;

    internal static (int FileCount, string Sha256) HashTreeInventory(
        string root,
        int maximumEntries,
        int maximumPathUtf8Bytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = MonitoredPathRules.EnumerateFilesRejectingLinks(
            root,
            maximumEntries,
            maximumPathUtf8Bytes);
        foreach (var path in files.Order(StringComparer.Ordinal))
        {
            var relativePath = Path.GetRelativePath(root, path)
                .Replace(Path.DirectorySeparatorChar, '/');
            var length = new FileInfo(path).Length;
            var fileHash = HashFile(path);
            hash.AppendData(Encoding.UTF8.GetBytes(relativePath));
            hash.AppendData([0]);
            hash.AppendData(Encoding.UTF8.GetBytes(length.ToString(
                System.Globalization.CultureInfo.InvariantCulture)));
            hash.AppendData([0]);
            hash.AppendData(Encoding.UTF8.GetBytes(fileHash));
            hash.AppendData("\n"u8);
        }
        return (
            files.Count,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    internal static bool IsReadOnlyTree(
        string root,
        int maximumEntries,
        int maximumPathUtf8Bytes)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Monitored artifacts are Linux-only.");
        }
        const UnixFileMode writable = UnixFileMode.UserWrite
            | UnixFileMode.GroupWrite
            | UnixFileMode.OtherWrite;
        if ((File.GetUnixFileMode(root) & writable) != 0)
        {
            return false;
        }
        foreach (var path in MonitoredPathRules.EnumerateFilesRejectingLinks(
                     root,
                     maximumEntries,
                     maximumPathUtf8Bytes))
        {
            if ((File.GetUnixFileMode(path) & writable) != 0)
            {
                return false;
            }
        }
        return true;
    }

    internal static void MakeReadOnly(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Monitored artifacts are Linux-only.");
        }
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    internal static void MakePrivateDirectory(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Monitored artifacts are Linux-only.");
        }
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    internal static void WriteNewJson<T>(string path, T value, bool flushToDisk = true)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4_096,
                FileOptions.WriteThrough);
            stream.Write(bytes);
            stream.Flush(flushToDisk);
        }
        catch (IOException) when (File.Exists(path))
        {
            throw new DurableStorageExperimentException(
                "ArtifactAlreadyExists",
                $"Refused to replace persistent artifact '{path}'.");
        }
    }
}
