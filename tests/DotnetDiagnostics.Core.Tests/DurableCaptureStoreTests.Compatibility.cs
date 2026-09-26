using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetDiagnostics.Core.Captures;

namespace DotnetDiagnostics.Core.Tests;

public sealed partial class DurableCaptureStoreTests
{
    private const string V1ProducerCommit = "59e34e40681720334db3812f5746e463592449d0";
    private const string SealedV1Sha256 = "B2F22F08BB2BA46D54A6E157F45280281A96070A4BF56F47B3766579F59D7B7E";
    private const string InterruptedV1Sha256 = "0C32D4C6A7CE6BD3C9A4E6ED0AD35CBA520CEB4D5BADD2A47003707F398CEA59";

    [Fact]
    public async Task FrozenV1_ReadByActualV2Reader_WithoutChangingBytesOrInventingProvenance()
    {
        var fixture = MaterializeV1(sealedPackage: true);
        var before = Hashes(Package(fixture.CaptureId));
        using (var reader = await Store().OpenAsync(fixture.CaptureId, fixture.Access))
        {
            Assert.Equal(new CaptureFormatVersions(1, 1, 1, 1, 1, 1), reader.Format);
            Assert.Equal(new CaptureReaderIdentity("DotnetDiagnostics.Core.Captures.CaptureReader", 3), reader.ExecutingReader);
            var artifact = Assert.Single(reader.Info.Artifacts);
            Assert.Null(artifact.Provenance);
            Assert.Null(artifact.SourceArtifactId);
            var records = reader.Query(new(artifact.ArtifactId)).Records;
            Assert.Equal(2, records.Count);
            Assert.Equal("frozen-v1 🧪", records[0].Record.Fields![0].StringValue);
            Assert.Equal(long.MaxValue, records[0].Record.Fields![1].Int64Value);
            Assert.Null(records[1].Record.Timestamp);
            Assert.Null(records[1].Record.NumericValue);
            var snapshot = reader.ReadSnapshot(artifact.ArtifactId)!;
            Assert.Equal(1, snapshot.Version);
            Assert.Equal("synthetic", snapshot.Kind);
            Assert.Equal("{\"fixtureVersion\":1,\"synthetic\":true}", Encoding.UTF8.GetString(snapshot.Utf8Json.Span));
        }
        Assert.Null(Assert.Single((await Store().ListAsync(fixture.Access)).Captures).Artifacts[0].Provenance);
        Assert.Equal(before, Hashes(Package(fixture.CaptureId)));
        Assert.False(File.Exists(Path.Combine(Package(fixture.CaptureId), "capture.sqlite-wal")));
        Assert.False(File.Exists(Path.Combine(Package(fixture.CaptureId), "capture.sqlite-shm")));
    }

    [Fact]
    public async Task FrozenInterruptedV1_RecoversIntoNewV2Identity_LeavingAllSourceBytesUnchanged()
    {
        var fixture = MaterializeV1(sealedPackage: false);
        var directory = Package(fixture.CaptureId);
        var before = Hashes(directory);
        await Error(CaptureErrorCode.Incomplete, () => Store().OpenAsync(fixture.CaptureId, fixture.Access));
        var recovered = await Store().RecoverAsync(fixture.CaptureId, fixture.Access);
        Assert.NotEqual(fixture.CaptureId, recovered.CaptureId);
        Assert.NotEqual(fixture.ArtifactId, Assert.Single(recovered.Artifacts).ArtifactId);
        Assert.Equal(fixture.ArtifactId, recovered.Artifacts[0].SourceArtifactId);
        Assert.Equal(fixture.CaptureId, recovered.DerivedFrom);
        Assert.True(recovered.Quality.UnknownTail);
        Assert.True(recovered.Quality.IsIncomplete);
        Assert.Null(recovered.Artifacts[0].Provenance);
        Assert.Equal(before, Hashes(directory));
        Assert.Equal(before, new SortedDictionary<string, string>(
            recovered.SourceHashes!.ToDictionary(static p => p.Key, static p => p.Value), StringComparer.Ordinal));
        using var reader = await Store().OpenAsync(recovered.CaptureId, fixture.Access);
        Assert.Equal(new CaptureFormatVersions(2, 1, 1, 1, 2, 2), reader.Format);
        Assert.Equal(3, reader.ExecutingReader.Version);
        Assert.Equal("committed-before-disposal",
            Assert.Single(reader.Query(new(recovered.Artifacts[0].ArtifactId)).Records).Record.Name);
    }

    [Fact]
    public async Task V2Provenance_RoundTripsAlreadyKnownFacts_AndDoesNotGrantAuthority()
    {
        await using var writer = await Store().CreateAsync(new("provenance"), Owner);
        var artifact = writer.AddArtifact("events", "known facts");
        var absent = writer.AddArtifact("events", "unknown facts");
        var started = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.FromHours(2));
        var provenance = new CaptureArtifactProvenance(
            ProcessId: int.MaxValue, ProducingTool: "*", OriginalHandleOrigin: "root",
            StartedAt: started, Duration: TimeSpan.FromMilliseconds(2500),
            ProcessStartUtc: started.AddHours(-1), RuntimeName: "CoreCLR", RuntimeVersion: "10.0.5");
        writer.SetArtifactProvenance(artifact, provenance with { Duration = null });
        Assert.True(writer.TryAppend(artifact, new(Name: "already-collected")));
        writer.SetArtifactProvenance(artifact, provenance);
        var info = await writer.CompleteAsync();
        var expected = provenance with { StartedAt = started.ToUniversalTime(), ProcessStartUtc = started.AddHours(-1).ToUniversalTime() };
        Assert.Equal(expected, info.Artifacts.Single(a => a.ArtifactId == artifact).Provenance);
        Assert.Null(info.Artifacts.Single(a => a.ArtifactId == absent).Provenance);
        Assert.Equal(CaptureErrorCode.Closed,
            Assert.Throws<CaptureStoreException>(() => writer.SetArtifactProvenance(artifact, provenance)).Code);
        await Error(CaptureErrorCode.Forbidden, () => Store().OpenAsync(info.CaptureId, new("bob")));
        await Error(CaptureErrorCode.Forbidden, () => Store().OpenAsync(info.CaptureId, new("root")));
        var before = Hashes(Package(info.CaptureId));
        using (var reader = await Store().OpenAsync(info.CaptureId, new("bob", AllOwners: true)))
        {
            Assert.Equal(new CaptureFormatVersions(2, 1, 1, 1, 2, 2), reader.Format);
            Assert.Equal(3, reader.ExecutingReader.Version);
            Assert.Equal(expected, reader.Info.Artifacts.Single(a => a.ArtifactId == artifact).Provenance);
            Assert.Equal("alice", reader.Info.OwnerId);
            Assert.Single(reader.Query(new(artifact)).Records);
        }
        Assert.Equal(before, Hashes(Package(info.CaptureId)));
    }

    [Fact]
    public async Task V2Recovery_PreservesProvenanceWithoutAttachingOrFillingMissingFacts()
    {
        var writer = await Store().CreateAsync(new("interrupted provenance"), Owner);
        var artifact = writer.AddArtifact("test", "test");
        var provenance = new CaptureArtifactProvenance(ProcessId: 42, ProducingTool: "collect_events",
            OriginalHandleOrigin: "Live", Duration: TimeSpan.FromSeconds(3));
        writer.SetArtifactProvenance(artifact, provenance);
        Assert.True(writer.TryAppend(artifact, new(Name: "committed")));
        await writer.DisposeAsync();
        var original = writer.Reference.CaptureId;
        var before = Hashes(Package(original));
        var recovered = await Store().RecoverAsync(original, Owner);
        Assert.Equal(before, Hashes(Package(original)));
        Assert.Equal(provenance, Assert.Single(recovered.Artifacts).Provenance);
        Assert.Null(recovered.Artifacts[0].Provenance!.ProcessStartUtc);
        Assert.Null(recovered.Artifacts[0].Provenance!.RuntimeName);
        Assert.Null(recovered.Artifacts[0].Provenance!.RuntimeVersion);
    }

    [Fact]
    public async Task Provenance_InvalidValuesAreExplicitlyRejected_AndDoNotReplaceExistingMetadata()
    {
        await using var writer = await Store().CreateAsync(new("bounded provenance"), Owner);
        var artifact = writer.AddArtifact("test", "test");
        var known = new CaptureArtifactProvenance(ProducingTool: "collect_events");
        writer.SetArtifactProvenance(artifact, known);
        CaptureArtifactProvenance[] invalid =
        [
            new(ProcessId: 0),
            new(ProcessId: -1),
            new(Duration: TimeSpan.FromTicks(-1)),
            new(ProducingTool: new string('x', 1025)),
            new(OriginalHandleOrigin: new string('é', 600)),
            new(RuntimeName: new string((char)0xD800, 1)),
            new(RuntimeVersion: new string('x', 1025))
        ];
        foreach (var value in invalid)
            Assert.Equal(CaptureErrorCode.InvalidInput,
                Assert.Throws<CaptureStoreException>(() => writer.SetArtifactProvenance(artifact, value)).Code);
        Assert.Equal(CaptureErrorCode.InvalidInput,
            Assert.Throws<CaptureStoreException>(() => writer.SetArtifactProvenance(Guid.NewGuid().ToString("N"), known)).Code);
        Assert.Equal(known, Assert.Single((await writer.CompleteAsync()).Artifacts).Provenance);
    }

    [Theory]
    [InlineData("PackageVersion")]
    [InlineData("SchemaVersion")]
    [InlineData("RecordVersion")]
    [InlineData("IndexVersion")]
    [InlineData("WriterVersion")]
    [InlineData("ReaderVersion")]
    public async Task EveryIndependentFormatAxis_RejectsUnsupportedFutureVersions(string axis)
    {
        await using var writer = await Store().CreateAsync(new("future-axis"), Owner);
        await writer.CompleteAsync();
        var directory = Package(writer.Reference.CaptureId);
        var path = Path.Combine(directory, "manifest.json");
        var metadata = JsonNode.Parse(await File.ReadAllBytesAsync(path))!;
        metadata[axis] = 99;
        await File.WriteAllTextAsync(path, metadata.ToJsonString());
        ResealForMalformedFixture(directory);
        var before = Hashes(directory);
        await Error(CaptureErrorCode.UnsupportedFormat, () => Store().OpenAsync(writer.Reference.CaptureId, Owner));
        Assert.Equal(before, Hashes(directory));
    }

    [Fact]
    public async Task KnownVersionTuplesMustMatchManifestAndDatabase_AndUnknownRequiredFeaturesFailClosed()
    {
        await using var writer = await Store().CreateAsync(new("format agreement"), Owner);
        await writer.CompleteAsync();
        var directory = Package(writer.Reference.CaptureId);
        using (var connection = CapturePackage.Connect(directory, immutable: false))
            CapturePackage.Execute(connection, "UPDATE format SET package=1,writer_version=1,reader_version=1;");
        ResealForMalformedFixture(directory);
        await Error(CaptureErrorCode.CorruptPackage, () => Store().OpenAsync(writer.Reference.CaptureId, Owner));

        var path = Path.Combine(directory, "manifest.json");
        var metadata = JsonNode.Parse(await File.ReadAllBytesAsync(path))!;
        metadata["RequiredFeatures"] = new JsonArray("normalized-scalars-v1", "artifact-provenance-v1", "unknown-feature");
        await File.WriteAllTextAsync(path, metadata.ToJsonString());
        ResealForMalformedFixture(directory);
        await Error(CaptureErrorCode.UnsupportedFormat, () => Store().OpenAsync(writer.Reference.CaptureId, Owner));
    }

    [Fact]
    public async Task CurrentProvenanceFeatureCannotBeDroppedToMakeV2AppearLikeV1()
    {
        await using var writer = await Store().CreateAsync(new("required provenance feature"), Owner);
        var artifact = writer.AddArtifact("test", "test");
        writer.SetArtifactProvenance(artifact, new(ProducingTool: "collect_events"));
        await writer.CompleteAsync();
        var directory = Package(writer.Reference.CaptureId);
        var path = Path.Combine(directory, "manifest.json");
        var metadata = JsonNode.Parse(await File.ReadAllBytesAsync(path))!;
        metadata["RequiredFeatures"] = new JsonArray("normalized-scalars-v1");
        await File.WriteAllTextAsync(path, metadata.ToJsonString());
        ResealForMalformedFixture(directory);
        await Error(CaptureErrorCode.UnsupportedFormat, () => Store().OpenAsync(writer.Reference.CaptureId, Owner));
    }

    [Fact]
    public async Task V1CannotClaimV2ProvenanceWithoutDeclaringItsRepresentation()
    {
        var fixture = MaterializeV1(sealedPackage: true);
        var directory = Package(fixture.CaptureId);
        var path = Path.Combine(directory, "manifest.json");
        var metadata = JsonNode.Parse(await File.ReadAllBytesAsync(path))!;
        metadata["Info"]!["Artifacts"]![0]!["Provenance"] = new JsonObject { ["ProcessId"] = 42 };
        await File.WriteAllTextAsync(path, metadata.ToJsonString());
        ResealForMalformedFixture(directory);
        await Error(CaptureErrorCode.UnsupportedFormat, () => Store().OpenAsync(fixture.CaptureId, fixture.Access));
    }

    private (string CaptureId, string ArtifactId, CaptureAccess Access) MaterializeV1(bool sealedPackage)
    {
        var fixtureDirectory = FindFrozenV1Fixtures();
        using var provenance = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixtureDirectory, "provenance.json")));
        Assert.Equal(V1ProducerCommit, provenance.RootElement.GetProperty("ProducerCommit").GetString());
        Assert.Equal("10.0.201", provenance.RootElement.GetProperty("ProducerSdk").GetString());
        Assert.Equal(provenance.RootElement.GetProperty("GeneratorSourceSha256").GetString(),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(fixtureDirectory, "generator.cs.txt")))));
        Assert.All(provenance.RootElement.GetProperty("PackageVersions").EnumerateObject(),
            static version => Assert.Equal(1, version.Value.GetInt32()));
        var archiveName = sealedPackage ? "sealed-v1.zip" : "interrupted-v1.zip";
        var item = provenance.RootElement.GetProperty("Packages").EnumerateArray()
            .Single(p => p.GetProperty("Archive").GetString() == archiveName);
        var archive = Path.Combine(fixtureDirectory, archiveName);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive)));
        Assert.Equal(sealedPackage ? SealedV1Sha256 : InterruptedV1Sha256, hash);
        Assert.Equal(hash, item.GetProperty("ArchiveSha256").GetString());
        var id = item.GetProperty("CaptureId").GetString()!;
        var captures = Path.Combine(_root, "captures");
        Directory.CreateDirectory(captures);
        File.WriteAllText(Path.Combine(captures, ".capture-store"), "dotnet-diagnostics-captures/1");
        var directory = Package(id);
        ZipFile.ExtractToDirectory(archive, directory);
        var expected = new SortedDictionary<string, string>(item.GetProperty("Members").EnumerateObject()
            .ToDictionary(static p => p.Name, static p => p.Value.GetString()!), StringComparer.Ordinal);
        Assert.Equal(expected, Hashes(directory));
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "manifest.json")));
        Assert.False(manifest.RootElement.GetProperty("Info").GetProperty("Artifacts")[0].TryGetProperty("Provenance", out _));
        return (id, item.GetProperty("ArtifactId").GetString()!, new(item.GetProperty("OwnerId").GetString()!));
    }

    private static string FindFrozenV1Fixtures()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "DotnetDiagnostics.Core.Tests", "Fixtures", "DurableCaptureV1");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("The independently frozen DurableCaptureV1 fixtures are required.");
    }
}
