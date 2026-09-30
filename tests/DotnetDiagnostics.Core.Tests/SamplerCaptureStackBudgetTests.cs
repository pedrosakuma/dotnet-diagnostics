using System.Text.Json;
using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.CaptureRecording;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using static DotnetDiagnostics.Core.Tests.SamplerCaptureObservationTests;

namespace DotnetDiagnostics.Core.Tests;

public sealed class SamplerCaptureStackBudgetTests
{
    private static readonly (string Key, string Module, string Display)[] Stack =
    [
        ("模块!类型.方法", "模块", "类型.方法"),
        ("root!Root", "root", "Root"),
    ];

    [Fact]
    public async Task RepeatedStacks_HaveOneDiscoverableDefinitionAndEveryOccurrenceDimension()
    {
        var sink = new Sink();
        var replay = new CpuReplayStackObservationWriter(sink);
        for (var i = 0; i < 100; i++)
            Assert.True(await replay.AppendAsync(i % 3, i * 1.5, Stack, CancellationToken.None));
        var definition = Assert.Single(sink.Rows, row => row.Category == CpuReplayStackObservationWriter.DefinitionCategory);
        Assert.False(Field(definition, "sourceOccurrence").Boolean);
        Assert.Null(definition.Timestamp);
        Assert.Null(definition.ThreadId);
        Assert.Equal("类型.方法", Field(definition, "method").Text);
        Assert.Equal("trace-relative-seconds", Field(definition, "sourceClock").Text);
        Assert.Equal("sample-profiler-thread-sample-not-proven-on-cpu", Field(definition, "evidence").Text);
        using var stack = JsonDocument.Parse(Field(definition, "stack").Text!);
        Assert.Equal("类型.方法", stack.RootElement[0].GetProperty("method").GetString());
        Assert.Equal("Root", stack.RootElement[1].GetProperty("method").GetString());
        Assert.Equal(JsonValueKind.Null, stack.RootElement[0].GetProperty("identity").ValueKind);
        var rows = sink.Rows.Where(row => row.Category == CpuReplayStackObservationWriter.SampleCategory).ToArray();
        Assert.Equal(100, rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            Assert.Equal(definition.Name, rows[i].Name);
            Assert.Equal(i % 3, rows[i].ThreadId);
            Assert.Null(rows[i].Timestamp);
            Assert.Equal(i * 1.5 / 1000, Field(rows[i], "sourceSeconds").Number);
            Assert.Equal(1, Field(rows[i], "weight").Integer);
            Assert.True(Field(rows[i], "sourceOccurrence").Boolean);
        }
        Assert.Equal(1, replay.DefinitionCount);
        Assert.InRange(replay.CacheBytes, 1, CpuReplayStackObservationWriter.MaximumStackCacheBytes);
        Assert.Empty(replay.GetNotes());
    }

    [Theory]
    [InlineData(1, 8388608)]
    [InlineData(4096, 1)]
    public async Task CacheSaturation_IsInsertionBoundedWithExplicitInlineFallback(int entries, long bytes)
    {
        var sink = new Sink();
        var replay = new CpuReplayStackObservationWriter(sink, entries, bytes);
        Assert.True(await replay.AppendAsync(1, 1, Stack, CancellationToken.None));
        Assert.True(await replay.AppendAsync(2, 2, [("different", "m", "different")], CancellationToken.None));
        Assert.True(await replay.AppendAsync(3, 3, Stack, CancellationToken.None));
        Assert.True(replay.DefinitionCount <= entries);
        Assert.True(replay.CacheBytes <= bytes);
        Assert.Contains(sink.Rows, row => row.Fields.Any(f => f.Name == "stackReferenceFallback" && f.Text == "stack-cache-saturated"));
        Assert.Single(replay.GetNotes());
        Assert.Equal(3, sink.Rows.Count(row => Field(row, "sourceOccurrence").Boolean));
    }

    [Fact]
    public async Task RejectedDefinition_UsesSelfContainedFallbackAndNeverReferencesRejectedId()
    {
        var sink = new Sink { RejectDefinitions = true };
        var replay = new CpuReplayStackObservationWriter(sink);
        Assert.True(await replay.AppendAsync(1, 1, Stack, CancellationToken.None));
        Assert.Equal(1, sink.Rejections);
        var fallback = Assert.Single(sink.Rows);
        Assert.Equal("sample.cpu.eventpipe", fallback.Category);
        Assert.Equal("stack-definition-rejected", Field(fallback, "stackReferenceFallback").Text);
        Assert.NotNull(Field(fallback, "stack").Text);
        Assert.Equal(0, replay.DefinitionCount);
        Assert.Equal(0, replay.CacheBytes);
        Assert.Single(replay.GetNotes());
    }

    [Fact]
    public async Task RejectedSample_IsNotRetriedAndItsAdmittedDefinitionRemainsUsable()
    {
        var sink = new Sink { RejectSamples = true };
        var replay = new CpuReplayStackObservationWriter(sink);
        Assert.False(await replay.AppendAsync(1, 1, Stack, CancellationToken.None));
        Assert.Equal(1, sink.Rejections);
        Assert.Single(sink.Rows);
        sink.RejectSamples = false;
        Assert.True(await replay.AppendAsync(2, 2, Stack, CancellationToken.None));
        Assert.Equal(1, replay.DefinitionCount);
        Assert.Equal(sink.Rows[0].Name, sink.Rows[1].Name);
    }

    [Fact]
    public async Task CancellationBeforeDefinitionAdmission_DoesNotPublishReferenceOrCacheEntry()
    {
        var sink = new Sink { HoldDefinition = true };
        var replay = new CpuReplayStackObservationWriter(sink);
        using var cancellation = new CancellationTokenSource();
        var pending = replay.AppendAsync(1, 1, Stack, cancellation.Token).AsTask();
        await sink.DefinitionEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(sink.Rows);
        Assert.Equal(0, replay.DefinitionCount);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(sink.Rows);
        Assert.Equal(0, replay.DefinitionCount);
    }

    [Fact]
    public async Task SeparateReplayInvocations_NeverReuseAReferenceName()
    {
        var sink = new Sink();
        await new CpuReplayStackObservationWriter(sink).AppendAsync(1, 1, Stack, CancellationToken.None);
        await new CpuReplayStackObservationWriter(sink).AppendAsync(1, 1, Stack, CancellationToken.None);
        var definitions = sink.Rows.Where(r => r.Category == CpuReplayStackObservationWriter.DefinitionCategory).ToArray();
        Assert.Equal(2, definitions.Length);
        Assert.NotEqual(definitions[0].Name, definitions[1].Name);
    }

    [Fact]
    public async Task CancellationAfterDefinitionAdmission_LeavesOnlyAnUnusedDefinition()
    {
        using var cancellation = new CancellationTokenSource();
        var sink = new Sink { CancelAfterDefinition = cancellation };
        var replay = new CpuReplayStackObservationWriter(sink);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await replay.AppendAsync(1, 1, Stack, cancellation.Token));
        Assert.Equal(CpuReplayStackObservationWriter.DefinitionCategory, Assert.Single(sink.Rows).Category);
        Assert.Equal(1, replay.DefinitionCount);
    }

    [Fact]
    public async Task DistinctFramesWithEqualLeafNames_KeepSeparateOrderedStackDefinitions()
    {
        var sink = new Sink();
        var replay = new CpuReplayStackObservationWriter(sink);
        await replay.AppendAsync(1, 1, Stack, CancellationToken.None);
        await replay.AppendAsync(1, 2, [Stack[0], ("different", "root", "DifferentCaller")], CancellationToken.None);
        Assert.Equal(2, replay.DefinitionCount);
        var definitions = sink.Rows.Where(r => r.Category == CpuReplayStackObservationWriter.DefinitionCategory).ToArray();
        Assert.Equal(Field(definitions[0], "method").Text, Field(definitions[1], "method").Text);
        Assert.NotEqual(Field(definitions[0], "stack").Text, Field(definitions[1], "stack").Text);
        Assert.NotEqual(definitions[0].Name, definitions[1].Name);
    }

    [Fact]
    public async Task TruncatedStack_FallsBackWithoutTreatingEqualPrefixesAsCompleteDefinitions()
    {
        var sink = new Sink();
        var replay = new CpuReplayStackObservationWriter(sink);
        Assert.True(await replay.AppendAsync(1, 1, Enumerable.Repeat(Stack[0], 129).ToArray(), CancellationToken.None));
        var row = Assert.Single(sink.Rows);
        Assert.True(Field(row, "stackTruncated").Boolean);
        Assert.Equal("stack-encoding-truncated", Field(row, "stackReferenceFallback").Text);
        Assert.Equal(0, replay.DefinitionCount);
        Assert.Single(replay.GetNotes());
    }

    [Fact(Timeout = 90_000)]
    public async Task DefaultStore_ReopensEightyThousandExactSamplesWithIndexedArtifactLocalDefinitions()
    {
        const int count = 80000;
        var root = Path.Combine(AppContext.BaseDirectory, "cpu-stack-budget-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new CaptureStoreOptions();
            var owner = new CaptureAccess("stack-reference-test");
            var store = new SqliteCaptureStore(new RootProvider(root), options);
            await using var writer = await store.CreateAsync(new("normalized CPU"), owner);
            var artifact = writer.AddArtifact("cpu-sample", "cpu");
            var otherArtifact = writer.AddArtifact("cpu-sample", "other");
            var sink = new SqliteCaptureObservationSink(writer, artifact, "cpu-sample", "cpu", options);
            var replay = new CpuReplayStackObservationWriter(sink);
            for (var i = 0; i < count; i++)
                Assert.True(await replay.AppendAsync(i % 4, i, Stack, CancellationToken.None));
            writer.SetSourceRejected(0);
            var info = await writer.CompleteAsync();
            Assert.Equal(count + 1, info.Quality.Persisted);
            Assert.True(info.Quality.IsComplete);
            Assert.True(writer.GetMetrics().LogicalBytes < options.MaxLogicalBytes / 2);
            using var reader = await new SqliteCaptureStore(new RootProvider(root), options).OpenAsync(info.CaptureId, owner);
            var definition = Assert.Single(reader.Query(new(artifact, Category: CpuReplayStackObservationWriter.DefinitionCategory)).Records).Record;
            var key = definition.Name;
            Assert.NotNull(key);
            Assert.Single(reader.Query(new(artifact, Category: CpuReplayStackObservationWriter.DefinitionCategory, Name: key)).Records);
            Assert.Empty(reader.Query(new(otherArtifact, Category: CpuReplayStackObservationWriter.DefinitionCategory, Name: key)).Records);
            var seen = 0;
            long after = 0;
            while (true)
            {
                var page = reader.Query(new(artifact, Category: CpuReplayStackObservationWriter.SampleCategory, AfterRecordId: after, PageSize: 1000));
                foreach (var row in page.Records)
                {
                    Assert.Equal(key, row.Record.Name);
                    Assert.Null(row.Record.Timestamp);
                    Assert.Equal(seen % 4, row.Record.ThreadId);
                    Assert.Equal(seen / 1000.0, Assert.Single(row.Record.Fields!, f => f.Name == "sourceSeconds").DoubleValue);
                    Assert.Equal(1, Assert.Single(row.Record.Fields!, f => f.Name == "weight").Int64Value);
                    seen++;
                }
                if (page.NextAfterRecordId is not { } next) break;
                after = next;
            }
            Assert.Equal(count, seen);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed record RootProvider(string Root) : IArtifactRootProvider;

    private sealed class Sink : IReplayCaptureObservationSink
    {
        internal List<CaptureObservation> Rows { get; } = [];
        internal bool RejectDefinitions { get; init; }
        internal bool RejectSamples { get; set; }
        internal bool HoldDefinition { get; init; }
        internal int Rejections { get; private set; }
        internal CancellationTokenSource? CancelAfterDefinition { get; init; }
        internal TaskCompletionSource DefinitionEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<bool> AppendReplayAsync(CaptureObservation observation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var definition = observation.Category == CpuReplayStackObservationWriter.DefinitionCategory;
            if (definition && HoldDefinition)
            {
                DefinitionEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            if ((definition && RejectDefinitions) || (!definition && RejectSamples)) { Rejections++; return false; }
            Rows.Add(observation);
            if (definition && CancelAfterDefinition is not null) await CancelAfterDefinition.CancelAsync();
            return true;
        }
        public bool TryAppend(CaptureObservation observation) => throw new InvalidOperationException("Offline replay must not use live admission.");
        public void ReportSourceLoss(string source, long? count) { }
        public void ArtifactRegistered(DiagnosticHandle handle, object artifact) { }
    }
}
