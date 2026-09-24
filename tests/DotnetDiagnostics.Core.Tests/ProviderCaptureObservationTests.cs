using System.Buffers.Binary;
using DotnetDiagnostics.Core.CaptureRecording;
using DotnetDiagnostics.Core.Db;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.EventSources;
using DotnetDiagnostics.Core.Exceptions;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Internal;
using DotnetDiagnostics.Core.Kestrel;
using DotnetDiagnostics.Core.Networking;
using DotnetDiagnostics.Core.Requests;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.Startup;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class ProviderCaptureObservationTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    [Fact]
    public void EfParser_FiltersSourceAndMissingCommandsBeforeRecordingRedactedCompletions()
    {
        var sink = new BoundedSink(4);
        var state = new DbEventAggregationState(sink);
        var parser = new EfCoreBridgeEventParser(new SensitiveDataRedactor());
        var arguments = new Dictionary<string, string>
        {
            ["Tags"] = "[db.statement, SELECT 名称 FROM 用户 WHERE secret = 'private literal' AND id = 987] " +
                "[db.connection_string, Server=数据库;Password=private-password] [unrelated, do-not-record]",
            ["DurationTicks"] = "25000",
            ["TraceId"] = "trace-重复",
        };
        parser.HandleCompletion("Unapproved.Source", arguments, Start, Guid.Empty, Guid.Empty, 19, state);
        parser.HandleCompletion("Microsoft.EntityFrameworkCore", new Dictionary<string, string>(),
            Start, Guid.Empty, Guid.Empty, 19, state);
        Assert.Equal(0, sink.Attempts);
        parser.HandleCompletion("Microsoft.EntityFrameworkCore", arguments, Start, Guid.Empty, Guid.Empty, 19, state);
        var record = Assert.Single(sink.Records);
        Assert.Equal("db.command.completion", record.Category);
        Assert.Equal(19, record.ThreadId);
        Assert.Equal(2.5, Field(record, "durationMs").Number);
        var text = string.Join(" ", record.Fields.Select(f => f.Text));
        Assert.DoesNotContain("private", text);
        Assert.DoesNotContain("987", text);
        Assert.DoesNotContain("do-not-record", text);
        Assert.Contains("名称", text);
        arguments.Remove("DurationTicks");
        parser.HandleCompletion("Microsoft.EntityFrameworkCore", arguments, Start, Guid.Empty, Guid.Empty, 19, state);
        Assert.True(Field(sink.Records[1], "timingUnavailable").Boolean);
        Assert.Equal(CaptureObservationValueKind.Null, Field(sink.Records[1], "durationMs").Kind);
        Assert.Equal(CaptureObservationValueKind.Null, Field(sink.Records[1], "startedAtUtc").Kind);
    }

    [Fact]
    public void DbAggregation_RecordsSanitizedShapesBeforeOverflowAndPreservesSnapshotOnRejection()
    {
        var sink = new BoundedSink(4);
        var recorded = new DbEventAggregationState(sink);
        var baseline = new DbEventAggregationState();
        var redactor = new SensitiveDataRedactor();
        var sql = redactor.RedactSqlText("SELECT 名称 FROM 客户 WHERE secret = 'private literal' AND id = 987")!;
        var connection = redactor.Redact("Server=客户;Password=private-password")!;
        for (var i = 0; i < DbEventAggregationState.MaxTrackedCommandAggregates + 3; i++)
        {
            var command = new PendingCommand("Microsoft.EntityFrameworkCore", $"shape-{i}", $"hash-{i}",
                sql, connection, "scope-重复", Start);
            recorded.CompleteCommand(command, Start.AddMilliseconds(i + 1), i + 1);
            baseline.CompleteCommand(command, Start.AddMilliseconds(i + 1), i + 1);
        }
        Assert.Equal(DbEventAggregationState.MaxTrackedCommandAggregates + 3, sink.Attempts);
        Assert.Equal(sink.Attempts - 4, sink.Rejections);
        Assert.All(sink.Records, observation =>
        {
            Assert.Equal(sql, Field(observation, "commandTextSanitized").Text);
            Assert.DoesNotContain("private", string.Join(" ", observation.Fields.Select(f => f.Text)));
            Assert.Equal(CaptureObservationValueKind.Number, Field(observation, "durationMs").Kind);
        });
        var actual = recorded.BuildSnapshot(1, Start, TimeSpan.FromSeconds(1));
        var expected = baseline.BuildSnapshot(1, Start, TimeSpan.FromSeconds(1));
        Assert.Equal(expected.TotalCommands, actual.TotalCommands);
        actual.ByCommand.Should().BeEquivalentTo(expected.ByCommand);
        Assert.Equal(expected.Notes, actual.Notes);
    }

    [Fact]
    public void DbPending_MissingAndExpiredStartsDoNotEmitCommands()
    {
        var sink = new BoundedSink(8);
        var state = new DbEventAggregationState(sink);
        var command = new PendingCommand("SqlClient", "key", "hash", "SELECT 名称", "", "scope", Start);
        Assert.False(state.TryCompletePendingCommand("missing", Start));
        state.SetPendingCommand("stale", command);
        Assert.False(state.TryCompletePendingCommand("stale", Start.AddMinutes(3)));
        state.SetPendingCommand("fresh", command with { StartedAt = Start.AddMinutes(3) });
        Assert.True(state.TryCompletePendingCommand("fresh", Start.AddMinutes(3).AddMilliseconds(2)));
        Assert.False(state.TryCompletePendingCommand("fresh", Start.AddMinutes(3).AddMilliseconds(3)));
        Assert.Single(sink.Records);
        Assert.Equal(2, Field(sink.Records[0], "durationMs").Number);
    }

    [Fact]
    public void DbPending_DuplicateReusedAndOutOfOrderIdsRemainAmbiguousWithoutChangingLegacyCounts()
    {
        var sink = new BoundedSink(8);
        var state = new DbEventAggregationState(sink);
        var command = new PendingCommand("SqlClient", "key", "hash", "SELECT 名称", "", "scope", Start);
        state.SetPendingCommand("duplicate", command);
        state.SetPendingCommand("duplicate", command);
        Assert.True(state.TryCompletePendingCommand("duplicate", Start.AddSeconds(1)));
        state.SetPendingCommand("duplicate", command);
        Assert.True(state.TryCompletePendingCommand("duplicate", Start.AddSeconds(1)));
        Assert.False(state.TryCompletePendingCommand("stop-first", Start));
        state.SetPendingCommand("stop-first", command);
        Assert.True(state.TryCompletePendingCommand("stop-first", Start.AddSeconds(1)));
        state.SetPendingCommand("clock", command);
        Assert.True(state.TryCompletePendingCommand("clock", Start.AddSeconds(-1)));
        Assert.Equal(4, state.TotalCommands);
        Assert.All(sink.Records, record =>
        {
            Assert.Equal("db.command.ambiguous-aggregate-input", record.Category);
            Assert.Equal(CaptureObservationValueKind.Null, Field(record, "durationMs").Kind);
            Assert.Equal(CaptureObservationValueKind.Null, Field(record, "startedAtUtc").Kind);
        });
    }

    [Fact]
    public void DbPending_CaptureIdentityHistoryIsBoundedAndStopsClaimingUniqueCompletionsAtCapacity()
    {
        var sink = new BoundedSink(0);
        var state = new DbEventAggregationState(sink);
        var command = new PendingCommand("SqlClient", "shape", "hash", "SELECT 名称", "", "scope", Start);
        for (var i = 0; i <= DbEventAggregationState.MaxRecordedCommandIdentities; i++)
        {
            var id = $"command-{i}";
            state.SetPendingCommand(id, command);
            Assert.True(state.TryCompletePendingCommand(id, Start.AddMilliseconds(1)));
        }
        Assert.Equal("db.command.ambiguous-aggregate-input", sink.LastAttempt!.Category);
        Assert.Equal(DbEventAggregationState.MaxRecordedCommandIdentities + 1, state.TotalCommands);
        state.BuildSnapshot(1, Start, TimeSpan.FromSeconds(1));
        Assert.Equal(DbEventAggregationState.MaxRecordedCommandIdentities,
            Field(sink.LastAttempt!, "recordedCommandIdentities").Integer);
        Assert.Equal(sink.Attempts, sink.Rejections);
    }

    [Fact]
    public void StartupBuffer_EmitsBeyondSnapshotCapsAndKeepsHighCardinalityAndRepeatedNames()
    {
        var sink = new BoundedSink(8);
        var buffer = new EventPipeStartupCollector.StartupCaptureBuffer(sink);
        var baseline = new EventPipeStartupCollector.StartupCaptureBuffer();
        for (var i = 0; i < EventPipeStartupCollector.MaxRetainedTimelineEvents + 3; i++)
        {
            var load = new StartupAssemblyLoad(Start.AddMilliseconds(i), "AssemblyLoad",
                i < 2 ? "重复🚀" : $"组件-{i}", i);
            buffer.AddAssembly(load);
            baseline.AddAssembly(load);
        }
        Assert.Equal(buffer.TotalAssemblyLoads * 2, sink.Attempts);
        Assert.Equal("重复🚀", Field(sink.Records[0], "assemblyName").Text);
        Assert.Equal("重复🚀", Field(sink.Records[2], "assemblyName").Text);
        Assert.True(buffer.Truncated);
        Assert.Equal(baseline.AssemblyLoads, buffer.AssemblyLoads);
        Assert.Equal(baseline.Timeline, buffer.Timeline);
        Assert.Equal(baseline.BuildAssemblyAggregates(), buffer.BuildAssemblyAggregates());
    }

    [Fact]
    public void StartupBuffer_DiAndModuleCarryTypedNullableDimensions()
    {
        var sink = new BoundedSink(8);
        var buffer = new EventPipeStartupCollector.StartupCaptureBuffer(sink);
        buffer.AddModule(new StartupModuleLoad(Start, "ModuleLoad", "组件.dll", null, 45, null));
        buffer.AddDiEvent(new StartupDiEvent(Start, "CallSiteBuilt", 7, "服务<T>", null, 2, 3, null, null, 11, null, 0, 1));
        var module = sink.Records.Single(r => r.Category == "startup.module");
        Assert.Equal(CaptureObservationValueKind.Null, Field(module, "modulePath").Kind);
        Assert.Equal(45, Field(module, "moduleId").Integer);
        var di = sink.Records.Single(r => r.Category == "startup.di");
        Assert.Equal(11, Field(di, "nodeCount").Integer);
        Assert.Equal("服务<T>", Field(di, "serviceType").Text);
        Assert.Equal(CaptureObservationValueKind.Null, Field(di, "methodSize").Kind);
    }

    [Theory]
    [InlineData("Resolution", "System.Net.NameResolution")]
    [InlineData("Handshake", "System.Net.Security")]
    public void NetworkProductionHandler_OnlyAcceptedPairsEmitAndFailureIsNotSuccess(string prefix, string provider)
    {
        var sink = new BoundedSink(8);
        var pending = new NetworkingActivityCorrelator<DateTimeOffset>();
        var durations = new BoundedDurationSampler();
        long started = 0, stopped = 0, failed = 0;
        var id = Guid.NewGuid();
        Handle("Stop", 0);
        id = Guid.NewGuid();
        Handle("Start", 1);
        Handle("Start", 2);
        Handle("Stop", 3);
        id = Guid.NewGuid();
        Handle("Start", 10);
        Handle("Failed", 11);
        Handle("Failed", 12);
        Handle("Stop", 15);
        Handle("Stop", 16);
        Handle("Start", 17);
        Handle("Stop", 18);
        id = Guid.Empty;
        Handle("Start", 20);
        Handle("Stop", 21);

        var observation = Assert.Single(sink.Records);
        Assert.Equal("networking.paired.completion", observation.Category);
        Assert.Equal(provider, Field(observation, "provider").Text);
        Assert.Equal(5, Field(observation, "durationMs").Number);
        Assert.True(Field(observation, "observedFailure").Boolean);
        Assert.Equal(42, observation.ThreadId);
        Assert.Equal(1, durations.Count);
        Assert.True(pending.Snapshot().HasLimitations);

        void Handle(string suffix, int ms) =>
            EventPipeNetworkingCollector.HandlePaired(prefix + suffix, prefix + "Start", prefix + "/Start",
                prefix + "Stop", prefix + "/Stop", prefix + "Failed", prefix + "/Failed",
                id, Start.AddMilliseconds(ms), pending, durations, ref started, ref stopped, ref failed,
                sink, provider, 42);
    }

    [Fact]
    public void NetworkProductionHandler_RejectingSinkDoesNotChangeLatencyPopulation()
    {
        var sink = new BoundedSink(0);
        var pending = new NetworkingActivityCorrelator<DateTimeOffset>();
        var durations = new BoundedDurationSampler();
        long starts = 0, stops = 0, failures = 0;
        var id = Guid.NewGuid();
        foreach (var phase in new[] { "Start", "Stop" })
            EventPipeNetworkingCollector.HandlePaired("Resolution" + phase,
                "ResolutionStart", "Resolution/Start", "ResolutionStop", "Resolution/Stop",
                "ResolutionFailed", "Resolution/Failed", id, Start, pending, durations,
                ref starts, ref stops, ref failures, sink, "System.Net.NameResolution");
        Assert.Equal(1, durations.Count);
        Assert.Equal(1, pending.Snapshot().Paired);
        Assert.Equal(1, sink.Attempts);
        Assert.Equal(1, sink.Rejections);
    }

    [Fact]
    public void NetworkQueue_EmitsOnlyFiniteNonnegativeParsedSamples()
    {
        var sink = new BoundedSink(2);
        var durations = new BoundedDurationSampler();
        foreach (var invalid in new object?[] { null, -1, double.NaN, double.PositiveInfinity })
            Assert.Throws<FormatException>(() => EventPipeNetworkingCollector.AddQueueSample(invalid, durations, sink));
        EventPipeNetworkingCollector.AddQueueSample(2.5, durations, sink, Start, 17);
        Assert.Equal(1, durations.Count);
        var record = Assert.Single(sink.Records);
        Assert.Equal(2.5, Field(record, "durationMs").Number);
        Assert.Equal(Start, record.Timestamp);
        Assert.Equal(17, record.ThreadId);
    }

    [Fact]
    public void EventSource_RetainsOnlyRequestedProviderWithinExplicitLimit()
    {
        var sink = new BoundedSink(4);
        var retained = new List<CapturedEvent>();
        var observation = new CapturedEvent(Start, "Approved.Provider", "操作", "Informational",
            new Dictionary<string, string> { ["命令"] = "重复🚀", ["empty"] = "" });
        EventPipeEventSourceCollector.RetainEvent(retained, 2, "Approved.Provider", observation with { Provider = "Other.Provider" }, sink);
        for (var i = 0; i < 4; i++)
            EventPipeEventSourceCollector.RetainEvent(retained, 2, "Approved.Provider", observation, sink);
        Assert.Equal(2, retained.Count);
        Assert.Equal(2, sink.Attempts);
        Assert.Equal("重复🚀", Field(sink.Records[0], "payload.命令").Text);
        Assert.Equal("", Field(sink.Records[0], "payload.empty").Text);
    }

    [Fact]
    public void EventSource_FieldProjectionIsBoundedAndReportsOmissions()
    {
        var sink = new BoundedSink(1);
        var observation = new CapturedEvent(Start, "Approved.Provider", "WideEvent", "Informational",
            Enumerable.Range(0, 300).ToDictionary(i => $"tag-{i}", i => $"value-{i}"));
        EventPipeEventSourceCollector.RetainEvent([], 1, observation.Provider, observation, sink);
        Assert.Equal(259, sink.Records[0].Fields.Count);
        Assert.Equal(44, Field(sink.Records[0], "omittedPayloadFields").Integer);
    }

    [Fact]
    public void Catalog_RecordsMetadataOnly()
    {
        var sink = new BoundedSink(1);
        EventPipeEventCatalogCollector.RecordMetadata(sink, new CatalogEventOccurrence(Start, "任意.Provider", "Event", "Warning"));
        var record = Assert.Single(sink.Records);
        Assert.Equal("event-catalog.metadata", record.Category);
        Assert.Equal(2, record.Fields.Count);
        Assert.DoesNotContain(record.Fields, f => f.Name.StartsWith("payload", StringComparison.Ordinal));
    }

    [Fact]
    public void RequestsTracker_RecordsOnlyRetainedTransitionsWithoutInventingBodiesOrUniqueCompletions()
    {
        var sink = new BoundedSink(8);
        var tracker = new EventPipeInFlightRequestCollector.OldestPendingRequestTracker(1, sink);
        var baseline = new EventPipeInFlightRequestCollector.OldestPendingRequestTracker(1);
        var request = new EventPipeInFlightRequestCollector.PendingRequest("trace", null, "/订单", "GET", Start);
        tracker.Track("id", request);
        baseline.Track("id", request);
        tracker.Track("new", request with { StartedAt = Start.AddSeconds(1) });
        baseline.Track("new", request with { StartedAt = Start.AddSeconds(1) });
        tracker.Track("id", request);
        baseline.Track("id", request);
        Assert.False(tracker.Remove("missing", Start));
        Assert.Equal(baseline.GetPending(), tracker.GetPending());
        Assert.Equal(baseline.DroppedCount, tracker.DroppedCount);
        Assert.True(tracker.Remove("id", Start.AddSeconds(2)));
        Assert.False(tracker.Remove("id", Start.AddSeconds(3)));
        Assert.Equal(3, sink.Attempts);
        Assert.Equal("replaced-existing-start", Field(sink.Records[1], "transition").Text);
        Assert.Equal("removed-by-stop", Field(sink.Records[2], "transition").Text);
        Assert.All(sink.Records, record => Assert.DoesNotContain(record.Fields, f => f.Name.Contains("body", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void RequestsTracker_EvictionIdentifiesRemovedStateWithoutGuessingTransitionTime()
    {
        var sink = new BoundedSink(8);
        var tracker = new EventPipeInFlightRequestCollector.OldestPendingRequestTracker(1, sink);
        tracker.Track("newer", new EventPipeInFlightRequestCollector.PendingRequest("trace-new", null, "/new", "GET", Start.AddSeconds(1)));
        tracker.Track("older", new EventPipeInFlightRequestCollector.PendingRequest("trace-old", null, "/old", "GET", Start));
        Assert.Equal(3, sink.Attempts);
        Assert.Null(sink.Records[1].Timestamp);
        Assert.Equal("newer", Field(sink.Records[1], "key").Text);
        Assert.Equal("evicted-by-older-start", Field(sink.Records[1], "transition").Text);
        Assert.Single(tracker.GetPending());
        Assert.Equal(1, tracker.DroppedCount);
    }

    [Fact]
    public void KestrelPhases_ExcludeQueriesAndDoNotPretendStopsAreUniqueCompletions()
    {
        var sink = new BoundedSink(4);
        foreach (var phase in new[] { "Unrelated", "RequestStart", "RequestStop", "RequestStop" })
            EventPipeKestrelCollector.RecordLifecycle(sink, phase, Start, 2, "conn", "req",
                "POST", "/订单?access_token=secret", "HTTP/2", "");
        Assert.Equal(3, sink.Attempts);
        Assert.Equal("/订单", Field(sink.Records[0], "path").Text);
        Assert.Equal(CaptureObservationValueKind.Null, Field(sink.Records[1], "path").Kind);
        Assert.All(sink.Records, record =>
        {
            Assert.False(Field(record, "correlated").Boolean);
            Assert.DoesNotContain("secret", string.Join(" ", record.Fields.Select(f => f.Text)));
        });
    }

    [Fact]
    public void DatasProductionAccumulator_UsesDecodedFieldsAndHonorsExplicitLimit()
    {
        var payload = new byte[DatasPayloadParser.SampleLength + 1];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, DatasPayloadParser.SupportedVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(2), ulong.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(10), 123);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(14), 12);
        var outcome = DatasPayloadParser.TryParseSample(payload, Start, out var sample, out var extra);
        var sink = new BoundedSink(8);
        var retained = new List<DatasSampleEvent>();
        int malformed = 0, unsupported = 0, extraCount = 0;
        for (var i = 0; i < 3; i++)
            EventPipeGcDatasCollector.Accumulate(outcome, sample, retained, 1, extra,
                ref malformed, ref unsupported, ref extraCount, sink);
        EventPipeGcDatasCollector.Accumulate(DatasParseOutcome.Malformed, sample, retained, 1, false,
            ref malformed, ref unsupported, ref extraCount, sink);
        var observation = Assert.Single(sink.Records);
        Assert.Single(retained);
        Assert.Equal(1, malformed);
        Assert.Equal(3, extraCount);
        Assert.Equal("18446744073709551615", Field(observation, "gcIndexUInt64").Text);
        Assert.Equal(123, Field(observation, "elapsedBetweenGcsUs").Integer);
        Assert.True(Field(observation, "extraBytes").Boolean);
    }

    [Fact]
    public void CrashGuard_RetainedEvidenceKeepsInferenceAndUnknownLossExplicit()
    {
        var sink = new BoundedSink(10);
        var exception = new CrashGuardExceptionEvent(Start, "测试.Exception", "message", "0x1", 7,
            "ExceptionThrown_V1", false, ["at 服务.Run()"]);
        var snapshot = new CrashGuardSnapshot(1, Start, TimeSpan.FromSeconds(1), true, 1, true,
            100, [], [exception], exception with { IsUnhandled = true }, [])
        {
            RecentCap = 1,
            Observation = new CrashGuardObservation(false, null, "IOException", false, true, exception),
        };
        EventPipeCrashGuardCollector.RecordRetainedEvidence(sink, snapshot, true);
        var quality = sink.Records[0];
        Assert.True(Field(quality, "finalInferredFromExit").Boolean);
        Assert.Equal(CaptureObservationValueKind.Null, Field(quality, "eventsLost").Kind);
        Assert.Equal(100, Field(quality, "totalExceptions").Integer);
        Assert.Contains(sink.Records, r => r.Fields.Any(f => f.Name == "role" && f.Text == "last-observed-not-proof-of-termination"));
        Assert.Equal(7, sink.Attempts);
        Assert.Null(sink.Loss);
        Assert.Equal("Microsoft-Windows-DotNETRuntime", sink.LossSource);
    }

    [Fact]
    public void CrashGuard_CallbackAndRetainedEvidenceRedactSecretsAndBoundStackFrames()
    {
        var sink = new BoundedSink(140);
        var stack = Enumerable.Repeat("at 服务.Run() Password=frame-secret", 130).ToArray();
        EventPipeCrashGuardCollector.RecordCrashObservation(sink, Start, 7, "ExceptionThrown_V1", false,
            "Exception", "Password=message-secret", null, stack, new SensitiveDataRedactor());
        var occurrence = Assert.Single(sink.Records);
        Assert.Equal("crashguard.exception.observed", occurrence.Category);
        Assert.False(Field(occurrence, "explicitUnhandledEvent").Boolean);
        Assert.Equal(2, Field(occurrence, "omittedStackFrames").Integer);
        Assert.Equal(CaptureObservationValueKind.Null, Field(occurrence, "hResult").Kind);
        Assert.Equal(128, occurrence.Fields.Count(f => f.Name.StartsWith("stack.", StringComparison.Ordinal)));
        var exception = new CrashGuardExceptionEvent(Start, "Exception", "Password=message-secret", "0x1", 7,
            "ExceptionThrown_V1", false, stack);
        var snapshot = new CrashGuardSnapshot(1, Start, TimeSpan.FromSeconds(1), false, null, false,
            1, [], [exception], null, []) { RecentCap = 1 };
        EventPipeCrashGuardCollector.RecordRetainedEvidence(sink, snapshot, false);
        Assert.Equal(128, sink.Records.Count(r => r.Category == "crashguard.retained-frame"));
        Assert.Equal(2, Field(sink.Records.Single(r => r.Category == "crashguard.retained-evidence"), "omittedStackFrames").Integer);
        Assert.All(sink.Records, record =>
        {
            Assert.DoesNotContain("frame-secret", record.Name ?? "");
            Assert.DoesNotContain("secret", string.Join(" ", record.Fields.Select(f => f.Text)));
        });
        Assert.Contains("message-secret", snapshot.Exceptions[0].ExceptionMessage);
        Assert.Equal(130, snapshot.Exceptions[0].ManagedStack.Count);
    }

    [Fact]
    public void CrashGuard_PayloadlessExplicitEventKeepsUnknownsAndDoesNotRetryRejectedSink()
    {
        var sink = new BoundedSink(0);
        EventPipeCrashGuardCollector.RecordCrashObservation(sink, Start, 7, "FailFast", true,
            null, null, null, null, new SensitiveDataRedactor());
        Assert.Equal(1, sink.Attempts);
        Assert.Equal(1, sink.Rejections);
        var record = sink.LastAttempt!;
        Assert.True(Field(record, "explicitUnhandledEvent").Boolean);
        Assert.False(Field(record, "stackAvailable").Boolean);
        Assert.Equal(CaptureObservationValueKind.Null, Field(record, "exceptionType").Kind);
        Assert.Equal(CaptureObservationValueKind.Null, Field(record, "message").Kind);
    }

    private static CaptureObservationField Field(CaptureObservation observation, string name) =>
        Assert.Single(observation.Fields, field => field.Name == name);

    private sealed class BoundedSink(int capacity) : ICaptureObservationSink
    {
        internal List<CaptureObservation> Records { get; } = [];
        internal int Attempts { get; private set; }
        internal int Rejections { get; private set; }
        internal long? Loss { get; private set; }
        internal string? LossSource { get; private set; }
        internal CaptureObservation? LastAttempt { get; private set; }

        public bool TryAppend(CaptureObservation observation)
        {
            Attempts++;
            LastAttempt = observation;
            if (Records.Count >= capacity)
            {
                Rejections++;
                return false;
            }
            Records.Add(observation);
            return true;
        }

        public void ReportSourceLoss(string source, long? count) { LossSource = source; Loss = count; }
        public void ArtifactRegistered(DiagnosticHandle handle, object artifact) { }
    }
}
