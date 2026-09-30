using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetDiagnostics.Core.Captures;

namespace DotnetDiagnostics.Core.Tests;

public sealed partial class DurableCaptureStoreTests
{
    [Fact]
    public async Task TwoRecoveryRounds_PreserveExactOriginalChildReferencesAndEverySourceByte()
    {
        var options = new CaptureStoreOptions { MaxArtifacts = 3 };
        var store = Store(options);
        var writer = await store.CreateAsync(new("recovery group"), Owner);
        var root = writer.AddArtifact("synthetic-group", "root");
        var firstChild = writer.AddArtifact("synthetic", "same-name");
        var secondChild = writer.AddArtifact("synthetic", "same-name");
        var provenance = new CaptureArtifactProvenance(ProcessId: 42, ProducingTool: "synthetic-producer");
        writer.SetArtifactProvenance(firstChild, provenance);
        writer.SetArtifactProvenance(secondChild, provenance);
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            representationVersion = 2,
            children = new[]
            {
                new { childArtifactId = firstChild, source = "one" },
                new { childArtifactId = secondChild, source = "two" }
            }
        });
        writer.SetSnapshot(root, 2, body);
        Assert.True(writer.TryAppend(firstChild, new(Name: "first")));
        Assert.True(writer.TryAppend(secondChild, new(Name: "second")));
        Assert.Equal(CaptureErrorCode.CapacityExceeded,
            Assert.Throws<CaptureStoreException>(() => writer.AddArtifact("extra", "extra")).Code);
        await writer.DisposeAsync();

        var originalId = writer.Reference.CaptureId;
        var originalBytes = Hashes(Package(originalId));
        var first = await store.RecoverAsync(originalId, Owner);
        Assert.Equal(originalBytes, Hashes(Package(originalId)));
        AssertOriginalReferences(first);
        using (var reader = await store.OpenAsync(first.CaptureId, Owner))
        {
            var recoveredRoot = Assert.Single(reader.Info.Artifacts, a => a.SourceArtifactId == root);
            Assert.Equal(body, reader.ReadSnapshot(recoveredRoot.ArtifactId)!.Utf8Json.ToArray());
            Assert.Equal(2, reader.ReadSnapshot(recoveredRoot.ArtifactId)!.Version);
        }

        // Model loss of seal publication for a derived package; this is not a power-loss simulation.
        // Freeze the resulting unsealed source before round two, then assert every remaining byte.
        File.Delete(Path.Combine(Package(first.CaptureId), "seal.json"));
        var intermediateBytes = Hashes(Package(first.CaptureId));
        var second = await store.RecoverAsync(first.CaptureId, Owner);
        Assert.Equal(originalBytes, Hashes(Package(originalId)));
        Assert.Equal(intermediateBytes, Hashes(Package(first.CaptureId)));
        Assert.NotEqual(first.CaptureId, second.CaptureId);
        Assert.Equal(first.CaptureId, second.DerivedFrom);
        Assert.True(second.Quality.UnknownTail);
        AssertOriginalReferences(second);
        Assert.DoesNotContain(second.Artifacts, a => first.Artifacts.Any(old => old.ArtifactId == a.ArtifactId));
        using var finalReader = await store.OpenAsync(second.CaptureId, Owner);
        var finalRoot = Assert.Single(finalReader.Info.Artifacts, a => a.SourceArtifactId == root);
        var snapshot = finalReader.ReadSnapshot(finalRoot.ArtifactId)!;
        Assert.Equal(2, snapshot.Version);
        Assert.Equal(body, snapshot.Utf8Json.ToArray());
        using var group = JsonDocument.Parse(snapshot.Utf8Json);
        foreach (var reference in group.RootElement.GetProperty("children").EnumerateArray())
        {
            var originalChild = reference.GetProperty("childArtifactId").GetString();
            var target = Assert.Single(finalReader.Info.Artifacts,
                a => a.ArtifactId == originalChild || a.SourceArtifactId == originalChild);
            Assert.Equal(provenance, target.Provenance);
            Assert.Equal(originalChild == firstChild ? "first" : "second",
                Assert.Single(finalReader.Query(new(target.ArtifactId)).Records).Record.Name);
        }
        var manifest = CapturePackage.ReadManifest(Package(second.CaptureId), second.CaptureId);
        Assert.Contains(CapturePackage.RecoveryIdentityFeature, manifest.RequiredFeatures!);
        Assert.Equal(new CaptureFormatVersions(2, 1, 1, 1, 2, 2), finalReader.Format);

        void AssertOriginalReferences(CaptureInfo capture)
        {
            Assert.Equal(3, capture.Artifacts.Count);
            Assert.Equal(new[] { root, firstChild, secondChild }.Order(StringComparer.Ordinal),
                capture.Artifacts.Select(static a => a.SourceArtifactId).Order(StringComparer.Ordinal));
            Assert.All(capture.Artifacts, static a => Assert.NotEqual(a.ArtifactId, a.SourceArtifactId));
        }
    }

    [Theory]
    [InlineData("self")]
    [InlineData("other-current")]
    [InlineData("duplicate")]
    [InlineData("malformed")]
    [InlineData("undeclared")]
    [InlineData("no-alias")]
    public async Task RecoveryIdentity_RejectsAmbiguousMalformedOrUndeclaredAliases(string mutation)
    {
        var writer = await Store().CreateAsync(new("identity validation"), Owner);
        writer.AddArtifact("test", "first");
        writer.AddArtifact("test", "second");
        await writer.DisposeAsync();
        var recovered = await Store().RecoverAsync(writer.Reference.CaptureId, Owner);
        var directory = Package(recovered.CaptureId);
        var path = Path.Combine(directory, "manifest.json");
        var json = JsonNode.Parse(await File.ReadAllBytesAsync(path))!;
        var artifacts = json["Info"]!["Artifacts"]!.AsArray();
        var expectedCode = CaptureErrorCode.CorruptPackage;
        switch (mutation)
        {
            case "self":
                artifacts[0]!["SourceArtifactId"] = artifacts[0]!["ArtifactId"]!.GetValue<string>();
                break;
            case "other-current":
                artifacts[0]!["SourceArtifactId"] = artifacts[1]!["ArtifactId"]!.GetValue<string>();
                break;
            case "duplicate":
                artifacts[1]!["SourceArtifactId"] = artifacts[0]!["SourceArtifactId"]!.GetValue<string>();
                break;
            case "malformed":
                artifacts[0]!["SourceArtifactId"] = "../invalid";
                expectedCode = CaptureErrorCode.InvalidInput;
                break;
            case "undeclared":
                json["RequiredFeatures"] = new JsonArray("normalized-scalars-v1", "artifact-provenance-v1");
                expectedCode = CaptureErrorCode.UnsupportedFormat;
                break;
            case "no-alias":
                foreach (var artifact in artifacts) artifact!.AsObject().Remove("SourceArtifactId");
                break;
        }
        await File.WriteAllTextAsync(path, json.ToJsonString());
        ResealForMalformedFixture(directory);
        var before = Hashes(directory);
        await Error(expectedCode, () => Store().OpenAsync(recovered.CaptureId, Owner));
        Assert.Equal(before, Hashes(directory));
    }

    [Fact]
    public async Task MissingSourceIdentity_OnPreviousV2RemainsNullWithoutClaimingRecoveryMapping()
    {
        await using var writer = await Store().CreateAsync(new("older-v2"), Owner);
        writer.AddArtifact("test", "ordinary");
        var info = await writer.CompleteAsync();
        Assert.Null(Assert.Single(info.Artifacts).SourceArtifactId);
        var directory = Package(info.CaptureId);
        var path = Path.Combine(directory, "manifest.json");
        var json = JsonNode.Parse(await File.ReadAllBytesAsync(path))!;
        json["Info"]!["Artifacts"]![0]!.AsObject().Remove("SourceArtifactId");
        await File.WriteAllTextAsync(path, json.ToJsonString());
        ResealForMalformedFixture(directory);
        var before = Hashes(directory);
        using var reader = await Store().OpenAsync(info.CaptureId, Owner);
        Assert.Null(Assert.Single(reader.Info.Artifacts).SourceArtifactId);
        Assert.DoesNotContain(CapturePackage.RecoveryIdentityFeature,
            CapturePackage.ReadManifest(directory, info.CaptureId).RequiredFeatures!);
        Assert.Equal(before, Hashes(directory));
    }

    [Fact]
    public async Task RecoverySourceIdentity_DoesNotBypassDestinationArtifactCountCap()
    {
        var writer = await Store().CreateAsync(new("too-many-artifacts"), Owner);
        writer.AddArtifact("test", "first");
        writer.AddArtifact("test", "second");
        await writer.DisposeAsync();
        var before = Hashes(Package(writer.Reference.CaptureId));
        await Error(CaptureErrorCode.CapacityExceeded, () => Store(new CaptureStoreOptions { MaxArtifacts = 1 })
            .RecoverAsync(writer.Reference.CaptureId, Owner));
        Assert.Equal(before, Hashes(Package(writer.Reference.CaptureId)));
    }
}
