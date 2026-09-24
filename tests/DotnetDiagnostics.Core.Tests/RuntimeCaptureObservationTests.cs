using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.CaptureRecording;
using DotnetDiagnostics.Core.Contention;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Exceptions;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Internal;
using DotnetDiagnostics.Core.Logs;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.ThreadPool;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace DotnetDiagnostics.Core.Tests;

public sealed class RuntimeCaptureObservationTests
{
    private const string TraceId = "abcdef0123456789abcdef0123456789";
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch.AddDays(1);

    [Fact]
    public void CounterParsingAndAggregation_RecordsEveryIntervalRatherThanLatestOrMaximum()
    {
        var sink = new BoundedSink(10);
        var latest = new ConcurrentDictionary<string, CounterValue>();
        var first = new ConcurrentDictionary<string, CounterValue>();
        var maximum = new ConcurrentDictionary<string, CounterValue>();
        double[] increments = [12, 40, 3];
        for (var i = 0; i < increments.Length; i++)
        {
            var value = EventPipeCounterCollector.ExtractCounterPayload("注文", new Dictionary<string, object>
            {
                ["Name"] = "请求/☃", ["DisplayName"] = "请求数 🧪", ["DisplayUnits"] = "件",
                ["Increment"] = increments[i], ["IntervalSec"] = 2.5,
                ["DisplayRateTimeScale"] = "00:00:01",
            })!;
            EventPipeCounterCollector.ObserveCounter(value, latest, first, maximum, sink,
                Start.AddMilliseconds(i * 2500), i * 2500);
        }

        latest.Single().Value.Value.Should().Be(3);
        first.Single().Value.Value.Should().Be(12);
        maximum.Single().Value.Value.Should().Be(40);
        sink.Observations.Select(o => Field(o, "value").Number).Should().Equal(increments);
        sink.Observations.Select(o => Field(o, "firstObservedSample").Boolean).Should().Equal(true, false, false);
        sink.Observations.Select(o => o.Timestamp).Should().Equal(Start, Start.AddMilliseconds(2500), Start.AddMilliseconds(5000));
        Field(sink.Observations[1], "displayName").Text.Should().Be("请求数 🧪");
        Field(sink.Observations[1], "intervalSeconds").Number.Should().Be(2.5);
        Field(sink.Observations[1], "ratePerSecond").Number.Should().Be(16);
        Field(sink.Observations[1], "unit").Text.Should().Be("件");
    }

    [Fact]
    public void CounterMissingIntervalAndClock_RemainNull_AndRejectionDoesNotChangeAggregates()
    {
        var sink = new BoundedSink(0);
        var recorded = CounterState();
        var baseline = CounterState();
        var value = EventPipeCounterCollector.ExtractCounterPayload("meter", new Dictionary<string, object>
        {
            ["Name"] = "mean", ["Mean"] = 12.5,
        })!;
        EventPipeCounterCollector.ObserveCounter(value, recorded.Latest, recorded.First, recorded.Maximum, sink);
        EventPipeCounterCollector.ObserveCounter(value, baseline.Latest, baseline.First, baseline.Maximum);
        recorded.Should().BeEquivalentTo(baseline);
        sink.Rejected.Should().Be(1);
        sink.Observations.Should().BeEmpty();

        var accepted = new BoundedSink(1);
        EventPipeCounterCollector.ObserveCounter(value, recorded.Latest, recorded.First, recorded.Maximum, accepted);
        accepted.Observations[0].Timestamp.Should().BeNull();
        Field(accepted.Observations[0], "intervalSeconds").Kind.Should().Be(CaptureObservationValueKind.Null);
        Field(accepted.Observations[0], "ratePerSecond").Kind.Should().Be(CaptureObservationValueKind.Null);
        Field(accepted.Observations[0], "relativeMilliseconds").Kind.Should().Be(CaptureObservationValueKind.Null);
    }

    [Fact]
    public void MeterSeriesAdmission_IsACollectionPolicy_NotBypassedByObservationRecording()
    {
        var sink = new BoundedSink(10);
        var latest = new ConcurrentDictionary<string, MeterInstrumentValue>();
        var notes = new ConcurrentDictionary<string, byte>();
        var accepted = 0;
        var redactor = new SensitiveDataRedactor();
        var value = new MeterInstrumentValue("注文", "histogram-λ", "秒", "Histogram",
            new Dictionary<string, string?> { ["label"] = "重复", ["missing"] = null, ["api_key"] = "credential" },
            null, null, new HistogramSnapshot(7, 12.5, 1.5, 2, 3));
        EventPipeCounterCollector.ObserveMeter("first", value, latest, 1, ref accepted, notes, sink, Start, 100, redactor);
        EventPipeCounterCollector.ObserveMeter("second", value with { Instrument = "excluded" }, latest, 1,
            ref accepted, notes, sink, Start, 101, redactor);
        EventPipeCounterCollector.ObserveMeter("first", value with { Histogram = new(9, 20.5, 2, 3, 4) }, latest,
            1, ref accepted, notes, sink, Start.AddSeconds(1), 1100, redactor);

        accepted.Should().Be(1);
        latest.Should().ContainSingle();
        notes.Keys.Should().ContainSingle().Which.Should().Contain("TimeSeriesLimitReached");
        sink.Observations.Should().HaveCount(2);
        sink.Observations.Select(o => Field(o, "histogramCount").Integer).Should().Equal(7, 9);
        Field(sink.Observations[0], "tag.label").Text.Should().Be("重复");
        Field(sink.Observations[0], "tag.missing").Kind.Should().Be(CaptureObservationValueKind.Null);
        Field(sink.Observations[0], "tag.api_key").Text.Should().Be(SensitiveDataRedactor.RedactedPlaceholder);
        Field(sink.Observations[0], "lastValue").Kind.Should().Be(CaptureObservationValueKind.Null);
    }

    [Fact]
    public void CounterNullNumericPayload_DoesNotArchiveLegacyConversionAsZero()
    {
        var value = EventPipeCounterCollector.ExtractCounterPayload("test", new Dictionary<string, object>
        {
            ["Name"] = "missing", ["Increment"] = null!,
        }, out var available)!;
        available.Should().BeFalse();
        value.Value.Should().Be(0, "the legacy ephemeral result stays unchanged");
        var state = CounterState();
        var sink = new BoundedSink(1);
        EventPipeCounterCollector.ObserveCounter(value, state.Latest, state.First, state.Maximum,
            sink, valueAvailable: available);
        Field(sink.Observations[0], "value").Kind.Should().Be(CaptureObservationValueKind.Null);
    }

    [Fact]
    public void ActivityParsing_AppliesSourceAndTraceFiltersBeforeRecording_AndRecordsBeyondRetention()
    {
        var sink = new BoundedSink(10);
        var recorded = new ActivityRetentionState(1, TraceId, 1, sink);
        var baseline = new ActivityRetentionState(1, TraceId, 1);
        var arguments = ActivityArguments();
        EventPipeActivityCollector.TryCreateActivity("Excluded", "secret", arguments, Start.UtcDateTime,
            ["Orders.*"], out _, out _).Should().BeFalse();

        for (var i = 0; i < 4; i++)
        {
            arguments["TraceId"] = i == 0 ? "ffffffffffffffffffffffffffffffff" : TraceId;
            EventPipeActivityCollector.TryCreateActivity("Orders.订单", i < 3 ? "重复 🧪" : "unique-λ",
                arguments, Start.AddSeconds(1).UtcDateTime, ["Orders.*"], out var activity, out var hasStart).Should().BeTrue();
            recorded.Observe(activity, Start.AddSeconds(1), hasStart);
            baseline.Observe(activity, Start.AddSeconds(1), hasStart);
        }

        recorded.Retention.Should().Be(baseline.Retention);
        recorded.Activities.Should().Equal(baseline.Activities);
        recorded.Retention.DroppedMatchingActivities.Should().Be(2);
        sink.Observations.Select(o => o.Name).Should().Equal("重复 🧪", "重复 🧪", "unique-λ");
        var observation = sink.Observations[0];
        Field(observation, "traceId").Text.Should().Be(TraceId);
        Field(observation, "spanId").Text.Should().Be("0123456789abcdef");
        Field(observation, "parentSpanId").Kind.Should().Be(CaptureObservationValueKind.Null);
        Field(observation, "durationTicks").Integer.Should().Be(1000);
        Field(observation, "tag.label").Text.Should().Be("重复 λ 🧪");
        Field(observation, "tag.http.request.header.authorization").Text.Should().Be(SensitiveDataRedactor.RedactedPlaceholder);
        Field(observation, "tag.db.statement").Text.Should().NotContain("alice");
        Field(observation, "tag.db.statement").Text.Should().Contain("SELECT");
    }

    [Fact]
    public void ActivityMissingStart_DoesNotArchiveFallbackAsARealStart()
    {
        var sink = new BoundedSink(1);
        var arguments = ActivityArguments();
        arguments.Remove("StartTimeTicks");
        EventPipeActivityCollector.TryCreateActivity("Orders.订单", "operation", arguments, Start.UtcDateTime,
            null, out var activity, out var hasStart).Should().BeTrue();
        new ActivityRetentionState(1, null, 1, sink).Observe(activity, Start, hasStart);
        Field(sink.Observations[0], "startedAt").Kind.Should().Be(CaptureObservationValueKind.Null);
        sink.Observations[0].Timestamp.Should().Be(Start);
    }

    [Fact]
    public void ActivityTagFieldCap_IsExplicit_AndDoesNotMutateSnapshotTags()
    {
        var sink = new BoundedSink(1);
        var tags = Enumerable.Range(0, 140).ToDictionary(i => "key" + i.ToString(CultureInfo.InvariantCulture),
            i => "unique-🧪-" + i.ToString(CultureInfo.InvariantCulture));
        var activity = new CapturedActivity("Orders", "many tags", "id", null, TraceId, null, null,
            Start, Start, null, tags);
        var state = new ActivityRetentionState(1, null, 1, sink);
        state.Observe(activity);
        state.Activities.Single().Tags.Should().HaveCount(140);
        Field(sink.Observations[0], "tagOmittedFieldCount").Integer.Should().Be(12);
        Field(sink.Observations[0], "tagFieldLimit").Integer.Should().Be(128);
        Field(sink.Observations[0], "durationTicks").Kind.Should().Be(CaptureObservationValueKind.Null);
    }

    [Fact]
    public void Exceptions_RecordBeyondRecentCap_WithNullAndRedactedStrings_WithoutChangingResults()
    {
        var sink = new BoundedSink(2);
        var recent = new List<ManagedExceptionEvent>();
        var counts = new Dictionary<string, int>();
        var baselineRecent = new List<ManagedExceptionEvent>();
        var baselineCounts = new Dictionary<string, int>();
        var redactor = new SensitiveDataRedactor();
        string?[] messages = ["订单 🧪 Password=secret", null, "unique-λ"];
        foreach (var message in messages)
        {
            EventPipeExceptionCollector.RecordException(Start.UtcDateTime, 42, "例外", message, -123,
                recent, counts, 1, sink, redactor);
            EventPipeExceptionCollector.RecordException(Start.UtcDateTime, 42, "例外", message, -123,
                baselineRecent, baselineCounts, 1, null, null);
        }
        recent.Should().Equal(baselineRecent);
        counts.Should().BeEquivalentTo(baselineCounts);
        recent.Should().ContainSingle();
        counts["例外"].Should().Be(3);
        sink.Rejected.Should().Be(1);
        Field(sink.Observations[0], "message").Text.Should().Be("订单 🧪 <redacted:sensitive>");
        Field(sink.Observations[1], "message").Kind.Should().Be(CaptureObservationValueKind.Null);
        Field(sink.Observations[0], "hresult").Integer.Should().Be(-123);
        sink.Observations[0].ThreadId.Should().Be(42);
    }

    [Fact]
    public void GcPairsAndSuspension_ArchiveBeyondSnapshotCaps_WithoutInventingOrphanIntervals()
    {
        var sink = new BoundedSink(30);
        var recorded = new GcCaptureState(1, sink);
        var baseline = new GcCaptureState(1);
        foreach (var state in new[] { recorded, baseline })
        {
            state.CollectionEnd(1, 99, 1, Start);
            for (var i = 0; i < 3; i++)
            {
                var at = Start.AddSeconds(i);
                state.CollectionBegin(1, (uint)i, 2, at, 2, "Induced", "BackgroundGC");
                state.SuspendBegin(1, 42, 1, at, 6, 100);
                state.Boundary(1, 42, 1, at.AddMilliseconds(10), 0);
                state.Boundary(1, 42, 1, at.AddMilliseconds(20), 1);
                state.Boundary(1, 42, 1, at.AddMilliseconds(30), 2);
                state.CollectionEnd(1, (uint)i, 1, at.AddMilliseconds(100));
            }
        }
        recorded.Finish(Start, Start.AddSeconds(4), null).Should()
            .BeEquivalentTo(baseline.Finish(Start, Start.AddSeconds(4), null));
        recorded.Collections.Events.Should().ContainSingle();
        sink.Observations.Count(o => o.Category == "gc.collection").Should().Be(3);
        sink.Observations.Count(o => o.Category == "gc.suspension").Should().Be(3);
        sink.Observations.Count(o => o.Category == "gc.restart").Should().Be(3);
        var orphan = sink.Observations.Single(o => o.Category == "gc.correlation");
        orphan.Name.Should().Be("orphan-collection-end");
        orphan.Timestamp.Should().BeNull();
        var pause = sink.Observations.First(o => o.Category == "gc.suspension");
        Field(pause, "durationTicks").Integer.Should().Be(TimeSpan.FromMilliseconds(10).Ticks);
        Field(pause, "gcCountAtSuspend").Integer.Should().Be(100);
    }

    [Fact]
    public void GcHeap_OlderEventVersionDoesNotFabricatePohZero_AndCapIsSnapshotOnly()
    {
        var sink = new BoundedSink(4);
        var retained = new List<GcHeapStatsSample>();
        var dropped = 0;
        var sample = new GcHeapStatsSample(Start, 10, 20, 30, 40, 0, 100, 4, 3, 0, 2, 1, 5, 6);
        EventPipeGcCollector.RecordHeapSample(sample, 1, 7, retained, 1, ref dropped, sink);
        EventPipeGcCollector.RecordHeapSample(sample with { PohSizeBytes = 123 }, 2, 7, retained, 1, ref dropped, sink);
        retained.Should().ContainSingle();
        dropped.Should().Be(1);
        sink.Observations.Should().HaveCount(2);
        Field(sink.Observations[0], "pohSizeBytes").Kind.Should().Be(CaptureObservationValueKind.Null);
        Field(sink.Observations[1], "pohSizeBytes").Integer.Should().Be(123);
        Field(sink.Observations[0], "clrInstanceId").Integer.Should().Be(7);
    }

    [Fact]
    public void ContentionTopN_EmitsBeforeEviction_AndRejectionDoesNotChangeLongestWaits()
    {
        var sink = new BoundedSink(2);
        var observed = new EventPipeContentionCollector.TopContentionEvents(1, sink);
        var baseline = new EventPipeContentionCollector.TopContentionEvents(1);
        foreach (var ms in new[] { 100, 2, 50 })
        {
            var sample = new ContentionEventSample(Start, Start.AddMilliseconds(ms),
                TimeSpan.FromMilliseconds(ms), 42, null, ulong.MaxValue, 0, "λ.等待", "应用.dll");
            observed.Add(sample);
            baseline.Add(sample);
        }
        observed.GetOrdered().Should().Equal(baseline.GetOrdered());
        observed.DroppedCount.Should().Be(2);
        sink.Rejected.Should().Be(1);
        sink.Observations.Should().HaveCount(2);
        Field(sink.Observations[1], "durationTicks").Integer.Should().Be(TimeSpan.FromMilliseconds(2).Ticks);
        Field(sink.Observations[0], "lockId").Text.Should().Be(ulong.MaxValue.ToString(CultureInfo.InvariantCulture));
        Field(sink.Observations[0], "ownerManagedThreadId").Kind.Should().Be(CaptureObservationValueKind.Null);
        Field(sink.Observations[0], "associatedObjectId").Kind.Should().Be(CaptureObservationValueKind.Null);
    }

    [Fact]
    public void ThreadPoolTimeline_EmitsBeforeRingEviction_AndPreservesInferenceAndNulls()
    {
        var sink = new BoundedSink(10);
        var workers = EventPipeThreadPoolCollector.CreateCountQueue(1, "worker", sink);
        var baseline = EventPipeThreadPoolCollector.CreateCountQueue(1, "worker", null);
        foreach (var sample in new[]
        {
            new EventPipeThreadPoolCollector.CountSample(Start, 2, ThreadPoolEvidence.RuntimeObserved),
            new EventPipeThreadPoolCollector.CountSample(Start.AddSeconds(1), 3, ThreadPoolEvidence.InferredFromDelta),
        })
        {
            workers.Enqueue(sample);
            baseline.Enqueue(sample);
        }
        workers.Items.Should().Equal(baseline.Items);
        workers.DroppedCount.Should().Be(1);
        var adjustments = EventPipeThreadPoolCollector.CreateAdjustmentQueue(1, sink);
        adjustments.Enqueue(new(Start, "99", null, 3, null, ThreadPoolEvidence.RuntimeUnrecognized,
            ThreadPoolEvidence.Missing, ThreadPoolEvidence.RuntimeObserved));
        sink.Observations.Should().HaveCount(3);
        Field(sink.Observations[1], "provenance").Text.Should().Be(ThreadPoolEvidence.InferredFromDelta);
        Field(sink.Observations[2], "oldCount").Kind.Should().Be(CaptureObservationValueKind.Null);
        Field(sink.Observations[2], "latestThroughput").Kind.Should().Be(CaptureObservationValueKind.Null);
        sink.Observations[2].Name.Should().Be("99");
    }

    [Fact]
    public void Logs_FilterRedactEnrichAndEmitBeforeRingEviction_WithExactUnicodeAndUniqueStrings()
    {
        var collector = new EventPipeLogCollector(new SensitiveDataRedactor());
        var sink = new BoundedSink(10);
        var recent = new Queue<EventPipeLogCollector.MutableLogEntry>();
        EventPipeLogCollector.MutableLogEntry? last = null;
        var truncated = false;
        var scopes = new Dictionary<string, string> { ["tenant"] = "租户-λ", ["authorization"] = "secret" };
        collector.TryCreateLogEntry(Start, "Other", LogLevel.Error, 9, null, "secret", null,
            scopes, ["Orders.*"], LogLevel.Information, 100, out _).Should().BeFalse();
        collector.TryCreateLogEntry(Start, "Orders.注文", LogLevel.Debug, 9, null, "secret", null,
            scopes, ["Orders.*"], LogLevel.Information, 100, out _).Should().BeFalse();

        foreach (var message in new[] { "重复 🧪 Password=secret", "重复 🧪 Password=secret", "unique-λ" })
        {
            collector.TryCreateLogEntry(Start, "Orders.注文", LogLevel.Warning, 9, null, message, null,
                scopes, ["Orders.*"], LogLevel.Information, 100, out var formatted).Should().BeTrue();
            collector.RecordLogEntry(formatted, false, ref last, recent, 1, ref truncated, sink).Should().BeTrue();
            collector.TryCreateLogEntry(Start, "Orders.注文", LogLevel.Warning, 9, null, message,
                """{"Type":"例外","Message":"Password=credential"}""", scopes, ["Orders.*"],
                LogLevel.Information, 100, out var json).Should().BeTrue();
            collector.RecordLogEntry(json, true, ref last, recent, 1, ref truncated, sink).Should().BeFalse();
        }
        collector.FlushLogEntry(sink, last);
        truncated.Should().BeTrue();
        recent.Should().ContainSingle();
        sink.Observations.Select(o => Field(o, "message").Text)
            .Should().Equal("重复 🧪 <redacted:sensitive>", "重复 🧪 <redacted:sensitive>", "unique-λ");
        Field(sink.Observations[0], "exceptionType").Text.Should().Be("例外");
        Field(sink.Observations[0], "exceptionMessage").Text.Should().Be(SensitiveDataRedactor.RedactedPlaceholder);
        Field(sink.Observations[0], "scope.tenant").Text.Should().Be("租户-λ");
        Field(sink.Observations[0], "scope.authorization").Text.Should().Be(SensitiveDataRedactor.RedactedPlaceholder);
        Field(sink.Observations[0], "eventName").Kind.Should().Be(CaptureObservationValueKind.Null);
    }

    [Fact]
    public void Logs_ByteLimitAndRejectedSinkPreserveEphemeralResult()
    {
        var collector = new EventPipeLogCollector(new SensitiveDataRedactor());
        var sink = new BoundedSink(0);
        collector.TryCreateLogEntry(Start, "Orders", LogLevel.Error, 1, null, new string('界', 100), null,
            new Dictionary<string, string>(), [], LogLevel.Information, 30, out var entry).Should().BeTrue();
        Encoding.UTF8.GetByteCount(entry.Message).Should().BeLessThanOrEqualTo(30);
        var recent = new Queue<EventPipeLogCollector.MutableLogEntry>();
        EventPipeLogCollector.MutableLogEntry? last = null;
        var truncated = false;
        collector.RecordLogEntry(entry, false, ref last, recent, 1, ref truncated, sink).Should().BeTrue();
        collector.FlushLogEntry(sink, last);
        sink.Rejected.Should().Be(1);
        recent.Single().Should().BeSameAs(entry);
        truncated.Should().BeFalse();
    }

    [Fact]
    public void SourceLoss_DistinguishesKnownZeroMeasuredLossAndUnknown()
    {
        var sink = new BoundedSink(0);
        EventPipeCollectionRunner.ReportSourceLoss(sink, null);
        EventPipeCollectionRunner.ReportSourceLoss(sink, 0);
        EventPipeCollectionRunner.ReportSourceLoss(sink, 19);
        sink.SourceLoss.Should().Equal(("EventPipe", null), ("EventPipe", 0L), ("EventPipe", 19L));
        sink.Rejected.Should().Be(0);
    }

    [Fact]
    public async Task ConcurrentScopes_CaptureLocalSinkBeforeCallbacksWithoutExecutionContext()
    {
        var sinks = new[] { new BoundedSink(1), new BoundedSink(1) };
        await Task.WhenAll(sinks.Select(async (sink, i) =>
        {
            using var scope = CaptureRecordingContext.Enter(sink);
            var state = new ActivityRetentionState(1, null, 1, CaptureRecordingContext.Current);
            Task callback;
            using (ExecutionContext.SuppressFlow())
            {
                callback = Task.Run(() =>
                {
                    CaptureRecordingContext.Current.Should().BeNull();
                    EventPipeActivityCollector.TryCreateActivity("Orders", $"callback-{i}", ActivityArguments(),
                        Start.UtcDateTime, null, out var activity, out var hasStart).Should().BeTrue();
                    state.Observe(activity, Start, hasStart);
                });
            }
            await callback;
            CaptureRecordingContext.Current.Should().BeSameAs(sink);
        }));
        CaptureRecordingContext.Current.Should().BeNull();
        sinks[0].Observations.Single().Name.Should().Be("callback-0");
        sinks[1].Observations.Single().Name.Should().Be("callback-1");
    }

    private static Dictionary<string, string> ActivityArguments() => new()
    {
        ["TraceId"] = TraceId, ["SpanId"] = "0123456789abcdef", ["ParentSpanId"] = "0000000000000000",
        ["StartTimeTicks"] = Start.UtcTicks.ToString(CultureInfo.InvariantCulture), ["DurationTicks"] = "1000",
        ["Tags"] = "[label, 重复 λ 🧪], [http.request.header.authorization, secret], [db.statement, SELECT 'alice']",
    };

    private static (ConcurrentDictionary<string, CounterValue> Latest, ConcurrentDictionary<string, CounterValue> First,
        ConcurrentDictionary<string, CounterValue> Maximum) CounterState() => (new(), new(), new());

    private static CaptureObservationField Field(CaptureObservation observation, string name)
        => observation.Fields.Single(f => f.Name == name);

    private sealed class BoundedSink(int capacity) : ICaptureObservationSink
    {
        internal List<CaptureObservation> Observations { get; } = [];
        internal List<(string Source, long? Count)> SourceLoss { get; } = [];
        internal int Rejected { get; private set; }

        public bool TryAppend(CaptureObservation observation)
        {
            if (Observations.Count >= capacity)
            {
                Rejected++;
                return false;
            }
            Observations.Add(observation with { Fields = observation.Fields.ToArray() });
            return true;
        }

        public void ReportSourceLoss(string source, long? count) => SourceLoss.Add((source, count));
        public void ArtifactRegistered(DiagnosticHandle handle, object artifact) { }
    }
}
