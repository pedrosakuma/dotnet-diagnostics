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
    }

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
