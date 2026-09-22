using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike;

public sealed class DurableStorageExperimentFoundationTests : IDisposable
{
    private const string ProtocolHash = "faaced68f26a53b7841713d4b1fe1044ce0dd3b277bbac2a9c6f9b6a09f498dd";
    private const string FixtureHash = "1c326a770c391bdb47a919987cf297ca185ec440feeaaa7d5d11b4cb986b716d";

    private readonly string _workspace = Path.Combine(
        AppContext.BaseDirectory,
        "dc5-storage-foundation",
        Guid.NewGuid().ToString("N"));

    public DurableStorageExperimentFoundationTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    [Fact]
    public void ClosedFoundationManifestValidatesWithoutCreatingRepositorySideEffects()
    {
        var repositoryRoot = FindRepositoryRoot();
        var manifestPath = WriteManifest();
        var before = SnapshotFiles(repositoryRoot);

        var result = DurableStorageExperimentFoundation.ValidateManifest(
            repositoryRoot,
            manifestPath,
            new DurableStorageAdapterRegistry());

        result.ExecutionGateClosed.Should().BeTrue();
        result.ProtocolRevision.Should().Be(3);
        result.ProtocolJsonSha256.Should().Be(ProtocolHash);
        result.FixtureManifestSha256.Should().Be(FixtureHash);
        result.AvailableAdapters.Should().BeEmpty();
        SnapshotFiles(repositoryRoot).Should().BeEquivalentTo(before);
    }

    [Fact]
    public void ExecutionGateAndMissingRuntimeIdentityFailExplicitly()
    {
        var repositoryRoot = FindRepositoryRoot();
        var enabled = WriteManifest(executionEnabled: true);
        Action execute = () => DurableStorageExperimentFoundation.ValidateManifest(
            repositoryRoot,
            enabled,
            new DurableStorageAdapterRegistry());
        execute.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("ExecutionGateClosed");

        var incomplete = WriteManifest(runtimeBinary: "");
        Action missingRuntime = () => DurableStorageExperimentFoundation.ValidateManifest(
            repositoryRoot,
            incomplete,
            new DurableStorageAdapterRegistry());
        missingRuntime.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("MissingIdentity");

        Action missingManifest = () => DurableStorageExperimentFoundation.ValidateManifest(
            repositoryRoot,
            Path.Combine(_workspace, "missing-manifest.json"),
            new DurableStorageAdapterRegistry());
        missingManifest.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("MissingFile");
    }

    [Fact]
    public void HashMismatchAndUnknownAdapterFailInsteadOfFallingBack()
    {
        var repositoryRoot = FindRepositoryRoot();
        var mismatch = WriteManifest(protocolHash: new string('0', 64));
        Action invalidHash = () => DurableStorageExperimentFoundation.ValidateManifest(
            repositoryRoot,
            mismatch,
            new DurableStorageAdapterRegistry());
        invalidHash.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("ProtocolHashMismatch");

        var unknown = WriteManifest(adapterId: "A", adapterCommit: "adapter-commit");
        Action missingAdapter = () => DurableStorageExperimentFoundation.ValidateManifest(
            repositoryRoot,
            unknown,
            new DurableStorageAdapterRegistry());
        missingAdapter.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("UnknownAdapter");
    }

    [Fact]
    public void PackageAndFramingContractRejectInvalidPreSealMetadata()
    {
        DurableStorageLogicalSchema.Columns.Should().Contain(column =>
            column.Name == "sourceTimeTicks" && column.Type == "int64-100ns-relative");
        DurableStorageFrameContract.RequiredFields.Should().Contain("commitFooter");
        DurableStorageFrameContract.MaximumRecordBytes.Should().Be(4_096);
        DurableStorageFrameContract.MaximumRecordsPerBatch.Should().Be(64);

        var valid = new DurableStoragePreSealResult(
            CanonicalMembers:
            [
                new DurableStorageMember(
                    DurableStoragePackageLayout.CanonicalMember("records.bin"),
                    128,
                    new string('a', 64),
                    DurableStorageMemberRole.CanonicalData),
            ],
            QueryMembers:
            [
                new DurableStorageMember(
                    DurableStoragePackageLayout.QueryMember("index.db"),
                    256,
                    new string('b', 64),
                    DurableStorageMemberRole.QueryIndex),
            ],
            FinalBytes: 384,
            AdapterConfigurationDigest: new string('c', 64));
        Action validate = () => DurableStorageSealRules.ValidatePreSeal(
            valid,
            new DurableCounterPipelineLimits(BatchMaxAge: TimeSpan.FromMilliseconds(100)));
        validate.Should().NotThrow();

        var traversal = valid with
        {
            CanonicalMembers =
            [
                new DurableStorageMember("../outside", 1, new string('a', 64), DurableStorageMemberRole.CanonicalData),
            ],
        };
        Action invalid = () => DurableStorageSealRules.ValidatePreSeal(
            traversal,
            new DurableCounterPipelineLimits(BatchMaxAge: TimeSpan.FromMilliseconds(100)));
        invalid.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("InvalidPackageMember");
    }

    [Fact]
    public void RecoveryRequiresNewIdsAndExplicitUnknownTailProvenance()
    {
        var request = new DurableStorageRecoveryRequest(
            SourceCaptureId: "capture-source",
            SourceArtifactId: "artifact-source",
            NewCaptureId: "capture-recovered",
            NewArtifactId: "artifact-recovered",
            SourcePackageRoot: "/host-controlled/source",
            RecoveryStagingRoot: "/host-controlled/staging",
            Reason: "explicit-test-recovery");
        var result = new DurableStorageRecoveryResult(
            CaptureId: request.NewCaptureId,
            ArtifactId: request.NewArtifactId,
            DerivedFromCaptureId: request.SourceCaptureId,
            RecoveryReason: request.Reason,
            VolatileTailUnknown: true,
            RecoveredMembers: []);

        Action validate = () => DurableStorageRecoveryRules.Validate(request, result);
        validate.Should().NotThrow();

        Action reusedIdentity = () => DurableStorageRecoveryRules.Validate(
            request with { NewCaptureId = request.SourceCaptureId },
            result with { CaptureId = request.SourceCaptureId });
        reusedIdentity.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("RecoveryIdentityReuse");

        Action knownTail = () => DurableStorageRecoveryRules.Validate(
            request,
            result with { VolatileTailUnknown = false });
        knownTail.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("RecoveryTailOverclaim");
    }

    [Theory]
    [InlineData("")]
    [InlineData("records.bin")]
    [InlineData("query/records.bin")]
    [InlineData("canonical/")]
    [InlineData("canonical//records.bin")]
    [InlineData("canonical/./records.bin")]
    [InlineData("canonical/../records.bin")]
    [InlineData("canonical\\records.bin")]
    [InlineData("canonical/C:/records.bin")]
    public void CanonicalMembersRequireNormalizedReservedPaths(string path)
    {
        var result = new DurableStoragePreSealResult(
            [new DurableStorageMember(path, 1, new string('a', 64), DurableStorageMemberRole.CanonicalData)],
            [],
            1,
            new string('c', 64));

        Action validate = () => DurableStorageSealRules.ValidatePreSeal(
            result,
            new DurableCounterPipelineLimits(BatchMaxAge: TimeSpan.FromMilliseconds(100)));

        validate.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("InvalidPackageMember");
    }

    [Fact]
    public void QueryMembersCannotUseCanonicalDirectory()
    {
        var result = new DurableStoragePreSealResult(
            [],
            [new DurableStorageMember("canonical/index.db", 1, new string('b', 64), DurableStorageMemberRole.QueryIndex)],
            1,
            new string('c', 64));

        Action validate = () => DurableStorageSealRules.ValidatePreSeal(
            result,
            new DurableCounterPipelineLimits(BatchMaxAge: TimeSpan.FromMilliseconds(100)));

        validate.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("InvalidPackageMember");
    }

    [Fact]
    public void AdapterCatalogUsesOrdinalOrdering()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var registry = new DurableStorageAdapterRegistry(
                [new IdentityOnlyFactory("a"), new IdentityOnlyFactory("A")]);

            registry.Available.Select(static identity => identity.Id).Should().Equal("A", "a");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    private sealed class IdentityOnlyFactory(string id) : IDurableCounterStorageAdapterFactory
    {
        public DurableStorageAdapterIdentity Identity { get; } = new(id, "test", "test", "unavailable");

        public IDurableCounterStorageAdapter Create(DurableStorageAdapterCreateRequest request)
            => throw new NotSupportedException("This fixture only describes an adapter identity.");

        public IDurableCounterReadonlyStore OpenReadonly(DurableStorageOpenRequest request)
            => throw new NotSupportedException("This fixture cannot open a package.");

        public ValueTask<DurableStorageRecoveryResult> RecoverAsync(
            DurableStorageRecoveryRequest request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("This fixture cannot recover a package.");
    }

    [Fact]
    public void RecoveredQualityPreservesUnknownAccountingAndKnownRetainedCount()
    {
        var recovered = new DurableStorageQualityReport(null, 64, VolatileTailUnknown: true);

        Action validate = () => DurableStorageQualityRules.Validate(recovered);
        validate.Should().NotThrow();
        recovered.FinalPipelineQuality.Should().BeNull();
        recovered.RetainedRecords.Should().Be(64);

        Action missingKnownQuality = () => DurableStorageQualityRules.Validate(
            recovered with { VolatileTailUnknown = false });
        missingKnownQuality.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("MissingFinalQuality");
    }

    [Fact]
    public void FinalizedQualityRequiresExactQuiescentHostAccounting()
    {
        var accounting = new DurableCounterAccounting(
            Offered: 100, Rejected: 3, Admitted: 97,
            InCopy: 0, Queued: 0, ActiveBatch: 0, Committed: 97,
            FailedAfterAdmission: 0, AbandonedKnown: 0, UnknownCommitOutcome: 0,
            OwnedRecords: 0, OwnedBytes: 0, PeakOwnedRecords: 64, PeakOwnedBytes: 262_144,
            AdmissionCancelled: false, Rejections: new Dictionary<string, long> { ["invalid"] = 3 });
        var quality = new DurableCounterQuery([], new DurableCounterPipelineLimits(
            BatchMaxAge: TimeSpan.FromMilliseconds(100))).Quality(accounting);
        var finalized = new DurableStorageQualityReport(quality, 97, VolatileTailUnknown: false);

        Action validate = () => DurableStorageQualityRules.Validate(finalized);
        validate.Should().NotThrow();
        finalized.FinalPipelineQuality!.Accounting.Should().BeSameAs(accounting);

        Action inventRecoveryQuality = () => DurableStorageQualityRules.Validate(
            finalized with { VolatileTailUnknown = true });
        inventRecoveryQuality.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("RecoveryQualityOverclaim");

        Action activeWriter = () => DurableStorageQualityRules.Validate(finalized with
        {
            FinalPipelineQuality = quality with { Accounting = accounting with { ActiveBatch = 1 } },
        });
        activeWriter.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("InvalidFinalQuality");

        Action lostRecord = () => DurableStorageQualityRules.Validate(finalized with { RetainedRecords = 96 });
        lostRecord.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("RetainedCountMismatch");
    }

    [Theory]
    [InlineData(-1L, "InvalidRetainedCount")]
    [InlineData(0L, "RetainedCountMismatch")]
    [InlineData(96L, "RetainedCountMismatch")]
    [InlineData(97L, null)]
    [InlineData(100L, null)]
    [InlineData(104L, null)]
    [InlineData(105L, "RetainedCountMismatch")]
    public void UncertainCommitsStillBoundRetainedRecords(long retained, string? error)
    {
        var report = new DurableStorageQualityReport(CreateUncertainQuality(), retained, VolatileTailUnknown: false);
        Action validate = () => DurableStorageQualityRules.Validate(report);

        if (error is null)
        {
            validate.Should().NotThrow();
        }
        else
        {
            validate.Should().Throw<DurableStorageExperimentException>().Which.Code.Should().Be(error);
        }
    }

    [Fact]
    public void UncertainCommitsCannotBypassAdmissionConservationOrWrapTotals()
    {
        var quality = CreateUncertainQuality();
        DurableCounterAccounting[] invalidAccounting =
        [
            quality.Accounting with { Offered = 0 },
            quality.Accounting with { Offered = 108, Admitted = 105 },
            quality.Accounting with { Committed = -1, UnknownCommitOutcome = 105 },
            quality.Accounting with
            {
                Offered = 0, Rejected = 0, Admitted = 0,
                Committed = long.MaxValue, FailedAfterAdmission = long.MaxValue, UnknownCommitOutcome = 2,
            },
        ];
        foreach (var accounting in invalidAccounting)
        {
            var report = new DurableStorageQualityReport(
                quality with { Accounting = accounting }, 100, VolatileTailUnknown: false);
            Action validate = () => DurableStorageQualityRules.Validate(report);
            validate.Should().Throw<DurableStorageExperimentException>()
                .Which.Code.Should().Be("InvalidFinalQuality");
        }
    }

    [Fact]
    public void FreshQualityMatchesManifestValuesAcrossSerialization()
    {
        var quality = CreateUncertainQuality();
        var manifest = new DurableStoragePackageManifest(
            DurableStorageExperimentVersions.PackageContract,
            DurableStorageExperimentVersions.RecordSchema,
            "capture", "artifact", DateTimeOffset.UnixEpoch,
            new DurableStorageAdapterIdentity("test", "1", "test", "test"),
            ProtocolHash, FixtureHash, "760dfe8c30e57ce4a226178f316b8f2ad047943a",
            [], null, null, VolatileTailUnknown: false, FinalPipelineQuality: quality);
        var reopenedManifest = JsonSerializer.Deserialize<DurableStoragePackageManifest>(
            JsonSerializer.Serialize(manifest))!;
        reopenedManifest.FinalPipelineQuality!.Accounting.Rejections
            .Should().NotBeSameAs(quality.Accounting.Rejections);
        var report = new DurableStorageQualityReport(quality, 100, VolatileTailUnknown: false);

        Action validate = () => DurableStorageQualityRules.Validate(report, reopenedManifest);
        validate.Should().NotThrow();

        var forgedAccounting = quality.Accounting with
        {
            Offered = 100, Rejected = 0, Admitted = 100, Committed = 100, UnknownCommitOutcome = 0,
            Rejections = new Dictionary<string, long>(),
        };
        var forged = report with
        {
            FinalPipelineQuality = quality with { Accounting = forgedAccounting, UnknownCommitOutcome = false },
        };
        Action selfConsistent = () => DurableStorageQualityRules.Validate(forged);
        selfConsistent.Should().NotThrow();
        Action substituteSnapshot = () => DurableStorageQualityRules.Validate(forged, reopenedManifest);
        substituteSnapshot.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("QualitySnapshotMismatch");

        Action changeReason = () => DurableStorageQualityRules.Validate(report with
        {
            FinalPipelineQuality = quality with
            {
                Accounting = quality.Accounting with
                {
                    Rejections = new Dictionary<string, long> { ["other"] = 3 },
                },
            },
        }, reopenedManifest);
        changeReason.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("QualitySnapshotMismatch");

        Action hideUnknownTail = () => DurableStorageQualityRules.Validate(
            report, reopenedManifest with { VolatileTailUnknown = true });
        hideUnknownTail.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("QualitySnapshotMismatch");
    }

    private static DurableCounterQualityReport CreateUncertainQuality()
        => new DurableCounterQuery([], new DurableCounterPipelineLimits(
            BatchMaxAge: TimeSpan.FromMilliseconds(100))).Quality(new DurableCounterAccounting(
                Offered: 107, Rejected: 3, Admitted: 104,
                InCopy: 0, Queued: 0, ActiveBatch: 0, Committed: 97,
                FailedAfterAdmission: 0, AbandonedKnown: 0, UnknownCommitOutcome: 7,
                OwnedRecords: 0, OwnedBytes: 0, PeakOwnedRecords: 64, PeakOwnedBytes: 262_144,
                AdmissionCancelled: false, Rejections: new Dictionary<string, long> { ["invalid"] = 3 }));

    private string WriteManifest(
        bool executionEnabled = false,
        string protocolHash = ProtocolHash,
        string runtimeBinary = "dotnet-runtime-binary-identity",
        string? adapterId = null,
        string? adapterCommit = null)
    {
        var manifestPath = Path.Combine(_workspace, $"{Guid.NewGuid():N}.json");
        var manifest = new
        {
            schema = DurableStorageExperimentVersions.FoundationSchema,
            executionEnabled,
            protocolRevision = 3,
            protocolJson = "docs/design/durable-capture-comparison-protocol.json",
            protocolJsonSha256 = protocolHash,
            fixtureManifest =
                "tests/DotnetDiagnostics.Core.Tests/DurableCounterSpike/durable-counter-fixture-manifest.json",
            fixtureManifestSha256 = FixtureHash,
            pipelineCommit = "760dfe8c30e57ce4a226178f316b8f2ad047943a",
            runtimeBinary,
            hostFacts = "foundation-unit-host",
            clockConversion = "fixture-relative ticks at 100 ns; exact integer conversion",
            adapterId,
            adapterCommit,
            adapterConfiguration = adapterId is null ? null : new { profile = "foundation-only" },
        };
        File.WriteAllBytes(
            manifestPath,
            JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return manifestPath;
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "global.json")))
        {
            current = current.Parent;
        }
        return current?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static IReadOnlyDictionary<string, FileStamp> SnapshotFiles(string repositoryRoot)
        => Directory.EnumerateFiles(
                repositoryRoot,
                "*",
                SearchOption.AllDirectories)
            .Where(static path => !path.Contains(
                $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal)
                && !path.Contains(
                    $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)
                && !path.Contains(
                    $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
            .ToDictionary(
                path => Path.GetRelativePath(repositoryRoot, path),
                path => new FileStamp(
                    new FileInfo(path).Length,
                    File.GetLastWriteTimeUtc(path),
                    HashFile(path)),
                StringComparer.Ordinal);

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed record FileStamp(long Length, DateTime LastWriteUtc, string Sha256);
}
