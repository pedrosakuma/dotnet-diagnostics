using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.EventSources;
using DotnetDiagnostics.Core.Security;

namespace DotnetDiagnostics.Core.Tests;

public sealed partial class DurableCaptureUseCasesTests
{
    [Fact]
    public async Task EventSourceDurableCloneMasksCredentialsWithoutMutatingOriginalEnvelopeOrHandle()
    {
        var fields = new Dictionary<string, string>
        {
            ["AccessToken"] = "opaque-short",
            ["Password"] = "p",
            ["ordinary"] = Rich,
        };
        var snapshot = new EventSourceCapture(42, "custom-provider", At, TimeSpan.FromSeconds(1), 1,
            [new(At, "custom-provider", "event", "Information", fields)]);
        var service = Service();
        var result = await service.CaptureAsync("provider", "event_source", Owner, _ =>
        {
            var handle = _handles.RegisterWithMetadata(42, "event-source", snapshot, TimeSpan.FromMinutes(1),
                producingTool: "collect_events");
            return Task.FromResult(DiagnosticResult.OkWithHandle(snapshot, "done", handle.Id, handle.ExpiresAt));
        });
        Assert.False(result.IsError, result.Error?.Message);
        Assert.Same(snapshot, result.Data);
        Assert.Same(snapshot, _handles.TryGet<EventSourceCapture>(result.Handle!));
        Assert.Equal("opaque-short", snapshot.Events[0].Payload["AccessToken"]);
        Assert.Equal("p", snapshot.Events[0].Payload["Password"]);
        var info = result.Capture!;
        var artifact = Assert.Single(info.Artifacts);
        var reopened = await service.OpenAsync(info.CaptureId, artifact.ArtifactId, Owner);
        var restored = _handles.TryGet<EventSourceCapture>(reopened.Handle.Id)!;
        Assert.Equal(SensitiveDataRedactor.RedactedPlaceholder, restored.Events[0].Payload["AccessToken"]);
        Assert.Equal(SensitiveDataRedactor.RedactedPlaceholder, restored.Events[0].Payload["Password"]);
        Assert.Equal(Rich, restored.Events[0].Payload["ordinary"]);
        using var reader = await Store().OpenAsync(info.CaptureId, Owner);
        var json = System.Text.Encoding.UTF8.GetString(reader.ReadSnapshot(artifact.ArtifactId)!.Utf8Json.Span);
        Assert.DoesNotContain("opaque-short", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedEventSourceCloneFailsBeforePersistenceWithoutMutatingOriginal()
    {
        var payload = new Dictionary<string, string> { ["Password"] = new('x', 10_000) };
        var original = new EventSourceCapture(42, "provider", At, TimeSpan.FromSeconds(1), 1,
            [new(At, "provider", "event", "Information", payload)]);
        var result = await Service(new() { MaxSnapshotBytes = 1024 }).CaptureAsync("oversized", "event_source",
            Owner, _ => Task.FromResult(DiagnosticResult.Ok(original, "done")));
        Assert.True(result.IsError);
        Assert.Equal(CaptureState.Interrupted, result.Capture!.State);
        Assert.Same(original, result.Data);
        Assert.Equal(10_000, payload["Password"].Length);
    }
}
