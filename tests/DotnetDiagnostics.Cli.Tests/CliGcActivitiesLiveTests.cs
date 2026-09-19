using System.Globalization;
using System.Text.Json;
using DotnetDiagnostics.Cli;
using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Hosting;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.UseCases;
using DotnetDiagnostics.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace DotnetDiagnostics.Cli.Tests;

public sealed class CliGcActivitiesLiveTests(ITestOutputHelper output)
{
    internal const int NegativeOuterTimeoutMs = 30_000;
    internal const int PositiveOuterTimeoutMs = 40_000;
    internal static readonly TimeSpan NegativeBodyTimeout = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan PositiveBodyTimeout = TimeSpan.FromSeconds(25);

    [Theory(Timeout = NegativeOuterTimeoutMs)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrTargetExitStopsBothOwnedStreamsBeforeRequestedLongWindow(bool exitTarget)
    {
        using var work = new CancellationTokenSource(WorkTimeout(NegativeOuterTimeoutMs));
        await using var sample = await LiveSampleProcess.StartPublishedAsync("CoreClrSample",
            new LiveSampleOptions { WaitForHttpReady = true, ReadinessPath = "/weatherforecast" }, work.Token);
        using var http = new HttpClient { BaseAddress = new Uri(sample.BaseUrl), Timeout = TimeSpan.FromSeconds(3) };
        using var deadline = CreateBodyDeadline(NegativeBodyTimeout, work.Token);
        var activityReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gcReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var services = Services(
            new EventPipeGcCollector { CollectionStarted = _ => gcReady.TrySetResult() },
            new EventPipeActivityCollector { ActivityObserved = () => activityReady.TrySetResult() });
        var collection = CliGcActivitiesTests.ExecuteAsync(services,
            ["collect", "--kind", "gc-activities", "--pid", sample.ProcessId.ToString(CultureInfo.InvariantCulture),
             "--duration", "300", "--source", "CoreClrSample.Activities", "--json"],
            deadline.Token);
        try
        {
            for (var attempt = 0; attempt < 60 && !(activityReady.Task.IsCompleted && gcReady.Task.IsCompleted); attempt++)
            {
                using var response = await http.GetAsync("/activity?delayMs=1&collectGc=true", deadline.Token);
                response.EnsureSuccessStatusCode();
                await Task.WhenAny(Task.WhenAll(activityReady.Task, gcReady.Task), Task.Delay(50, deadline.Token));
            }
            activityReady.Task.IsCompletedSuccessfully.Should().BeTrue();
            gcReady.Task.IsCompletedSuccessfully.Should().BeTrue();
            if (exitTarget) sample.Process.Kill(entireProcessTree: true);
            else await deadline.CancelAsync();
            var result = await collection.WaitAsync(TimeSpan.FromSeconds(12));
            var capture = result.Json.GetProperty("data").Deserialize<GcActivitiesCapture>(CliGcActivitiesTests.JsonOptions)!;
            result.Exit.Should().NotBe(0);
            capture.Overlay.Should().BeNull();
            capture.Gc.Handle.Should().BeNull();
            capture.Activities.Handle.Should().BeNull();
            capture.Gc.UnavailableReason.Should().NotBeNullOrEmpty();
            capture.Activities.UnavailableReason.Should().NotBeNullOrEmpty();
            if (!exitTarget) sample.IsRunning.Should().BeTrue();
            output.WriteLine($"exitTarget={exitTarget}; {result.Json}");
        }
        finally { await deadline.CancelAsync(); await collection; }
    }

    [Theory(Timeout = PositiveOuterTimeoutMs)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public async Task LiveWorkflow_ObservedBothStreamsBeforeTargetedGcSpan_AndQueriesRealArtifacts(bool repl, int matchingCap)
    {
        using var work = new CancellationTokenSource(WorkTimeout(PositiveOuterTimeoutMs));
        await using var sample = await LiveSampleProcess.StartPublishedAsync("CoreClrSample",
            new LiveSampleOptions { WaitForHttpReady = true, ReadinessPath = "/weatherforecast" }, work.Token);
        using var http = new HttpClient { BaseAddress = new Uri(sample.BaseUrl), Timeout = TimeSpan.FromSeconds(3) };
        using var deadline = CreateBodyDeadline(PositiveBodyTimeout, work.Token);
        var activityObserved = 0;
        var gcObserved = 0;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void CheckReady()
        {
            if (Volatile.Read(ref activityObserved) >= 12 && Volatile.Read(ref gcObserved) >= 1)
                ready.TrySetResult();
        }
        var activities = new EventPipeActivityCollector
        {
            ActivityObserved = () => { Interlocked.Increment(ref activityObserved); CheckReady(); },
        };
        var readiness = new CliGcActivitiesReadiness();
        var gc = readiness.CreateCollector(_ => { Interlocked.Increment(ref gcObserved); CheckReady(); });
        using var services = Services(gc, activities);
        var command = $"collect --kind gc-activities --pid {sample.ProcessId.ToString(CultureInfo.InvariantCulture)} " +
            $"--duration 10 --source CoreClrSample.Activities --trace-id {CliGcActivitiesTests.Trace} " +
            $"--max-events 2 --max-matched-activities {matchingCap.ToString(CultureInfo.InvariantCulture)} --max-gc-events 100 --json";
        var root = Path.Combine("artifacts", $"cli-gc-activities-live-{Guid.NewGuid():N}");
        using var stdout = new StringWriter(CultureInfo.InvariantCulture);
        using var stderr = new StringWriter(CultureInfo.InvariantCulture);
        using var input = new CliGcActivitiesTests.AcquiredHandleInput(services.GetRequiredService<IDiagnosticHandleStore>(), command);
        Task<(int Exit, JsonElement Json)>? oneShot = null;
        Task<int>? session = null;
        if (repl)
            session = SessionRepl.RunAsync(services, new MutableArtifactRootProvider(root), input, stdout, stderr,
                sample.ProcessId, deadline.Token, interactiveSafetyOverride: false);
        else oneShot = CliGcActivitiesTests.ExecuteAsync(services, command.Split(' '), deadline.Token);
        try
        {
            // After the non-GC stream marker, the bounded prefix proves BOTH collectors observe
            // workload events and creates unrelated trace-budget pressure before the target span.
            await readiness.ObserveWorkloadAsync(ready.Task, async cancellationToken =>
            {
                using var noise = await http.GetAsync("/activity?delayMs=1&collectGc=true", cancellationToken);
                noise.EnsureSuccessStatusCode();
            }, deadline.Token);
            ready.Task.IsCompletedSuccessfully.Should().BeTrue("EACH collector must observe events before target workload");
            Volatile.Read(ref activityObserved).Should().BeGreaterThan(2);
            Volatile.Read(ref gcObserved).Should().BeGreaterThan(0);
            using var request = new HttpRequestMessage(HttpMethod.Get, "/activity?delayMs=20&collectGc=true");
            request.Headers.TryAddWithoutValidation("traceparent", $"00-{CliGcActivitiesTests.Trace}-9999999999999999-01");
            using var response = await http.SendAsync(request, deadline.Token);
            response.EnsureSuccessStatusCode();
            using var targetJson = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
            targetJson.RootElement.GetProperty("traceId").GetString().Should().Be(CliGcActivitiesTests.Trace);
            var targetSpanId = targetJson.RootElement.GetProperty("spanId").GetString();

            var store = services.GetRequiredService<IDiagnosticHandleStore>();
            GcActivitiesCapture? inline = null;
            if (session is not null)
            {
                (await session).Should().Be(0, stderr.ToString());
                input.ActivityHandle.Should().NotBeNullOrEmpty();
                input.GcHandle.Should().NotBeNullOrEmpty();
                stdout.ToString().Should().Contain("gc-overlay").And.Contain("\"measurementStatus\": \"no-detected-loss\"");
            }
            else
            {
                var result = await oneShot!;
                result.Exit.Should().Be(0);
                inline = result.Json.GetProperty("data").Deserialize<GcActivitiesCapture>(CliGcActivitiesTests.JsonOptions)!;
                inline.Status.Should().Be("captured", inline.OverlayUnavailableReason);
                inline.Overlay.Should().NotBeNull();
                inline.Overlay!.TotalGcOverlapMs.Should().BeGreaterThan(0);
                inline.IntersectionEnd.Should().BeAfter(inline.IntersectionStart!.Value);
                output.WriteLine(JsonSerializer.Serialize(inline, CliGcActivitiesTests.JsonOptions));
            }
            var gcHandle = store.TryGetLatestByKind(CollectionHandleKinds.GcEvents, sample.ProcessId)!;
            var activityHandle = store.TryGetLatestByKind(CollectionHandleKinds.Activities, sample.ProcessId)!;
            var gcCapture = store.TryGet<GcSummary>(gcHandle.Id)!;
            var activityCapture = store.TryGet<ActivityCapture>(activityHandle.Id)!;
            activityCapture.Activities.Should().HaveCount(matchingCap);
            activityCapture.Retention!.NonMatchingActivities.Should().BeGreaterThanOrEqualTo(12);
            activityCapture.Retention.MatchingActivities.Should().Be(2);
            activityCapture.Retention.RetainedMatchingActivities.Should().Be(matchingCap);
            activityCapture.Retention.DroppedMatchingActivities.Should().Be(2 - matchingCap);
            activityCapture.Retention.AppliedTraceId.Should().Be(CliGcActivitiesTests.Trace);
            activityCapture.Activities.Should().Contain(a => a.ParentSpanId == targetSpanId);
            activityCapture.Observation!.Completion.Should().Be("normal-stop");
            activityCapture.Observation.EventsLost.Should().Be(0);
            gcCapture.Suspension!.IsAuthoritative.Should().BeTrue();
            gcCapture.Suspension.MeasurementVersion.Should().Be(2);
            gcCapture.Suspension.Intervals.Should().NotBeEmpty();
            gcCapture.Suspension.Intervals.Should().OnlyContain(p => p.Reason == 1 || p.Reason == 6);
            gcCapture.Suspension.DroppedIntervals.Should().Be(0);
            GcCorrelationHandles.Resolve(store, new HandleLookup(activityHandle, activityCapture), gcHandle.Id, out var evidence)
                .Should().BeNull();
            var overlay = GcActivityCorrelator.Correlate(activityCapture, evidence!, 20);
            overlay.TotalGcOverlapMs.Should().BeGreaterThan(0);
            overlay.ImpactedActivities.Should().Contain(a => a.OperationName == "CoreClrSample.Inner");
            overlay.LifetimeCompatibility.Should().Be("matched");
            overlay.CorrelationTruncated.Should().Be(matchingCap == 1);
            if (inline is not null)
            {
                inline.Activities.Handle!.Id.Should().Be(activityHandle.Id);
                inline.Gc.Handle!.Id.Should().Be(gcHandle.Id);
            }
            output.WriteLine($"mode={(repl ? "session" : "one-shot")}; GC readiness={gcObserved}; activity readiness={activityObserved}; targetSpan={targetSpanId}");
            output.WriteLine(JsonSerializer.Serialize(overlay, CliGcActivitiesTests.JsonOptions));
        }
        finally
        {
            await deadline.CancelAsync();
            if (oneShot is not null) await oneShot;
            if (session is not null) await session;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

    }

    private static ServiceProvider Services(IGcCollector gc, IActivityCollector activities)
        => new ServiceCollection().AddDiagnosticCoreServices(new SecurityOptions())
            .AddSingleton(gc).AddSingleton(activities).BuildServiceProvider();

    internal static TimeSpan WorkTimeout(int outerTimeoutMs)
        => TimeSpan.FromMilliseconds(outerTimeoutMs) - LiveSampleProcess.CleanupTimeout - TimeSpan.FromSeconds(2);

    internal static CancellationTokenSource CreateBodyDeadline(TimeSpan bodyTimeout, CancellationToken workToken)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(workToken);
        deadline.CancelAfter(bodyTimeout);
        return deadline;
    }
}
