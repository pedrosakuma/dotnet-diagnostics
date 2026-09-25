using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Triage;
using DotnetDiagnostics.Core.UseCases;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed partial class DurableCaptureUseCasesTests
{
    [Fact]
    public async Task SweepRetainsWindowTriageResourcesFailuresAndExactChildReferencesWithoutSnapshotDuplication()
    {
        var service = Service();
        var triage = new TriageResult("healthy", TriageSeverity.Healthy,
            new(null, null, null, null, null, null, null, null, null))
        {
            ModelVersion = 2,
            Assessment = "inconclusive",
            ObservedSignals = [new("cpu", "elevated", Rich, [new("cpu", 30, "%", ">=", 25, Rich)])],
            Hypotheses = [new("cpu-bound", "moderate", Rich,
                [new("cpu", 30, "%", ">=", 25, Rich)], [], Rich)],
            TopIndicators = [new("cpu", 30, "%", 70, "elevated")],
        };
        var resources = new ProcessResources(42, At, 7, null, new(2, 1, 1, 1, 2),
            new(2, 3, 4, 5, 6), new(1024, 4096, 0.125), [Rich],
            new([new(At, 7, null, null, null, null)]))
        {
            ManagedVsNative = new(8192, 1024, 7168, 0.125, true) { Interpretation = Rich },
        };
        var result = await service.CaptureAsync("sweep", "sweep", Owner, async _ =>
        {
            var child = await service.RunChildAsync("counters", "counters", _ =>
            {
                var handle = _handles.RegisterWithMetadata(42, "counters", Snapshot, TimeSpan.FromMinutes(1));
                return Task.FromResult(DiagnosticResult.OkWithHandle(Snapshot, "counter", handle.Id, handle.ExpiresAt));
            });
            var sweep = new SweepResult(8, triage, child.Data, null, null, null, resources,
                new Dictionary<string, string?> { ["counters"] = child.Handle, ["gc"] = null }, [Rich]);
            return DiagnosticResult.Ok(sweep, "sweep");
        });
        Assert.False(result.IsError, result.Error?.Message);
        var info = result.Capture!;
        var root = info.Artifacts.Single(a => a.Name == "sweep");
        var opened = await service.OpenAsync(info.CaptureId, root.ArtifactId, Owner);
        var metadata = opened.Composition!.Metadata!.Sweep!;
        Assert.Equal(8, metadata.DurationSeconds);
        metadata.Triage.Should().BeEquivalentTo(triage);
        metadata.Resource.Should().BeEquivalentTo(resources);
        Assert.Equal([Rich], metadata.Failures);
        Assert.Equal(info.Artifacts.Single(a => a.Name == "counters").ArtifactId, metadata.ArtifactIds["counters"]);
        Assert.Null(metadata.ArtifactIds["gc"]);
        using var reader = await Store().OpenAsync(info.CaptureId, Owner);
        var json = System.Text.Encoding.UTF8.GetString(reader.ReadSnapshot(root.ArtifactId)!.Utf8Json.Span);
        Assert.DoesNotContain("\"Counters\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Gc\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Data!.Handles["counters"]!, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GcActivitiesRetainsObservedWindowsIntersectionStatusAndOverlayWithRecoverableReferences()
    {
        var service = Service();
        var gc = (GcSummary)CaptureArtifactCodecTests.Snapshots().First(row => (string)row[0] == "gc-events")[1];
        var activities = (ActivityCapture)CaptureArtifactCodecTests.Snapshots().First(row => (string)row[0] == "activities")[1];
        var overlay = new GcOverlayResult(2, 2, 1, 1, 3.5, 1, 4, 1, 0, false, Rich, false,
            [new("source", Rich, "activity", "trace", "span", 7, 3.5, 50,
                [new(2, "GC", "suspension", 4, 3.5)], false)], "known", "retained", "same");
        var result = await service.CaptureAsync("pair", "gc-activities", Owner, async _ =>
        {
            var gcResult = await service.RunChildAsync("gc", "gc", _ =>
            {
                var handle = _handles.RegisterWithMetadata(42, "gc-events", gc, TimeSpan.FromMinutes(1));
                return Task.FromResult(DiagnosticResult.OkWithHandle(gc, "gc", handle.Id, handle.ExpiresAt));
            });
            var activityResult = await service.RunChildAsync("activities", "activities", _ =>
            {
                var handle = _handles.RegisterWithMetadata(42, "activities", activities, TimeSpan.FromMinutes(1));
                return Task.FromResult(DiagnosticResult.OkWithHandle(activities, "activities", handle.Id, handle.ExpiresAt));
            });
            var pair = new GcActivitiesCapture(42, At.AddMinutes(-1), "partial",
                new("complete", At, At.AddSeconds(8), At.AddMilliseconds(500), At.AddSeconds(8),
                    _handles.TryGetWithKind(gcResult.Handle!)!.Value.Handle, gc, null),
                new("partial", At, At.AddSeconds(8), At.AddSeconds(1), At.AddSeconds(7),
                    _handles.TryGetWithKind(activityResult.Handle!)!.Value.Handle, activities, Rich),
                At.AddSeconds(1), At.AddSeconds(7), 500, overlay, Rich, [Rich]);
            return DiagnosticResult.Ok(pair, "partial") with { Cancelled = true };
        });
        Assert.Equal(CaptureState.Interrupted, result.Capture!.State);
        var recovered = await service.RecoverAsync(result.Capture.CaptureId, Owner);
        var root = recovered.Artifacts.Single(a => a.Name == "pair");
        var opened = await service.OpenAsync(recovered.CaptureId, root.ArtifactId, Owner);
        var metadata = opened.Composition!.Metadata!.GcActivities!;
        Assert.Equal(42, metadata.ProcessId);
        Assert.Equal(At.AddMinutes(-1), metadata.ProcessStartedAt);
        Assert.Equal("partial", metadata.Status);
        Assert.Equal(At.AddMilliseconds(500), metadata.Gc.ObservedStart);
        Assert.Equal(At.AddSeconds(8), metadata.Gc.RequestedEnd);
        Assert.Equal(At.AddSeconds(1), metadata.IntersectionStart);
        Assert.Equal(At.AddSeconds(7), metadata.IntersectionEnd);
        Assert.Equal(500, metadata.StartupSkewMs);
        Assert.Equal(Rich, metadata.Activities.UnavailableReason);
        Assert.Equal(Rich, metadata.OverlayUnavailableReason);
        metadata.Overlay.Should().BeEquivalentTo(overlay);
        Assert.Equal([Rich], metadata.Notes);
        Assert.Equal(recovered.Artifacts.Single(a => a.Kind == "gc-events").ArtifactId, metadata.Gc.ArtifactId);
        Assert.Equal(recovered.Artifacts.Single(a => a.Kind == "activities").ArtifactId, metadata.Activities.ArtifactId);
        using var reader = await Store().OpenAsync(recovered.CaptureId, Owner);
        var json = System.Text.Encoding.UTF8.GetString(reader.ReadSnapshot(root.ArtifactId)!.Utf8Json.Span);
        Assert.DoesNotContain("\"Capture\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Data!.Gc.Handle!.Id, json, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Data.Activities.Handle!.Id, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingParentChildReferenceAndOversizedMetadataFailRatherThanSilentlyDiscard()
    {
        var service = Service();
        var result = await service.CaptureAsync("sweep", "sweep", Owner, async _ =>
        {
            await service.RunChildAsync("counters", "child", _ => Task.FromResult(DiagnosticResult.Ok(Snapshot, "child")));
            return DiagnosticResult.Ok(new SweepResult(8,
                new("unknown", TriageSeverity.Healthy, new(null, null, null, null, null, null, null, null, null)),
                null, null, null, null, null, new Dictionary<string, string?> { ["missing"] = "unregistered" }, []), "sweep");
        });
        Assert.True(result.IsError);
        Assert.Contains("without a retained artifact", result.Error!.Message, StringComparison.Ordinal);
        Assert.Equal(CaptureState.Interrupted, result.Capture!.State);
        var huge = new DurableCaptureComposition([])
        {
            Metadata = new(1, new(8,
                new("unknown", TriageSeverity.Healthy, new(null, null, null, null, null, null, null, null, null)),
                null, new Dictionary<string, string?>(), [new string('x', 100_000)]), null),
        };
        Assert.Throws<InvalidDataException>(() => DurableCaptureCompositionCodec.Encode("sweep", huge, 1024));
    }

    [Fact]
    public void ParentMetadataVersionAndRequiredFieldsAreValidatedWhilePreviousCompositionRemainsReadable()
    {
        var root = new CaptureArtifactInfo(Guid.NewGuid().ToString("N"), "sweep", "sweep");
        var info = new CaptureInfo(Guid.NewGuid().ToString("N"), Owner.OwnerId, "sweep", null, At,
            CaptureState.Sealed, [root], new());
        var composition = new DurableCaptureComposition([])
        {
            Metadata = new(1, new(8, new("healthy", TriageSeverity.Healthy,
                new(null, null, null, null, null, null, null, null, null)), null,
                new Dictionary<string, string?>(), []), null),
        };
        var bytes = DurableCaptureCompositionCodec.Encode("sweep", composition, 8192);
        var node = JsonNode.Parse(bytes)!;
        node["metadata"]!["Version"] = 2;
        Assert.Throws<InvalidDataException>(() => Decode(node));
        node["metadata"]!["Version"] = 1;
        ((JsonObject)node["metadata"]!["Sweep"]!).Remove("DurationSeconds");
        Assert.Throws<JsonException>(() => Decode(node));
        var previous = JsonNode.Parse(bytes)!;
        previous["compositionVersion"] = 1;
        ((JsonObject)previous).Remove("metadata");
        Assert.Null(Decode(previous).Metadata);
        var unknown = JsonNode.Parse(bytes)!;
        unknown["metadata"]!["arbitraryPayload"] = "must not deserialize";
        Assert.Throws<JsonException>(() => Decode(unknown));
        var explicitNull = JsonNode.Parse(bytes)!;
        explicitNull["metadata"]!["Sweep"]!["Triage"] = null;
        Assert.Throws<InvalidDataException>(() => Decode(explicitNull));
        var selfReference = JsonNode.Parse(bytes)!;
        selfReference["metadata"]!["Sweep"]!["ArtifactIds"]!["self"] = root.ArtifactId;
        Assert.Throws<InvalidDataException>(() => Decode(selfReference));
        var duplicate = System.Text.Encoding.UTF8.GetString(bytes)
            .Replace("\"Version\":1,", "\"Version\":1,\"Version\":1,", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => DurableCaptureCompositionCodec.Decode("sweep",
            root.ArtifactId, new(DurableCaptureCompositionCodec.SnapshotVersion,
                System.Text.Encoding.UTF8.GetBytes(duplicate)), info, new()));

        DurableCaptureComposition Decode(JsonNode value) => DurableCaptureCompositionCodec.Decode("sweep",
            root.ArtifactId, new(DurableCaptureCompositionCodec.SnapshotVersion,
                System.Text.Encoding.UTF8.GetBytes(value.ToJsonString())), info, new());
    }
}
