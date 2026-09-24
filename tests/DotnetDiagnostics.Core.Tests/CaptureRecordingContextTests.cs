using DotnetDiagnostics.Core.CaptureRecording;
using DotnetDiagnostics.Core.Drilldown;

namespace DotnetDiagnostics.Core.Tests;

public sealed class CaptureRecordingContextTests
{
    [Fact]
    public void NestedScopesRestorePreviousInvocation()
    {
        Assert.Null(CaptureRecordingContext.Current);
        var first = new Sink();
        var second = new Sink();
        using (CaptureRecordingContext.Enter(first))
        {
            Assert.Same(first, CaptureRecordingContext.Current);
            using (CaptureRecordingContext.Enter(second))
                Assert.Same(second, CaptureRecordingContext.Current);
            Assert.Same(first, CaptureRecordingContext.Current);
        }
        Assert.Null(CaptureRecordingContext.Current);
    }

    [Fact]
    public async Task ConcurrentInvocationsDoNotShareSinkState()
    {
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            var sink = new Sink();
            using (CaptureRecordingContext.Enter(sink))
            {
                await Task.Yield();
                Assert.Same(sink, CaptureRecordingContext.Current);
                var store = new MemoryDiagnosticHandleStore();
                var artifact = new object();
                var handle = store.RegisterWithMetadata(42, "test", artifact, TimeSpan.FromMinutes(1),
                    producingTool: "collect_events");
                Assert.Equal(handle, Assert.Single(sink.Registrations).Handle);
                Assert.Same(artifact, sink.Registrations[0].Artifact);
            }
            Assert.Null(CaptureRecordingContext.Current);
        }));
        Assert.Null(CaptureRecordingContext.Current);
    }

    [Fact]
    public async Task ChildContextDisposalDoesNotPoisonParentScope()
    {
        var sink = new Sink();
        var scope = CaptureRecordingContext.Enter(sink);
        await Task.Run(scope.Dispose);
        Assert.Same(sink, CaptureRecordingContext.Current);
        scope.Dispose();
        Assert.Null(CaptureRecordingContext.Current);
    }

    [Fact]
    public void UnorderedDisposalFailsWithoutLosingOuterScope()
    {
        var outer = CaptureRecordingContext.Enter(new Sink());
        var inner = CaptureRecordingContext.Enter(new Sink());
        Assert.Throws<InvalidOperationException>(outer.Dispose);
        inner.Dispose();
        outer.Dispose();
        outer.Dispose();
        Assert.Null(CaptureRecordingContext.Current);
    }

    [Fact]
    public void MissingAndScalarValuesRemainDistinct()
    {
        Assert.Equal(CaptureObservationValueKind.Null, CaptureObservationField.String("missing", null).Kind);
        var empty = CaptureObservationField.String("empty", "");
        Assert.Equal(CaptureObservationValueKind.String, empty.Kind);
        Assert.Equal("", empty.Text);
        Assert.Equal(long.MaxValue, CaptureObservationField.Int64("integer", long.MaxValue).Integer);
        Assert.Equal(0.25, CaptureObservationField.Double("number", 0.25).Number);
        Assert.False(CaptureObservationField.Bool("boolean", false).Boolean);
        Assert.Equal("λ/日本語", CaptureObservationField.String("text", "λ/日本語").Text);
    }

    private sealed class Sink : ICaptureObservationSink
    {
        internal List<(DiagnosticHandle Handle, object Artifact)> Registrations { get; } = [];
        public bool TryAppend(CaptureObservation observation) => throw new NotSupportedException();
        public void ReportSourceLoss(string source, long? count) => throw new NotSupportedException();
        public void ArtifactRegistered(DiagnosticHandle handle, object artifact) => Registrations.Add((handle, artifact));
    }
}
