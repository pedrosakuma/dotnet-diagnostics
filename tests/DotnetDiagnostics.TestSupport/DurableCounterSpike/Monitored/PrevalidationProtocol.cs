using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.Monitored;

internal sealed record PrevalidationProbe(
    int Ordinal, string Id, string Candidate, string Workload, string CaseClass, int FixtureSlots)
{
    internal MonitoredExecutionSpec Execution => new(Ordinal, Workload, Candidate, CaseClass, 120, 1);
}

internal sealed record PrevalidationBounds(
    int Attempts, int EntrySeconds, int AdmissionSeconds, int FinalizationSeconds, int SuiteSeconds,
    long SuiteBytes, long HistoryBytes, long PackageBytes, int HistorySlots,
    int RootedIdentities, int DescriptorOnlyIdentities, int ContextIdentities, int SuiteIdentities,
    int FixtureFilesPerSlot, int FixtureFileBytes, int SummaryRecords, int SummaryBytes,
    int StdoutBytes, int StderrBytes, int PollMilliseconds, int ActiveGapMilliseconds,
    int DrainSeconds, int PublishSeconds, int ReopenSeconds, long DiagnosticRssBytes,
    long HarnessRssBytes, long TargetRssBytes, long MinimumFreeBytes, long MinimumMemoryBytes);

internal sealed record PrevalidationManifest(
    string Schema, string Scope, string SuiteId, string PrivateRoot,
    string AddendumCommit, string AddendumSha256, string ProtocolSha256,
    IReadOnlyList<PrevalidationProbe> Probes, PrevalidationBounds Bounds,
    MonitoredSourceCommits SourceCommits,
    MonitoredBinaryIdentity RuntimeBinary, MonitoredBinaryIdentity ToolBinary,
    MonitoredBinaryIdentity SampleBinary, IReadOnlyList<MonitoredBinaryIdentity> ManagedBinaries,
    IReadOnlyList<MonitoredBinaryIdentity> NativeBinaries,
    MonitoredBinaryIdentity FixtureManifest, MonitoredHostFacts Host, MonitoredClockDefinition Clock,
    MonitoredBinaryIdentity Attribution, MonitoredBinaryIdentity Encoding,
    MonitoredBinaryIdentity ComponentEvidence, MonitoredBinaryIdentity Adoption,
    MonitoredBinaryIdentity ImplementationAcceptance, MonitoredBinaryIdentity HistoricalReport,
    string AuthorizationReceipt, string RuntimeEnvironmentSha256, string ContextSummaryFieldMapSha256);

internal sealed record PrevalidationAcceptance(
    string Schema, string Scope, string AddendumSha256, string Reviewer,
    MonitoredSourceCommits SourceCommits, string BinaryInventorySha256,
    string ComponentEvidenceSha256, string AttributionSha256, string EncodingSha256,
    int DerivedSuiteIdentities, bool CallPathsReviewed, bool TwoLevelAccountingReviewed,
    bool CleanupReviewed, bool NegativeAdmissionsReviewed);

internal sealed record PrevalidationAdoption(string Schema, string AddendumSha256, string Maintainer,
    DateTimeOffset AdoptedAt, string Scope);

internal sealed record PrevalidationAuthorization(
    string Schema, string Scope, string SuiteId, string ManifestSha256, string AddendumSha256,
    string AcceptanceSha256, string HistoricalReportSha256, string Authorizer,
    DateTimeOffset AuthorizedAt, int Entries, int Attempts, bool SingleSuiteOnly);

internal sealed record PrevalidationValidated(
    PrevalidationManifest Manifest, string RepositoryRoot, string ManifestPath, string ManifestSha256,
    string AuthorizationSha256, MonitoredAttributionMap Attribution,
    MonitoredEvidenceEncoding Encoding, MonitoredComponentEvidence ComponentEvidence);

internal sealed record PrevalidationExecutionSettings(
    MonitoredBinaryIdentity RuntimeBinary, MonitoredBinaryIdentity ToolBinary,
    MonitoredBinaryIdentity SampleBinary, MonitoredSourceCommits SourceCommits,
    string FixtureManifestSha256, string HistoryRoot, string WorkspaceRoot, string OutputRoot)
    : IMonitoredExecutionManifest;

internal static class PrevalidationProtocol
{
    internal const string Scope = "monitor-feasibility-only";
    internal const string PlanSchema = "durable-prevalidation-plan/1";
    internal const string ManifestSchema = "durable-prevalidation-manifest/1";
    internal const string WorkerSchema = "durable-prevalidation-worker/1";
    internal const string AddendumPath = "docs/design/durable-capture-real-worker-prevalidation.md";
    internal const string AddendumSha256 = "b825cb4a15b66d6b1ac4a245ffffb22e192f5bcb5625e65087b1cf0b51374bb5";
    internal const string AddendumCommit = "9a049681562e1c995a9fec43a632e6b69918d315";
    internal const string HistoricalReportPath = "docs/evidence/dc5/monitored-readiness-dbeb8cd.md";
    internal const string HistoricalReportSha256 = "18c22eddacdfa9dc2d3b2308264f42a139e7e311c8df57bc43e6cd0a129622bb";
    internal const string SummarySchema = "durable-prevalidation-sweep-summary/3";
    internal const string CoverageSchema = "durable-prevalidation-coverage/2";
    internal const string ReportSchema = "durable-prevalidation-report/2";
    internal static string LegacyContextSummaryFieldMapSha256 { get; } = MonitoredFile.HashBytes(
        System.Text.Encoding.UTF8.GetBytes(MonitoredSweepSummaryEncoding.FieldMap
            + "ci=CurrentContextIdentities\ncr=CurrentContextRootedIdentities\nrh=RetainedHistoryBytes\n"));
    private const string FailureContextFieldMap =
        "ci=CurrentContextIdentities\ncr=CurrentContextRootedIdentities\nrh=RetainedHistoryBytes\n"
        + "ec=FirstErrorCode(first sweep error;1-64 ASCII token;null when complete;remaining errors counted by er)\n";
    internal static string PreviousContextSummaryFieldMapSha256 { get; } = MonitoredFile.HashBytes(
        System.Text.Encoding.UTF8.GetBytes(MonitoredSweepSummaryEncoding.FieldMap
            + FailureContextFieldMap));
    internal static string ContextSummaryFieldMapSha256 { get; } = MonitoredFile.HashBytes(
        System.Text.Encoding.UTF8.GetBytes(MonitoredSweepSummaryEncoding.FieldMap
            + FailureContextFieldMap
            + "nc=FirstDescriptorFailure([role,operation,error,fd];role=0:harness,1:diagnostic,2:target;"
            + "operation=1:first-open,2:first-fdinfo,3:second-open,4:second-fdinfo;"
            + "open-error=errno[1,4095],fdinfo-error=Int32-HResult;fd=nonnegative-Int32;"
            + "first descriptor failure independently of ec;null when absent;no PID or path)\n"));
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        NumberHandling = JsonNumberHandling.Strict,
    };

    internal static IReadOnlyList<PrevalidationProbe> Plan() =>
    [
        new(1, "PV-O1-A", "A", "O1", "stress", 63),
        new(2, "PV-O1-B", "B", "O1", "stress", 63),
        new(3, "PV-LIVE-E", "E", "L1", "live", 64),
        new(4, "PV-LIVE-A", "A", "L1", "live", 63),
        new(5, "PV-LIVE-B", "B", "L1", "live", 63),
        new(6, "PV-F3-A", "A", "F3", "isolated-fault", 63),
        new(7, "PV-F3-B", "B", "F3", "isolated-fault", 63),
        new(8, "PV-GEOMETRY", "shared", "geometry", "inventory", 64),
    ];

    internal static PrevalidationBounds Bounds() => new(
        1, 120, 120, 120, 1_200, 3_221_225_472, 2_147_483_648, 268_435_456, 64,
        539, 32, 571, 4_096, 4, 512, 2_048, 1_024, 8_388_608, 8_388_608, 100, 1_000,
        5, 10, 2, 536_870_912, 268_435_456, 805_306_368, 8_589_934_592, 2_147_483_648);

    internal static T Read<T>(string path, int maximumBytes = 1_048_576)
    {
        var bytes = MonitoredFile.ReadBounded(MonitoredPathRules.ResolveExistingFile(path), maximumBytes);
        using var document = JsonDocument.Parse(bytes);
        RejectDuplicateMembers(document.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, Json)
            ?? throw Error("PrevalidationEmptyArtifact", "A required prevalidation artifact was empty.");
    }

    private static void RejectDuplicateMembers(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                Require(names.Add(property.Name), "DuplicateJsonMember");
                RejectDuplicateMembers(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateMembers(item);
            }
        }
    }

    internal static void ValidateShape(PrevalidationManifest manifest, bool allowLegacyInspection = false)
    {
        Require(manifest.Schema == ManifestSchema && manifest.Scope == Scope, "PrevalidationSchemaMismatch");
        Require(manifest.SuiteId.StartsWith("pv-", StringComparison.Ordinal)
            && !manifest.SuiteId.Contains("..", StringComparison.Ordinal)
            && MonitoredSweepSummaryEncoding.IsBoundedToken(manifest.SuiteId, 64), "PrevalidationNamespace");
        Require(manifest.AddendumCommit == AddendumCommit && manifest.AddendumSha256 == AddendumSha256
            && manifest.ProtocolSha256 == MonitoredProtocolVersions.SuccessorProtocolSha256,
            "PrevalidationFrozenInputMismatch");
        Require(manifest.Probes.SequenceEqual(Plan()) && manifest.Bounds == Bounds(), "PrevalidationPlanOrBounds");
        Require(manifest.ContextSummaryFieldMapSha256 == ContextSummaryFieldMapSha256
            || allowLegacyInspection && (manifest.ContextSummaryFieldMapSha256 == LegacyContextSummaryFieldMapSha256
                || manifest.ContextSummaryFieldMapSha256 == PreviousContextSummaryFieldMapSha256),
            "PrevalidationContextEncodingMismatch");
        Require(PrevalidationLayout.DeriveSuiteIdentityBound() <= Bounds().SuiteIdentities,
            "PrevalidationSuiteGeometry");
    }

    internal static PrevalidationValidated Validate(string repositoryRoot, string manifestPath)
    {
        var watch = Stopwatch.StartNew();
        if (!OperatingSystem.IsLinux())
        {
            throw Error("PrevalidationLinuxOnly", "The prevalidation executor requires Linux.");
        }
        EnsureImmutable(manifestPath);
        var manifest = Read<PrevalidationManifest>(manifestPath);
        ValidateShape(manifest);
        Require(manifest.RuntimeEnvironmentSha256 == RuntimeEnvironmentHash(),
            "PrevalidationRuntimeEnvironmentChanged");
        var repository = Path.GetFullPath(repositoryRoot);
        Require(MonitoredFile.HashFile(MonitoredPathRules.ResolveRepositoryFile(repository, AddendumPath))
            == AddendumSha256, "PrevalidationAddendumChanged");
        MonitoredSuccessorProtocolValidator.Validate(repository, MonitoredProtocolVersions.SuccessorProtocolPath);
        MonitoredRunManifestValidator.ValidateCommits(manifest.SourceCommits);
        MonitoredRunManifestValidator.ValidateClock(manifest.Clock);
        var root = MonitoredPathRules.ResolveAbsoluteDirectory(manifest.PrivateRoot, mustExist: true);
        Require(Path.GetFileName(root) == manifest.SuiteId, "PrevalidationPrivateRootName");
        Require(File.GetUnixFileMode(root) == (UnixFileMode.UserRead | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute), "PrevalidationPrivateRootMode");
        Require(manifest.ManagedBinaries.Count is > 0 and <= 256
            && manifest.NativeBinaries.Count is > 0 and <= 32, "PrevalidationBinaryInventory");
        foreach (var binary in Binaries(manifest))
        {
            MonitoredRunManifestValidator.ValidateBinary(binary, "PrevalidationBinary");
            Require(!MonitoredPathRules.IsContained(root, binary.Path), "PrevalidationBinaryInsideOutput");
        }
        Require(manifest.NativeBinaries.Any(static item => Path.GetFileName(item.Path) == "libe_sqlite3.so"),
            "PrevalidationNativeStorageIdentityMissing");
        Require(Environment.ProcessPath == manifest.RuntimeBinary.Path,
            "PrevalidationExecutingRuntimeIdentity");
        Require(DotnetDiagnostics.TestSupport.SampleLocator.LocateSampleDll() == manifest.SampleBinary.Path,
            "PrevalidationSampleResolutionMismatch");
        ValidateManagedInventory(manifest);
        VerifyArtifact(manifest.FixtureManifest);
        Require(manifest.FixtureManifest.Sha256 == MonitoredFile.HashFile(
            MonitoredPathRules.ResolveRepositoryFile(repository,
                "tests/DotnetDiagnostics.Core.Tests/DurableCounterSpike/durable-counter-fixture-manifest.json")),
            "PrevalidationFixtureMismatch");
        VerifyArtifact(manifest.Attribution);
        VerifyArtifact(manifest.Encoding);
        VerifyArtifact(manifest.ComponentEvidence);
        VerifyArtifact(manifest.Adoption);
        VerifyArtifact(manifest.ImplementationAcceptance);
        VerifyArtifact(manifest.HistoricalReport);
        Require(manifest.HistoricalReport.Sha256 == HistoricalReportSha256
            && manifest.HistoricalReport.Sha256 == MonitoredFile.HashFile(
            MonitoredPathRules.ResolveRepositoryFile(repository, HistoricalReportPath)),
            "PrevalidationHistoricalReportMismatch");
        var adoption = Read<PrevalidationAdoption>(manifest.Adoption.Path);
        Require(adoption.Schema == "durable-prevalidation-adoption/1"
            && adoption.AddendumSha256 == AddendumSha256 && adoption.Scope == Scope
            && !string.IsNullOrWhiteSpace(adoption.Maintainer) && adoption.AdoptedAt != default,
            "PrevalidationAdoptionMissing");
        var attribution = Read<MonitoredAttributionMap>(manifest.Attribution.Path);
        ValidateAttribution(manifest, attribution);
        var encoding = Read<MonitoredEvidenceEncoding>(manifest.Encoding.Path);
        MonitoredRunManifestValidator.ValidateEncoding(encoding);
        var component = Read<MonitoredComponentEvidence>(manifest.ComponentEvidence.Path);
        MonitoredRunManifestValidator.ValidateComponentEvidence(
            manifest.SourceCommits, manifest.Attribution.Sha256, encoding, component,
            MonitoredAdmissionStage.PrevalidationComponentProof);
        var acceptance = Read<PrevalidationAcceptance>(manifest.ImplementationAcceptance.Path);
        Require(acceptance.Schema == "durable-prevalidation-implementation-acceptance/1"
            && acceptance.Scope == Scope && acceptance.AddendumSha256 == AddendumSha256
            && acceptance.SourceCommits == manifest.SourceCommits
            && acceptance.BinaryInventorySha256 == BinaryInventoryHash(manifest)
            && acceptance.ComponentEvidenceSha256 == manifest.ComponentEvidence.Sha256
            && acceptance.AttributionSha256 == manifest.Attribution.Sha256
            && acceptance.EncodingSha256 == manifest.Encoding.Sha256
            && acceptance.DerivedSuiteIdentities == PrevalidationLayout.DeriveSuiteIdentityBound()
            && !string.IsNullOrWhiteSpace(acceptance.Reviewer)
            && acceptance.CallPathsReviewed && acceptance.TwoLevelAccountingReviewed
            && acceptance.CleanupReviewed && acceptance.NegativeAdmissionsReviewed,
            "PrevalidationImplementationNotAccepted");
        var hash = MonitoredFile.HashFile(manifestPath);
        EnsureImmutable(manifest.AuthorizationReceipt);
        var authorization = Read<PrevalidationAuthorization>(manifest.AuthorizationReceipt);
        ValidateAuthorization(manifest, hash, authorization);
        MonitoredRunManifestValidator.ValidateCurrentHost(manifest.Host, root);
        Require(watch.Elapsed < TimeSpan.FromSeconds(120), "PrevalidationAdmissionDeadline");
        return new(manifest, repository, Path.GetFullPath(manifestPath), hash,
            MonitoredFile.HashFile(manifest.AuthorizationReceipt), attribution, encoding, component);
    }

    internal static void ValidateAuthorization(PrevalidationManifest manifest, string hash,
        PrevalidationAuthorization authorization)
        => Require(authorization.Schema == "durable-prevalidation-authorization/1"
            && authorization.Scope == Scope && authorization.SuiteId == manifest.SuiteId
            && authorization.ManifestSha256 == hash && authorization.AddendumSha256 == AddendumSha256
            && authorization.AcceptanceSha256 == manifest.ImplementationAcceptance.Sha256
            && authorization.HistoricalReportSha256 == manifest.HistoricalReport.Sha256
            && authorization.Entries == 8 && authorization.Attempts == 1 && authorization.SingleSuiteOnly
            && !string.IsNullOrWhiteSpace(authorization.Authorizer) && authorization.AuthorizedAt != default,
            "PrevalidationAuthorizationRejected");

    private static void ValidateAttribution(PrevalidationManifest manifest, MonitoredAttributionMap map)
    {
        Require(map.Schema == MonitoredProtocolVersions.AttributionSchema
            && map.MaximumTrackedIdentitiesPerSweep == 4_096 && map.MaximumObservedPathUtf8Bytes == 4_096
            && map.MaximumDescriptorOnlyIdentitiesPerSweep == 32
            && map.Roots.SequenceEqual(new[] { new MonitoredAttributionRoot("evidence", manifest.PrivateRoot, true) })
            && map.PackageDirectoryNames.SequenceEqual(MonitoredRunManifestValidator.PackageDirectoryNames)
            && map.RecoveryDirectoryNames.SequenceEqual(MonitoredRunManifestValidator.RecoveryDirectoryNames)
            && map.RuntimeOnlyDescriptorTargets.SequenceEqual(MonitoredRunManifestValidator.RuntimeOnlyDescriptorTargets)
            && map.ReadOnlyDependencyRoots.Count is > 0 and <= 16 && map.RuntimeOnlyDescriptorProofs.Count == 1,
            "PrevalidationAttributionMismatch");
        foreach (var dependency in map.ReadOnlyDependencyRoots)
        {
            var resolved = MonitoredPathRules.ResolveAbsoluteDirectory(dependency, mustExist: true);
            Require(!MonitoredPathRules.IsContained(resolved, manifest.PrivateRoot)
                && !MonitoredPathRules.IsContained(manifest.PrivateRoot, resolved),
                "PrevalidationDependencyOverlap");
            Require(resolved.TrimEnd(Path.DirectorySeparatorChar) is not ("/tmp" or "/var/tmp")
                && !MonitoredPathRules.PathsEqual(resolved, Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)),
                "PrevalidationGenericTempExemptionRejected");
        }
        var proof = map.RuntimeOnlyDescriptorProofs.Single();
        Require(proof.Schema == "dotnet-runtime-memory-proof/1" && proof.ResourceKind == "dotnet-doublemapper"
            && proof.DescriptorTarget == "/memfd:doublemapper (deleted)" && proof.BackingFileSystem == "tmpfs"
            && proof.BackingFileSystemMagic == LinuxRuntimeMemoryClassifier.TmpfsMagic
            && proof.RequireDeletedDescriptor && proof.RequireZeroLinks && proof.RequireExecutableBackingMapping
            && proof.RequireExecutableRuntimeMapping && !string.IsNullOrWhiteSpace(proof.Rationale)
            && Path.GetFileName(proof.RuntimeNativeBinary.Path) == "libcoreclr.so"
            && manifest.NativeBinaries.Contains(proof.RuntimeNativeBinary), "PrevalidationRuntimeProof");
    }

    internal static IEnumerable<MonitoredBinaryIdentity> Binaries(PrevalidationManifest manifest)
        => new[] { manifest.RuntimeBinary, manifest.ToolBinary, manifest.SampleBinary }
            .Concat(manifest.ManagedBinaries).Concat(manifest.NativeBinaries);

    internal static string RuntimeEnvironmentHash()
    {
        var values = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Select(static item => new KeyValuePair<string, string>((string)item.Key, (string?)item.Value ?? string.Empty))
            .Where(static item => item.Key != "DOTNET_NOLOGO"
                && (item.Key.StartsWith("DOTNET_", StringComparison.Ordinal)
                    || item.Key.StartsWith("COMPlus_", StringComparison.Ordinal)
                    || item.Key.StartsWith("CORECLR_", StringComparison.Ordinal)
                    || item.Key is "LD_PRELOAD" or "LD_LIBRARY_PATH" or "TMPDIR" or "TMP" or "TEMP"
                        or "SQLITE_TMPDIR" or "LANG" or "LC_ALL" or "LC_CTYPE" or "TZ"))
            .OrderBy(static item => item.Key, StringComparer.Ordinal).ToArray();
        return MonitoredFile.HashBytes(JsonSerializer.SerializeToUtf8Bytes(values));
    }

    internal static string BinaryInventoryHash(PrevalidationManifest manifest)
        => MonitoredFile.HashBytes(JsonSerializer.SerializeToUtf8Bytes(
            Binaries(manifest).OrderBy(static item => item.Path, StringComparer.Ordinal).ToArray(), Json));

    private static void ValidateManagedInventory(PrevalidationManifest manifest)
    {
        var groups = Binaries(manifest).GroupBy(static item => Path.GetFullPath(item.Path), StringComparer.Ordinal);
        var declared = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            Require(group.Select(static item => item.Sha256).Distinct(StringComparer.Ordinal).Count() == 1,
                "PrevalidationBinaryIdentityConflict");
            declared.Add(group.Key, group.First().Sha256);
        }
        var supportPath = typeof(PrevalidationProtocol).Assembly.Location;
        Require(declared.TryGetValue(supportPath, out var supportHash)
            && supportHash == MonitoredFile.HashFile(supportPath), "PrevalidationExecutingSupportIdentity");
        Require(System.Reflection.Assembly.GetEntryAssembly()?.Location == manifest.ToolBinary.Path,
            "PrevalidationExecutingToolIdentity");
        foreach (var directory in new[] { Path.GetDirectoryName(manifest.ToolBinary.Path)!,
            Path.GetDirectoryName(manifest.SampleBinary.Path)! }.Distinct(StringComparer.Ordinal))
        {
            foreach (var path in MonitoredPathRules.EnumerateFilesRejectingLinks(directory, 4_096, 4_096)
                .Where(static path => path.EndsWith(".dll", StringComparison.Ordinal)
                    || path.EndsWith(".deps.json", StringComparison.Ordinal)
                    || path.EndsWith(".runtimeconfig.json", StringComparison.Ordinal)))
            {
                Require(declared.ContainsKey(Path.GetFullPath(path)), "PrevalidationManagedInventoryIncomplete");
            }
        }
    }

    internal static void VerifyArtifact(MonitoredBinaryIdentity artifact)
    {
        MonitoredRunManifestValidator.ValidateBinary(artifact, "PrevalidationArtifact");
        EnsureImmutable(artifact.Path);
    }

    internal static void EnsureImmutable(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw Error("PrevalidationLinuxOnly", "Prevalidation immutable artifacts require Linux.");
        }
        var resolved = MonitoredPathRules.ResolveExistingFile(path);
        Require((File.GetUnixFileMode(resolved) & (UnixFileMode.UserWrite | UnixFileMode.GroupWrite
            | UnixFileMode.OtherWrite)) == 0, "PrevalidationMutableArtifact");
        using var handle = File.OpenHandle(resolved, FileMode.Open, FileAccess.Read, FileShare.Read);
        Require(LinuxStatxHandleMetadataObserver.Instance.Observe(handle).LinkCount == 1,
            "PrevalidationLinkedArtifact");
    }

    internal static void Require(bool condition, string code)
    {
        if (!condition)
        {
            throw Error(code, "The closed prevalidation contract was not satisfied; execution remains blocked.");
        }
    }

    internal static DurableStorageExperimentException Error(string code, string message) => new(code, message);
}
