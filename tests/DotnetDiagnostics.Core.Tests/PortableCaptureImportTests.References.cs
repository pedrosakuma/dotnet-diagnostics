using System.Text;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.UseCases;

namespace DotnetDiagnostics.Core.Tests;

public sealed partial class PortableCaptureImportTests
{
    [Fact]
    public async Task RecoveryAliasesCompositionWrappersAndArtifactLocalStackNamesSurviveRepeatedImport()
    {
        if (!Linux) return;
        const string parentAlias = "11111111111111111111111111111111";
        const string childAlias = "22222222222222222222222222222222";
        const string cpuAlias = "33333333333333333333333333333333";
        CaptureInfo source;
        await using (var writer = await Store("producer").CreateAsync(new("references"), Owner))
        {
            writer.SetRecovery("44444444444444444444444444444444",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["capture.sqlite"] = new string('a', 64) });
            var parent = writer.AddRecoveredArtifact(new(parentAlias, "batch", "parent"));
            var child = writer.AddRecoveredArtifact(new(childAlias, "counters", "child"));
            var cpu = writer.AddRecoveredArtifact(new(cpuAlias, "cpu-sample", "cpu"));
            Assert.True(await writer.AppendAsync(cpu, new(Category: CpuReplayStackObservationWriter.DefinitionCategory,
                Name: childAlias, Fields:
                [
                    new("stack", CaptureFieldKind.Text, StringValue: "[]"),
                    new("stackOrder", CaptureFieldKind.Text, StringValue: "leaf-to-root"),
                    new("stackTruncated", CaptureFieldKind.Boolean, BooleanValue: false),
                    new("provenance", CaptureFieldKind.Text, StringValue: "interpreted-stack-definition"),
                    new("sourceOccurrence", CaptureFieldKind.Boolean, BooleanValue: false),
                    new("literal", CaptureFieldKind.Text, StringValue: childAlias)
                ])));
            Assert.True(await writer.AppendAsync(cpu, new(Category: CpuReplayStackObservationWriter.SampleCategory,
                Name: childAlias, Fields:
                [
                    new("sourceSeconds", CaptureFieldKind.FloatingPoint, DoubleValue: 1.5),
                    new("weight", CaptureFieldKind.SignedInteger, Int64Value: 1),
                    new("sourceOccurrence", CaptureFieldKind.Boolean, BooleanValue: true)
                ])));
            writer.SetSnapshot(child, 1, CaptureArtifactCodec.Encode("counters",
                new CounterSnapshot(42, DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1), [], [], []), 8 * 1024 * 1024));
            var composition = new DurableCaptureComposition(
            [
                new(childAlias, "counters", "child", parentAlias, 0, 0, null,
                    new Dictionary<string, long?>(), 0, null, false, true),
                new(cpuAlias, "cpu-sample", "cpu", parentAlias, 2, 2, null,
                    new Dictionary<string, long?>(), 0, null, false, false)
            ]);
            var encoded = DurableCaptureCompositionCodec.Encode("batch", composition, 8 * 1024 * 1024);
            writer.SetSnapshot(parent, 3, DurableCaptureSnapshotMetadata.Encode("batch", 2, encoded,
                new(false, 0, 0, null, new Dictionary<string, long?>(), 0), 8 * 1024 * 1024));
            source = await writer.CompleteAsync();
        }
        var location = "producer";
        var capture = source.CaptureId;
        for (var pass = 0; pass < 2; pass++)
        {
            using var bytes = new MemoryStream();
            var archive = await Service(location).ExportAsync(new(Key(), [new(capture, null)]), bytes, Owner);
            bytes.Position = 0;
            var destination = pass == 0 ? "one" : "two";
            var imported = await Service(destination).ImportAsync(new(Key(), archive.ArchiveBytes, archive.ArchiveSha256),
                bytes, Owner, Allow);
            Assert.True(imported.Complete);
            capture = Assert.Single(imported.Entries).Mapping!.LocalCaptureId;
            location = destination;
            using var reader = await Store(destination).OpenAsync(capture, Owner);
            Assert.Equal(source.Quality, reader.Info.Quality);
            Assert.Equal(source.DerivedFrom, reader.Info.DerivedFrom);
            var root = reader.Info.Artifacts.Single(static item => item.Kind == "batch");
            var leaf = reader.Info.Artifacts.Single(static item => item.Kind == "counters");
            var samples = reader.Info.Artifacts.Single(static item => item.Kind == "cpu-sample");
            Assert.Equal(parentAlias, root.SourceArtifactId);
            Assert.Equal(childAlias, leaf.SourceArtifactId);
            Assert.Equal(cpuAlias, samples.SourceArtifactId);
            var wrapper = reader.ReadSnapshot(root.ArtifactId)!;
            var nested = DurableCaptureSnapshotMetadata.Decode("batch", wrapper, 8 * 1024 * 1024).Snapshot;
            var group = DurableCaptureCompositionCodec.Decode("batch", root.ArtifactId, nested, reader.Info, new());
            Assert.Contains(group.Children, item => item.ArtifactId == leaf.ArtifactId && item.ParentArtifactId == root.ArtifactId);
            Assert.Contains(group.Children, item => item.ArtifactId == samples.ArtifactId && item.ParentArtifactId == root.ArtifactId);
            Assert.DoesNotContain(childAlias, Encoding.UTF8.GetString(nested.Utf8Json.Span));
            var records = reader.Query(new(samples.ArtifactId)).Records;
            Assert.Equal(2, records.Count);
            Assert.All(records, row => Assert.Equal(childAlias, row.Record.Name));
            Assert.Equal(childAlias, records[0].Record.Fields!.Single(static field => field.Name == "literal").StringValue);
        }
    }

    [Theory]
    [InlineData("ProcessorCount")]
    [InlineData("duplicate")]
    [InlineData("type-activation")]
    public async Task MalformedTypedPayloadsAreRejectedBeforeDtoPromotion(string kind)
    {
        if (!Linux) return;
        var members = ReadArchive(Frozen());
        byte[] snapshot;
        if (kind == "ProcessorCount")
        {
            snapshot = CaptureArtifactCodec.Encode("counters",
                new CounterSnapshot(42, DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1), [], [], []) { ProcessorCount = -1 },
                8 * 1024 * 1024);
        }
        else if (kind == "duplicate") snapshot = "{\"kind\":\"counters\",\"kind\":\"counters\",\"snapshot\":{}}"u8.ToArray();
        else snapshot = "{\"kind\":\"counters\",\"snapshot\":{\"$type\":\"System.Type\"}}"u8.ToArray();
        var sql = "INSERT INTO snapshots VALUES('cccccccccccccccccccccccccccccccc',1,x'" +
            Convert.ToHexString(snapshot) + "');";
        members[6] = (members[6].Name, CreateKnownSql(sql));
        ResealEntry(members, 1);
        var bytes = Zip(members);
        var error = await Assert.ThrowsAsync<CaptureStoreException>(() => Service().ImportAsync(
            new(Key(), bytes.Length, Hash(bytes)), new MemoryStream(bytes), Owner, Allow));
        Assert.Equal(CaptureErrorCode.CorruptPackage, error.Code);
        Assert.Empty((await Store().ListAsync(Owner)).Captures);
    }
}
