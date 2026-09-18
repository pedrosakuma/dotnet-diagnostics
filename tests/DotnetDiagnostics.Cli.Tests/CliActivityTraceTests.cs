using System.Globalization;
using System.Text.Json;
using DotnetDiagnostics.Cli;
using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.ProcessDiscovery;
using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

/// <summary>
/// Real CLI parsing, dispatch, Core use case, production retention state, and result serialization.
/// Only IPC delivery is substituted; the live Core tests cover that boundary with observed-noise readiness.
/// </summary>
public sealed class CliActivityTraceTests
{
    private const string Trace = "abcdef0123456789abcdef0123456789";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEnumerable<object[]> HttpTagCases()
    {
        yield return ["empty", null!];
        yield return ["enriched", null!];
        // Optional replay is additional coverage, never a substitute for the deterministic cases.
        if (Environment.GetEnvironmentVariable("HTTP_ACTIVITY_REPLAY") is { Length: > 0 } directory)
        {
            foreach (var framework in new[] { "net8.0", "net9.0", "net10.0" })
                foreach (var mode in new[] { "plain", "enrich" })
                    yield return [$"{framework}-{mode}", Path.Combine(directory, $"{framework}-{mode}.json")];
        }
    }

    [Theory]
    [MemberData(nameof(HttpTagCases))]
    public async Task HttpTags_CollectionAndQueriesPreserveAvailabilityWithoutInventingDestinations(string scenario, string? replayFile)
    {
        ActivityCapture capture;
        if (replayFile is not null)
        {
            using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(replayFile));
            capture = evidence.RootElement.GetProperty("capture").Deserialize<ActivityCapture>(JsonOptions)!;
            capture.Activities.Count(a => a.SourceName == "System.Net.Http").Should().Be(4);
        }
        else
        {
            var start = DateTimeOffset.UnixEpoch;
            var tags = new Dictionary<string, string>();
            if (scenario == "enriched")
            {
                tags["server.address"] = "127.0.0.1";
                tags["url.full"] = "http://127.0.0.1/sanitized";
                tags["http.request.method"] = "GET";
            }
            capture = new ActivityCapture(Environment.ProcessId, ["System.Net.Http"], start, TimeSpan.FromSeconds(1),
                1, 1, [new("System.Net.Http", "System.Net.Http.HttpRequestOut", "http", null, Trace,
                    "1111111111111111", null, start, start.AddMilliseconds(50), TimeSpan.FromMilliseconds(50), tags)], [], []);
        }
        using var services = Services(new CaptureReplay(capture));
        var (exit, json) = await ExecuteAsync(services, ["collect", "--kind", "activities", "--source", "System.Net.Http", "--json"]);
        exit.Should().Be(0);
        var returned = json.GetProperty("data").Deserialize<ActivityCapture>(JsonOptions)!;
        returned.Should().BeEquivalentTo(capture);
        var handle = services.GetRequiredService<IDiagnosticHandleStore>()
            .TryGetLatestByKind(CollectionHandleKinds.Activities, Environment.ProcessId)!;
        var (listExit, list) = await ExecuteAsync(services,
            ["query", "--handle", handle.Id, "--view", "activities", "--top", "100", "--json"], session: true);
        listExit.Should().Be(0, list.ToString());
        list.GetProperty("data").GetProperty("payload").GetProperty("activities")
            .Deserialize<CapturedActivity[]>(JsonOptions).Should().BeEquivalentTo(capture.Activities);
        foreach (var activity in capture.Activities.Where(a => a.SourceName == "System.Net.Http"))
        {
            var (traceExit, trace) = await ExecuteAsync(services,
                ["query", "--handle", handle.Id, "--view", "trace", "--trace-id", activity.TraceId!, "--json"], session: true);
            traceExit.Should().Be(0, trace.ToString());
            var span = trace.GetProperty("data").GetProperty("payload").GetProperty("spans").EnumerateArray().Single();
            span.GetProperty("spanId").GetString().Should().Be(activity.SpanId);
            var tags = span.GetProperty("tags").Deserialize<Dictionary<string, string>>()!;
            tags.Should().NotContainKey("server.address").And.NotContainKey("url.full");
            if (scenario is "empty" or "net8.0-plain")
            {
                activity.Tags.Should().BeEmpty();
                tags.Should().BeEmpty();
            }
            else
            {
                activity.Tags["server.address"].Should().Be("127.0.0.1");
                tags["http.request.method"].Should().Be("GET");
            }
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 0)]
    [InlineData(3, 0)]
    public async Task OneShot_TargetedCapture_ForwardsIndependentBudgetAndSerializesCoreProvenance(int cap, int dropped)
    {
        var collector = new EventStreamCollector();
        using var services = Services(collector);
        var (exit, json) = await ExecuteAsync(services,
            ["collect", "--kind", "activities", "--trace-id", $" {Trace.ToUpperInvariant()} ",
             "--max-matched-activities", cap.ToString(CultureInfo.InvariantCulture),
             "--max-events", "1", "--source", "test-source", "--duration", "3", "--json"]);

        exit.Should().Be(0);
        var capture = json.GetProperty("data").Deserialize<ActivityCapture>(JsonOptions)!;
        capture.Activities.Select(a => a.OperationName).Should().Equal(cap == 1 ? ["child"] : ["child", "parent"]);
        capture.Retention.Should().Be(new ActivityRetention(Trace, cap, 14, 2, Math.Min(cap, 2), dropped, 12));
        capture.TotalActivities.Should().Be(14);
        capture.CompletedActivities.Should().Be(14);
        collector.LastTrace.Should().Be($" {Trace.ToUpperInvariant()} ");
        collector.LastExploratoryCap.Should().Be(1);
        collector.LastMatchingCap.Should().Be(cap);
        collector.LastSources.Should().Equal("test-source");
        collector.LastDuration.Should().Be(TimeSpan.FromSeconds(3));
        json.GetProperty("summary").GetString().Should().Contain($"dropped matching={dropped}");
        json.GetProperty("summary").GetString()!.Contains("Retention truncation", StringComparison.Ordinal).Should().Be(dropped > 0);
        json.GetProperty("handleNotice").GetString().Should().Contain("one-shot");
    }

    [Theory]
    [InlineData(false, false, 200, 14, 0)]
    [InlineData(false, true, 1, 1, 13)]
    [InlineData(true, true, 200, 2, 0)]
    public async Task OneShot_DefaultExploratoryAndTargetedBudgetsRemainIndependent(
        bool targeted, bool explicitExploratoryCap, int effectiveCap, int retained, int dropped)
    {
        var collector = new EventStreamCollector();
        using var services = Services(collector);
        var args = new List<string> { "collect", "--kind", "activities", "--json" };
        if (targeted) args.AddRange(["--trace-id", Trace]);
        if (explicitExploratoryCap) args.AddRange(["--max-events", "1"]);
        var (exit, json) = await ExecuteAsync(services, args);
        exit.Should().Be(0);
        var capture = json.GetProperty("data").Deserialize<ActivityCapture>(JsonOptions)!;
        capture.Activities.Should().HaveCount(retained);
        capture.Activities[0].OperationName.Should().Be(targeted ? "child" : "noise");
        capture.Retention!.EffectiveCap.Should().Be(effectiveCap);
        capture.Retention.DroppedMatchingActivities.Should().Be(dropped);
        capture.Retention.NonMatchingActivities.Should().Be(targeted ? 12 : 0);
        capture.Retention.MatchingActivities.Should().Be(targeted ? 2 : 14);
        capture.Retention.AppliedTraceId.Should().Be(targeted ? Trace : null);
    }

    [Theory]
    [InlineData("", "2", "traceId")]
    [InlineData(" ", "2", "traceId")]
    [InlineData("00000000000000000000000000000000", "2", "traceId")]
    [InlineData("bad", "2", "traceId")]
    [InlineData("abcdef0123456789abcdef0123456789", "0", "maxMatchedActivities")]
    [InlineData("abcdef0123456789abcdef0123456789", "-1", "maxMatchedActivities")]
    public async Task InvalidValues_ReturnCoreErrorEnvelopeBeforeCollection(string trace, string budget, string detail)
    {
        var collector = new EventStreamCollector();
        using var services = Services(collector);
        var (exit, json) = await ExecuteAsync(services,
            ["collect", "--kind", "activities", "--trace-id", trace, "--max-matched-activities", budget, "--json"]);
        exit.Should().Be(1);
        json.GetProperty("error").GetProperty("kind").GetString().Should().Be("InvalidArgument");
        json.GetProperty("error").GetProperty("detail").GetString().Should().Be(detail);
        collector.Calls.Should().Be(0);
        services.GetRequiredService<Resolver>().Calls.Should().Be(0);
    }

    [Fact]
    public async Task MaxEventsStillUsesExistingCoreValidationEvenWhenTargeted()
    {
        var collector = new EventStreamCollector();
        using var services = Services(collector);
        var (exit, json) = await ExecuteAsync(services,
            ["collect", "--kind", "activities", "--trace-id", Trace, "--max-events", "0", "--json"]);
        exit.Should().Be(1);
        json.GetProperty("error").GetProperty("detail").GetString().Should().Be("maxActivities");
        collector.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("collect", "cpu", "--trace-id", "abcdef0123456789abcdef0123456789")]
    [InlineData("collect", "counters", "--max-matched-activities", "2")]
    [InlineData("collect", "activities", "--max-matched-activities", "2")]
    [InlineData("query", "", "--max-matched-activities", "2")]
    [InlineData("processes", "", "--trace-id", "abcdef0123456789abcdef0123456789")]
    public void InactiveOptionsAreExplicitUsageErrorsInOneShotAndSession(string command, string kind, string flag, string value)
    {
        var args = new List<string> { command, flag, value };
        if (kind.Length > 0) args.AddRange(["--kind", kind]);
        CliCommandExecution.TryPrepareOneShot(args, out _, out var oneShot).Should().BeFalse();
        oneShot!.Text.Should().Contain(flag);
        CliCommandExecution.TryPrepareSession(args, null, out _, out var session).Should().BeFalse();
        session!.Text.Should().Contain(flag);
    }

    [Theory]
    [InlineData("not-an-int")]
    [InlineData("2147483648")]
    public void MatchingBudgetParserRejectsNonIntegers(string value)
    {
        CliOptions.Parse(["collect", "--kind", "activities", "--max-matched-activities", value], out var error)
            .Should().BeNull();
        error.Should().Contain("--max-matched-activities").And.Contain("integer");
    }

    [Fact]
    public void HelpAndCompletionAdvertiseBothCollectionOptionsWithoutChangingQuery()
    {
        CliHelp.ForCommand("collect").Should().Contain("--trace-id").And.Contain("--max-matched-activities");
        SessionReplCompletion.GetCandidates(["collect"], "--", null)
            .Should().Contain("--trace-id").And.Contain("--max-matched-activities");
        SessionReplCompletion.GetCandidates(["collect", "--max-matched-activities"], "", null).Should().BeEmpty();
        SessionReplCompletion.GetCandidates(["query"], "--", null).Should().Contain("--trace-id")
            .And.NotContain("--max-matched-activities");
    }

    [Fact]
    public async Task Session_CollectThenQuery_ReusesTargetedArtifactAndPreservesBoundPidAndTraceOption()
    {
        var collector = new EventStreamCollector();
        using var services = Services(collector);
        var root = Path.Combine("artifacts", $"cli-activities-{Guid.NewGuid():N}");
        using var input = new StringReader(
            "collect --kind activities --trace-id bad --json\n" +
            $"collect --kind activities --trace-id {Trace} --max-matched-activities 0 --json\n" +
            $"collect --kind activities --trace-id \" {Trace.ToUpperInvariant()} \" --max-matched-activities 2 --max-events 1 --json\n" +
            $"query --latest-of-kind activities --view trace --trace-id {Trace}\nexit\n");
        using var stdout = new StringWriter(CultureInfo.InvariantCulture);
        using var stderr = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            var exit = await SessionRepl.RunAsync(services, new MutableArtifactRootProvider(root),
                input, stdout, stderr, Environment.ProcessId, CancellationToken.None, interactiveSafetyOverride: false);
            exit.Should().Be(0);
            collector.Calls.Should().Be(1, stderr.ToString());
            collector.LastPid.Should().Be(Environment.ProcessId);
            var store = services.GetRequiredService<IDiagnosticHandleStore>();
            var handle = store.TryGetLatestByKind(CollectionHandleKinds.Activities, Environment.ProcessId)!;
            var capture = store.TryGet<ActivityCapture>(handle.Id)!;
            capture.Retention.Should().Be(new ActivityRetention(Trace, 2, 14, 2, 2, 0, 12));
            stdout.ToString().Should().Contain("view=trace").And.Contain("\"canClaimComplete\": false")
                .And.Contain("parent").And.Contain("child").And.Contain("\"appliedTraceId\"")
                .And.Contain("\"effectiveCap\": 2").And.Contain("\"kind\": \"InvalidArgument\"")
                .And.Contain("\"detail\": \"traceId\"").And.Contain("\"detail\": \"maxMatchedActivities\"");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<(int Exit, JsonElement Json)> ExecuteAsync(IServiceProvider services, IReadOnlyList<string> args, bool session = false)
    {
        var valid = session
            ? CliCommandExecution.TryPrepareSession(args, null, out var prepared, out var response)
            : CliCommandExecution.TryPrepareOneShot(args, out prepared, out response);
        valid.Should().BeTrue(response?.Text);
        using var stdout = new StringWriter(CultureInfo.InvariantCulture);
        using var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var outcome = await CliCommandExecution.ExecuteAsync(services, prepared!, stdout, stderr,
            new CliExecutionOptions(session ? CliExecutionContext.Session : CliExecutionContext.OneShot, AnsiEnabled: false, ShowProgress: false),
            CancellationToken.None);
        using var document = JsonDocument.Parse(stdout.ToString());
        return (outcome.ExitCode, document.RootElement.Clone());
    }

    private static ServiceProvider Services(IActivityCollector collector)
        => new ServiceCollection()
            .AddSingleton<IDiagnosticHandleStore>(new MemoryDiagnosticHandleStore())
            .AddSingleton<IActivityCollector>(collector)
            .AddSingleton<Resolver>()
            .AddSingleton<IProcessContextResolver>(sp => sp.GetRequiredService<Resolver>())
            .BuildServiceProvider();

    private sealed class CaptureReplay(ActivityCapture capture) : IActivityCollector
    {
        public Task<ActivityCapture> CollectAsync(int processId, TimeSpan duration, IReadOnlyList<string>? sources = null,
            int maxActivities = 200, CancellationToken cancellationToken = default) => Task.FromResult(capture);
    }

    private sealed class Resolver : IProcessContextResolver
    {
        internal int Calls { get; private set; }
        public Task<ProcessContextResolution> ResolveAsync(int? requestedProcessId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new ProcessContextResolution(
                new ProcessContext(requestedProcessId ?? Environment.ProcessId, RuntimeFlavor.CoreClr, true, true, requestedProcessId is null), null));
        }
    }

    private sealed class EventStreamCollector : IActivityCollector
    {
        internal int Calls { get; private set; }
        internal int LastPid { get; private set; }
        internal string? LastTrace { get; private set; }
        internal int LastExploratoryCap { get; private set; }
        internal int LastMatchingCap { get; private set; }
        internal IReadOnlyList<string>? LastSources { get; private set; }
        internal TimeSpan LastDuration { get; private set; }

        public Task<ActivityCapture> CollectAsync(int processId, TimeSpan duration, IReadOnlyList<string>? sources = null,
            int maxActivities = 200, CancellationToken cancellationToken = default)
            => CollectAsync(processId, duration, sources, maxActivities, null, 200, cancellationToken);

        public Task<ActivityCapture> CollectAsync(int processId, TimeSpan duration, IReadOnlyList<string>? sources,
            int maxActivities, string? traceId, int maxMatchedActivities, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastPid = processId;
            LastTrace = traceId;
            LastExploratoryCap = maxActivities;
            LastMatchingCap = maxMatchedActivities;
            LastSources = sources;
            LastDuration = duration;
            var state = new ActivityRetentionState(maxActivities, traceId, maxMatchedActivities);
            var start = DateTimeOffset.UnixEpoch;
            var noise = new CapturedActivity("test-source", "noise", "noise", null, "ffffffffffffffffffffffffffffffff",
                "ffffffffffffffff", null, start, start.AddMilliseconds(1), TimeSpan.FromMilliseconds(1), new Dictionary<string, string>());
            for (var i = 0; i < 5; i++) state.Observe(noise);
            state.Observe(noise with { OperationName = "child", TraceId = Trace, SpanId = "2222222222222222",
                ParentSpanId = "1111111111111111", StartedAt = start.AddMilliseconds(1),
                StoppedAt = start.AddMilliseconds(9), Duration = TimeSpan.FromMilliseconds(8) });
            state.Observe(noise);
            state.Observe(noise with { OperationName = "parent", TraceId = Trace, SpanId = "1111111111111111",
                StoppedAt = start.AddMilliseconds(10), Duration = TimeSpan.FromMilliseconds(10) });
            state.Observe(noise);
            for (var i = 0; i < 5; i++) state.Observe(noise);
            return Task.FromResult(new ActivityCapture(processId, sources, start, duration,
                state.ObservedActivities, state.ObservedActivities, state.Activities, [], [], state.Retention));
        }
    }
}
