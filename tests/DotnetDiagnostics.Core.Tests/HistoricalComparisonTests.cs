using System.Security.Cryptography;
using System.Text.Json;
using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.Comparison;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.UseCases;

namespace DotnetDiagnostics.Core.Tests;

public sealed class HistoricalComparisonTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "historical-tests", Guid.NewGuid().ToString("N"));
    private static readonly CaptureAccess Owner = new("historical-owner");
    private static readonly DateTimeOffset At = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly AuthorizeHistoricalCapture Allow = static (_, _, _, _) => ValueTask.CompletedTask;
    private sealed record RootProvider(string Root) : IArtifactRootProvider;
    private SqliteCaptureStore Store => new(new RootProvider(_root));
    private DurableCaptureUseCases Service => new(Store, new MemoryDiagnosticHandleStore(), new());

    private async Task<HistoricalCaptureReference> Capture(string kind, object snapshot, CaptureAccess? access = null)
    {
        await using var writer = await Store.CreateAsync(new("historical"), access ?? Owner);
        var id = writer.AddArtifact(kind, "retained");
        writer.SetSnapshot(id, CaptureArtifactCodec.FormatVersion, CaptureArtifactCodec.Encode(kind, snapshot, 8 * 1024 * 1024));
        var info = await writer.CompleteAsync();
        return new(info.CaptureId, id);
    }

    private static CounterSnapshot Counter(double value, CounterKind kind = CounterKind.Mean, string? unit = "bytes",
        double? interval = null, string name = "value") => new(17, At, TimeSpan.FromSeconds(60),
        [new("Provider", name, name, value, kind, unit) { IntervalSec = interval, DisplayRateTimeScale = TimeSpan.FromSeconds(1) }], [], []);
    private static CpuSampleTraceArtifact Cpu(long samples, CpuSampleEvidence? evidence = null, string name = "Work") =>
        new(17, At, TimeSpan.FromSeconds(60), samples,
            new(new("", "<root>"), samples, 0, [new(new("Assembly", name), samples, samples, [])]))
        { Evidence = evidence ?? CpuSampleEvidence.EventPipeSampleProfiler };
    private static HeapSnapshotArtifact Heap(long bytes, string type = "Type") => new(
        HeapSnapshotOrigin.Live, 17, At, TimeSpan.FromSeconds(1), new("CoreCLR", "10", "X64", false, 1),
        new(bytes, 0, 0, bytes, 0, 0, bytes), [new(type, "Assembly", 1, bytes, 100)], []);

    [Theory]
    [InlineData(0, 5, 5, null, "ZeroBaseline")]
    [InlineData(0, 0, 0, null, "ZeroBaseline")]
    [InlineData(10, 15, 5, .5, null)]
    [InlineData(10, 5, -5, -.5, null)]
    public async Task CounterDirectionAndZeroBaselineAreHonest(double before, double after, double absolute, double? relative, string? reason)
    {
        var a = await Capture("counters", Counter(before));
        var b = await Capture("counters", Counter(after));
        var hashes = Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".sqlite", StringComparison.Ordinal) || p.EndsWith("manifest.json", StringComparison.Ordinal))
            .ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        var result = await Service.CompareHistoricalAsync(new(a, b), Owner, Allow);
        var metric = Assert.Single(result.Metrics);
        Assert.Equal((decimal)absolute, metric.AbsoluteDelta);
        Assert.Equal(relative is null ? null : (decimal?)relative.Value, metric.RelativeDelta);
        Assert.Equal(reason, metric.UnavailableReason);
        Assert.Equal("last-interval-mean", metric.Aggregation);
        Assert.Equal(a, result.Left.Reference);
        Assert.Equal(b, result.Right.Reference);
        Assert.Equal("qualified", result.Compatibility.Status);
        foreach (var (path, hash) in hashes) Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }

    [Theory]
    [InlineData(null, "UnitUnknown")]
    [InlineData("seconds", "UnitMismatch")]
    public async Task CounterUnitsSuppressDeltas(string? unit, string reason)
    {
        var result = await Service.CompareHistoricalAsync(new(
            await Capture("counters", Counter(5)), await Capture("counters", Counter(10, unit: unit))), Owner, Allow);
        Assert.Equal(reason, Assert.Single(result.Metrics).UnavailableReason);
        Assert.Null(result.Metrics[0].AbsoluteDelta);
        Assert.Null(result.Metrics[0].Unit);
        Assert.Equal("bytes", result.Metrics[0].LeftUnit);
        Assert.Equal(unit, result.Metrics[0].RightUnit);
        Assert.Equal("incompatible", result.Compatibility.Status);
    }

    [Theory]
    [InlineData(2d, 5d)]
    [InlineData(null, null)]
    [InlineData(0d, null)]
    public async Task CounterRatesUseOnlyActualInterval(double? interval, double? expected)
    {
        var result = await Service.CompareHistoricalAsync(new(
            await Capture("counters", Counter(10, CounterKind.Sum, interval: interval)),
            await Capture("counters", Counter(20, CounterKind.Sum, interval: interval))), Owner, Allow);
        var rate = Assert.Single(result.Metrics, m => m.Key.EndsWith("/rate", StringComparison.Ordinal));
        Assert.Equal(expected is null ? null : (decimal?)expected.Value, rate.LeftValue);
        Assert.Equal(interval is null ? null : (decimal?)interval.Value, rate.LeftDenominator);
        Assert.Equal("last-interval-increment", result.Metrics[0].Aggregation);
    }

    [Fact]
    public async Task CpuCountsStayExactAndDoNotBecomeTime()
    {
        var result = await Service.CompareHistoricalAsync(new(await Capture("cpu-sample", Cpu(long.MaxValue - 1)),
            await Capture("cpu-sample", Cpu(long.MaxValue))), Owner, Allow);
        var count = result.Metrics.Single(m => m.Unit == "samples");
        Assert.Equal(1m, count.AbsoluteDelta);
        Assert.Equal((decimal)long.MaxValue, count.RightValue);
        Assert.Contains(result.Compatibility.Reasons, r => r.Code == "SamplingNotEquivalentWork");
        Assert.Equal(CpuSampleBackend.EventPipeSampleProfiler, result.Left.CpuEvidence!.Backend);
    }

    [Fact]
    public async Task CounterRoundTripPrecisionPreservesAnObservedDoubleDifference()
    {
        var result = await Service.CompareHistoricalAsync(new(await Capture("counters", Counter(10_000_000_000_000_000d)),
            await Capture("counters", Counter(10_000_000_000_000_002d))), Owner, Allow);
        Assert.Equal(2m, Assert.Single(result.Metrics).AbsoluteDelta);
    }

    [Fact]
    public async Task RelativeOverflowDoesNotEraseValidAbsoluteDelta()
    {
        var result = await Service.CompareHistoricalAsync(new(await Capture("counters", Counter(1e-28)),
            await Capture("counters", Counter(1e28))), Owner, Allow);
        var metric = Assert.Single(result.Metrics);
        Assert.NotNull(metric.AbsoluteDelta);
        Assert.Null(metric.RelativeDelta);
        Assert.Equal("RelativeNumericRangeExceeded", metric.UnavailableReason);
    }

    [Theory]
    [InlineData("negative-heap")]
    [InlineData("unknown-aggregation")]
    public async Task InvalidFamilyDefinitionsRejectInsteadOfInventingMetrics(string scenario)
    {
        var reference = scenario == "negative-heap" ? await Capture("heap-snapshot", Heap(-1))
            : await Capture("counters", Counter(1, (CounterKind)99));
        await Assert.ThrowsAsync<CaptureStoreException>(() => Service.CompareHistoricalAsync(new(reference, reference), Owner, Allow));
    }

    [Fact]
    public async Task CounterAggregationMismatchPreservesBothDefinitions()
    {
        var result = await Service.CompareHistoricalAsync(new(await Capture("counters", Counter(1)),
            await Capture("counters", Counter(2, CounterKind.Sum))), Owner, Allow);
        var value = result.Metrics[0];
        Assert.Equal("incompatible", value.Aggregation);
        Assert.Equal("last-interval-mean", value.LeftAggregation);
        Assert.Equal("last-interval-increment", value.RightAggregation);
        Assert.Null(value.AbsoluteDelta);
        Assert.Equal("incompatible", result.Compatibility.Status);
    }

    [Fact]
    public async Task RecoveryLineageAndOriginalLossRemainVisibleOnBothSides()
    {
        var writer = await Store.CreateAsync(new("interrupted comparison"), Owner);
        var artifact = writer.AddArtifact("counters", "retained");
        writer.SetSnapshot(artifact, CaptureArtifactCodec.FormatVersion, CaptureArtifactCodec.Encode("counters", Counter(3), 1024 * 1024));
        writer.SetSourceRejected(3);
        var source = writer.Reference.CaptureId;
        await writer.DisposeAsync();
        var recovered = await Store.RecoverAsync(source, Owner);
        var reference = new HistoricalCaptureReference(recovered.CaptureId, Assert.Single(recovered.Artifacts).ArtifactId);
        var result = await Service.CompareHistoricalAsync(new(reference, reference), Owner, Allow);
        Assert.Equal(source, result.Left.DerivedFrom);
        Assert.Equal(source, result.Right.DerivedFrom);
        Assert.True(result.Quality.Left.Capture.UnknownTail);
        Assert.True(result.Quality.Right.Capture.UnknownTail);
        Assert.Equal(recovered.Quality, result.Quality.Left.Capture);
        Assert.Equal("qualified", result.Compatibility.Status);
        Assert.Contains(result.Compatibility.Reasons, reason => reason.Code == "IncompleteOrUnknownCaptureQuality");
    }

    [Fact]
    public async Task HeapMetadataTokensKeepSameNamedTypesDistinct()
    {
        var snapshot = Heap(3) with
        {
            TopTypesByBytes = [
                new("Type", "Assembly", 1, 1, 0, new("Type") { ModuleName = "Assembly", MetadataToken = 1 }),
                new("Type", "Assembly", 1, 2, 0, new("Type") { ModuleName = "Assembly", MetadataToken = 2 }),
            ],
        };
        var reference = await Capture("heap-snapshot", snapshot);
        var result = await Service.CompareHistoricalAsync(new(reference, reference), Owner, Allow);
        Assert.Equal(4, result.Metrics.Count);
        Assert.Equal(4, result.Metrics.Select(m => m.Key).Distinct().Count());
        Assert.All(result.Metrics, metric => Assert.Equal(0m, metric.AbsoluteDelta));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HeapOriginAndArchitectureAreRequired(bool changeOrigin)
    {
        var a = Heap(1);
        var b = changeOrigin ? a with { Origin = HeapSnapshotOrigin.Dump }
            : a with { Runtime = a.Runtime with { Architecture = "unknown" } };
        var result = await Service.CompareHistoricalAsync(new(await Capture("heap-snapshot", a),
            await Capture("heap-snapshot", b)), Owner, Allow);
        Assert.Equal("incompatible", result.Compatibility.Status);
        Assert.All(result.Metrics, metric => Assert.Null(metric.AbsoluteDelta));
    }

    [Fact]
    public async Task CpuBackendMismatchSuppressesEveryDelta()
    {
        var result = await Service.CompareHistoricalAsync(new(await Capture("cpu-sample", Cpu(5)),
            await Capture("cpu-sample", Cpu(10, CpuSampleEvidence.LinuxPerfOnCpu))), Owner, Allow);
        Assert.Equal("incompatible", result.Compatibility.Status);
        Assert.All(result.Metrics, m => Assert.Null(m.AbsoluteDelta));
    }

    [Fact]
    public async Task ZeroSamplePopulationHasNoInventedFraction()
    {
        var result = await Service.CompareHistoricalAsync(new(await Capture("cpu-sample", Cpu(0)),
            await Capture("cpu-sample", Cpu(0))), Owner, Allow);
        var fraction = result.Metrics.Single(m => m.Unit == "fraction");
        Assert.Null(fraction.LeftValue);
        Assert.Equal("SampleDenominatorUnavailable", fraction.UnavailableReason);
    }

    [Fact]
    public async Task HeapLongArithmeticAndMissingRetainedTypesDoNotInventAbsence()
    {
        var a = await Capture("heap-snapshot", Heap(long.MaxValue - 1));
        var b = await Capture("heap-snapshot", Heap(long.MaxValue));
        var result = await Service.CompareHistoricalAsync(new(a, b), Owner, Allow);
        Assert.Equal(1m, result.Metrics.Single(m => m.Unit == "bytes").AbsoluteDelta);
        var missing = await Service.CompareHistoricalAsync(new(a, await Capture("heap-snapshot", Heap(5, "Different"))), Owner, Allow);
        Assert.All(missing.Metrics, m => { Assert.Null(m.AbsoluteDelta); Assert.Equal("MissingObservation", m.UnavailableReason); });
    }

    [Fact]
    public async Task DenialAndRevocationExposeNoResultAndReleaseBothLeases()
    {
        var a = await Capture("counters", Counter(1));
        var b = await Capture("counters", Counter(2));
        var calls = 0;
        var error = await Assert.ThrowsAsync<CaptureStoreException>(() => Service.CompareHistoricalAsync(new(a, b), Owner,
            (capture, artifact, view, token) =>
            {
                Assert.Equal("diff", view);
                if (++calls == 4) throw new CaptureStoreException(CaptureErrorCode.Forbidden, "Revoked");
                return ValueTask.CompletedTask;
            }));
        Assert.Equal(CaptureErrorCode.Forbidden, error.Code);
        Assert.Equal(4, calls);
        await Store.DeleteAsync(a.CaptureId, Owner);
        await Store.DeleteAsync(b.CaptureId, Owner);
    }

    [Fact]
    public async Task UnauthorizedAndMissingInputsReturnTheSameNonDisclosingFailure()
    {
        var a = await Capture("counters", Counter(1));
        var b = await Capture("counters", Counter(2), new("other"));
        var denied = await Assert.ThrowsAsync<CaptureStoreException>(() => Service.CompareHistoricalAsync(new(a, b), Owner, Allow));
        var absent = await Assert.ThrowsAsync<CaptureStoreException>(() => Service.CompareHistoricalAsync(
            new(a, new(new string('0', 32), new string('1', 32))), Owner, Allow));
        Assert.Equal(denied.Code, absent.Code);
        Assert.Equal(denied.Message, absent.Message);
        Assert.DoesNotContain(a.CaptureId, denied.Message);
    }

    [Fact]
    public async Task TraversalBudgetRejectsBeforeProducingPartialMetrics()
    {
        var large = Cpu(1001) with { Root = new(new("", "<root>"), 1001, 0,
            Enumerable.Range(0, 1001).Select(i => new CallTreeNode(new("Assembly", $"Work{i}"), 1, 1, [])).ToArray()) };
        var a = await Capture("cpu-sample", large);
        var b = await Capture("cpu-sample", Cpu(1));
        var error = await Assert.ThrowsAsync<CaptureStoreException>(() => Service.CompareHistoricalAsync(new(a, b), Owner, Allow));
        Assert.Equal(CaptureErrorCode.CapacityExceeded, error.Code);
        Assert.Contains("ComparisonSideRows", error.Message);
    }

    [Fact]
    public async Task CancellationBeforeProjectionReleasesReaders()
    {
        var a = await Capture("counters", Counter(1));
        var b = await Capture("counters", Counter(2));
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.CompareHistoricalAsync(new(a, b), Owner,
            (_, _, _, token) => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }, cancellation.Token));
        await Store.DeleteAsync(a.CaptureId, Owner);
        await Store.DeleteAsync(b.CaptureId, Owner);
    }

    [Fact]
    public void JsonContractRejectsUnsupportedFilters()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
            """{"baseline":{"captureId":"a","artifactId":"b","from":"2026-01-01"},"candidate":{"captureId":"c","artifactId":"d"}}""",
            HistoricalComparisonJsonContext.Default.HistoricalComparisonRequest));
    }

    [Theory]
    [InlineData("source-bytes")]
    [InlineData("result-bytes")]
    [InlineData("metrics")]
    public async Task BoundsRejectWholeComparisonWithoutTruncation(string bound)
    {
        var count = bound == "source-bytes" ? 900 : 600;
        var nameLength = bound == "source-bytes" ? 1600 : bound == "result-bytes" ? 850 : 10;
        CounterSnapshot Snapshot(string prefix) => new(1, At, TimeSpan.FromSeconds(1),
            Enumerable.Range(0, count).Select(i => new CounterValue("Provider",
                prefix + i + new string('x', nameLength), "", 1, bound == "metrics" ? CounterKind.Sum : CounterKind.Mean, "bytes")
                { IntervalSec = 1, DisplayRateTimeScale = TimeSpan.FromSeconds(1) }).ToArray(), [], []);
        var a = await Capture("counters", Snapshot("a"));
        var b = await Capture("counters", Snapshot("b"));
        var error = await Assert.ThrowsAsync<CaptureStoreException>(() => Service.CompareHistoricalAsync(new(a, b), Owner, Allow));
        Assert.Equal(CaptureErrorCode.CapacityExceeded, error.Code);
        if (bound == "metrics") Assert.Equal("ComparisonMetrics", error.Message);
        if (bound == "result-bytes") Assert.Equal("ComparisonResultBytes", error.Message);
    }

    [Fact]
    public async Task ReadersStayLeasedUntilBothAuthorizationPassesFinish()
    {
        var a = await Capture("counters", Counter(1));
        var b = await Capture("counters", Counter(2));
        var checkedBoth = 0;
        await Service.CompareHistoricalAsync(new(a, b), Owner, async (_, _, _, _) =>
        {
            foreach (var reference in new[] { a, b })
            {
                var busy = await Assert.ThrowsAsync<CaptureStoreException>(() => Store.DeleteAsync(reference.CaptureId, Owner));
                Assert.Equal(CaptureErrorCode.Busy, busy.Code);
                checkedBoth++;
            }
        });
        Assert.Equal(8, checkedBoth);
        await Store.DeleteAsync(a.CaptureId, Owner);
        await Store.DeleteAsync(b.CaptureId, Owner);
    }

    [Fact]
    public async Task ComparisonSqliteProgressInterruptsValidationAndReleasesLease()
    {
        await using var writer = await Store.CreateAsync(new("progress"), Owner);
        var artifact = writer.AddArtifact("counters", "rows");
        for (var i = 0; i < 1000; i++) Assert.True(writer.TryAppend(artifact, new(Name: "row", NumericValue: i)));
        var info = await writer.CompleteAsync();
        var callbacks = 0;
        var error = await Assert.ThrowsAsync<CaptureStoreException>(() => Store.OpenBoundedAsync(info.CaptureId, Owner,
            () => { if (callbacks > 0) throw new CaptureStoreException(CaptureErrorCode.CapacityExceeded, "test-interrupt"); },
            () => { callbacks++; return 1; }, CancellationToken.None));
        Assert.Equal("test-interrupt", error.Message);
        Assert.True(callbacks > 0);
        await Store.DeleteAsync(info.CaptureId, Owner);
    }

    [Fact]
    public async Task BoundedHashKeepsIntegrityAndHonorsMidReadCancellation()
    {
        var a = await Capture("counters", Counter(1));
        var file = Directory.GetFiles(_root, "*.sqlite", SearchOption.AllDirectories).Single();
        Assert.Equal(CapturePackage.Hash(file), CapturePackage.Hash(file, static () => { }));
        var checks = 0;
        Assert.Throws<OperationCanceledException>(() => CapturePackage.Hash(file, () =>
        {
            if (++checks == 2) throw new OperationCanceledException();
        }));
        Assert.Equal(2, checks);
        await Store.DeleteAsync(a.CaptureId, Owner);
    }

    [Fact]
    public void ExaminedWorkBudgetIncludesSqliteInstructions()
    {
        var budget = new HistoricalComparisonBudget(CancellationToken.None);
        for (var i = 0; i < 10000; i++) Assert.Equal(0, budget.SqliteProgress());
        Assert.Equal(1, budget.SqliteProgress());
        Assert.Equal(CaptureErrorCode.CapacityExceeded, Assert.Throws<CaptureStoreException>(budget.Check).Code);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
