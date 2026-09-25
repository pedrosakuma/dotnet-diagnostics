using System.Globalization;
using System.Text.Json;
using DotnetDiagnostics.Cli;
using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Internal;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.UseCases;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetDiagnostics.Cli.Tests;

public sealed class CliGcActivitiesTests
{
    internal const string Trace = "abcdef0123456789abcdef0123456789";
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string Command = "collect --kind gc-activities --duration 3 --source test-source --trace-id " + Trace +
        " --max-matched-activities 2 --max-events 1 --max-gc-events 7 --top 1 --json";

    [Fact]
    public async Task PersistedWorkflowKeepsBothChildrenInOneLogicalCapture()
    {
        var root = Path.Combine(Environment.CurrentDirectory, ".validation", "cli-durable-gc-activities-" + Guid.NewGuid().ToString("N"));
        try
        {
            var boundary = new Boundary();
            using var services = Services(boundary, boundary, Environment.ProcessId);
            var (exit, json) = await ExecuteAsync(services, [.. Command.Split(' '), "--persist", "--capture-root", root]);
            exit.Should().Be(0, json.ToString());
            boundary.AssertArguments();
            var captured = json.GetProperty("capture").Deserialize<CaptureInfo>(JsonOptions)!;
            captured.State.Should().Be(CaptureState.Sealed);
            captured.Artifacts.Select(a => a.Kind).Should().Contain(CollectionHandleKinds.GcEvents)
                .And.Contain(CollectionHandleKinds.Activities);
            var store = new SqliteCaptureStore(new CliCaptureRootProvider(root));
            (await store.ListAsync(CliCaptureRootProvider.CurrentAccess())).Captures.Should().ContainSingle();
            using var fresh = Services(new Boundary(), new Boundary(), Environment.ProcessId);
            foreach (var (kind, view) in new[]
            {
                (CollectionHandleKinds.GcEvents, "summary"),
                (CollectionHandleKinds.Activities, "activities"),
            })
            {
                var artifact = captured.Artifacts.Single(a => a.Kind == kind);
                var (queryExit, query) = await ExecuteAsync(fresh,
                    ["query", "--capture-id", captured.CaptureId, "--artifact-id", artifact.ArtifactId,
                        "--capture-root", root, "--view", view, "--json"]);
                queryExit.Should().Be(0, query.ToString());
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OneShot_ParsesDispatchesRealCoordinatorAndSerializesUsefulOverlayWithIndependentBudgets()
    {
        var boundary = new Boundary();
        using var services = Services(boundary, boundary, Environment.ProcessId);
        var (exit, json) = await ExecuteAsync(services, Command.Split(' '));
        exit.Should().Be(0);
        var capture = json.GetProperty("data").Deserialize<GcActivitiesCapture>(JsonOptions)!;
        AssertCapture(capture, services);
        boundary.AssertArguments();
        capture.Overlay!.ImpactedCount.Should().Be(2);
        capture.Overlay.ImpactedActivities.Should().HaveCount(1);
        capture.Overlay.OutputOmittedActivities.Should().Be(1);
        capture.Overlay.TotalGcOverlapMs.Should().Be(20, "each span legitimately overlaps the same 10ms suspension");
        json.GetProperty("handleNotice").GetString().Should().Contain("one-shot");
    }

    [Fact]
    public async Task Repl_AcquiresBothThenQueriesActualReturnedHandles()
    {
        var boundary = new Boundary();
        using var services = Services(boundary, boundary, Environment.ProcessId);
        using var input = new AcquiredHandleInput(services.GetRequiredService<IDiagnosticHandleStore>(), Command);
        var root = Path.Combine("artifacts", $"cli-gc-activities-{Guid.NewGuid():N}");
        using var stdout = new StringWriter(CultureInfo.InvariantCulture);
        using var stderr = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            var exit = await SessionRepl.RunAsync(services, new MutableArtifactRootProvider(root),
                input, stdout, stderr, Environment.ProcessId, CancellationToken.None, interactiveSafetyOverride: false);
            exit.Should().Be(0, stderr.ToString());
            boundary.AssertArguments();
            input.GcHandle.Should().NotBeNullOrEmpty();
            input.ActivityHandle.Should().NotBeNullOrEmpty();
            stdout.ToString().Should().Contain("\"totalGcOverlapMs\": 20").And.Contain("gc-overlay");
            services.GetRequiredService<Resolver>().Calls.Should().Be(1);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FailedSideRemainsExplicitInProductionJson()
    {
        var boundary = new Boundary { FailGc = true };
        using var services = Services(boundary, boundary, Environment.ProcessId);
        var (_, json) = await ExecuteAsync(services, Command.Split(' '));
        var capture = json.GetProperty("data").Deserialize<GcActivitiesCapture>(JsonOptions)!;
        capture.Status.Should().Be("partial");
        capture.Gc.Status.Should().Be("failed");
        capture.Gc.UnavailableReason.Should().Contain("test-start-failure");
        capture.Gc.Handle.Should().BeNull();
        capture.Activities.Handle.Should().NotBeNull();
        capture.Overlay.Should().BeNull();
        capture.OverlayUnavailableReason.Should().Contain("test-start-failure");
        json.GetProperty("summary").GetString().Should().Contain("partial").And.Contain("failed");
    }

    [Theory]
    [InlineData("collect --kind gc --max-gc-events 1")]
    [InlineData("processes --max-gc-events 1")]
    [InlineData("collect --kind gc-activities --max-matched-activities 1")]
    public void InactiveFlagsAreUsageErrors(string command)
    {
        CliCommandExecution.TryPrepareOneShot(command.Split(' '), out _, out var response).Should().BeFalse();
        response!.WriteToStdout.Should().BeFalse();
        CliCommandExecution.TryPrepareSession(command.Split(' '), null, out _, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("--max-gc-events", "0")]
    [InlineData("--max-events", "10001")]
    [InlineData("--max-matched-activities", "10001")]
    [InlineData("--duration", "301")]
    [InlineData("--top", "101")]
    [InlineData("--trace-id", "invalid")]
    public async Task BudgetsAreValidatedBeforeResolutionOrCollection(string flag, string value)
    {
        var boundary = new Boundary();
        using var services = Services(boundary, boundary, Environment.ProcessId);
        var (_, json) = await ExecuteAsync(services,
            ["collect", "--kind", "gc-activities", "--trace-id", Trace, flag, value, "--json"]);
        json.GetProperty("error").GetProperty("kind").GetString().Should().Be("InvalidArgument");
        services.GetRequiredService<Resolver>().Calls.Should().Be(0);
        boundary.GcCalls.Should().Be(0);
        boundary.ActivityCalls.Should().Be(0);
    }

    [Fact]
    public void HelpAndCompletionExposeCliOnlyWorkflowAndSeparateBudget()
    {
        CliHelp.ForCommand("collect").Should().Contain("gc-activities").And.Contain("--max-gc-events");
        SessionReplCompletion.GetCandidates(["collect", "--kind"], "gc-", null).Should().Contain("gc-activities");
        SessionReplCompletion.GetCandidates(["collect"], "--max-gc", null).Should().Contain("--max-gc-events");
        SessionReplCompletion.GetCandidates(["collect", "--max-gc-events"], "", null).Should().BeEmpty();
    }

    private static void AssertCapture(GcActivitiesCapture capture, ServiceProvider services)
    {
        capture.Status.Should().Be("captured");
        capture.Overlay.Should().NotBeNull();
        capture.Activities.Capture!.Retention.Should().Be(new ActivityRetention(Trace, 2, 14, 2, 2, 0, 12));
        capture.Gc.Capture!.Suspension!.MeasurementVersion.Should().Be(2);
        var store = services.GetRequiredService<IDiagnosticHandleStore>();
        store.TryGet<GcSummary>(capture.Gc.Handle!.Id).Should().NotBeNull();
        store.TryGet<ActivityCapture>(capture.Activities.Handle!.Id).Should().NotBeNull();
        services.GetRequiredService<Resolver>().Calls.Should().Be(1);
    }

    internal static ServiceProvider Services(IGcCollector gc, IActivityCollector activity, int pid)
        => new ServiceCollection().AddSingleton<IDiagnosticHandleStore>(new MemoryDiagnosticHandleStore())
            .AddSingleton(gc).AddSingleton(activity).AddSingleton(new Resolver(pid))
            .AddSingleton<IProcessContextResolver>(sp => sp.GetRequiredService<Resolver>()).BuildServiceProvider();

    internal static async Task<(int Exit, JsonElement Json)> ExecuteAsync(
        IServiceProvider services, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        CliCommandExecution.TryPrepareOneShot(args, out var prepared, out var response).Should().BeTrue(response?.Text);
        using var stdout = new StringWriter(CultureInfo.InvariantCulture);
        using var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var outcome = await CliCommandExecution.ExecuteAsync(services, prepared!, stdout, stderr,
            new CliExecutionOptions(CliExecutionContext.OneShot, AnsiEnabled: false, ShowProgress: false), cancellationToken);
        using var document = JsonDocument.Parse(stdout.ToString());
        return (outcome.ExitCode, document.RootElement.Clone());
    }

    internal sealed class AcquiredHandleInput(IDiagnosticHandleStore store, string command) : TextReader
    {
        private int _line;
        public string? GcHandle { get; private set; }
        public string? ActivityHandle { get; private set; }
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            if (_line++ == 0) return ValueTask.FromResult<string?>(command);
            if (_line == 2)
            {
                GcHandle = store.TryGetLatestByKind(CollectionHandleKinds.GcEvents)!.Id;
                ActivityHandle = store.TryGetLatestByKind(CollectionHandleKinds.Activities)!.Id;
                return ValueTask.FromResult<string?>($"query --handle {ActivityHandle} --view gc-overlay --gc-handle {GcHandle} --json");
            }
            return ValueTask.FromResult<string?>("exit");
        }
    }

    internal sealed class Resolver(int pid) : IProcessContextResolver
    {
        public int Calls { get; private set; }
        public Task<ProcessContextResolution> ResolveAsync(int? requestedProcessId, CancellationToken cancellationToken)
        {
            Calls++;
            if (requestedProcessId is > 0) requestedProcessId.Should().Be(pid);
            return Task.FromResult(new ProcessContextResolution(new(pid, RuntimeFlavor.CoreClr, true, true, requestedProcessId is null), null));
        }
    }

    private sealed class Boundary : IGcCollector, IActivityCollector
    {
        private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;
        private readonly DateTimeOffset? _lifetime = ProcessLifetime.TryReadStart(Environment.ProcessId);
        private readonly TaskCompletionSource _bothEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailGc { get; init; }
        public int GcCalls { get; private set; }
        public int ActivityCalls { get; private set; }
        public void AssertArguments() { GcCalls.Should().Be(1); ActivityCalls.Should().Be(1); }

        public async Task<GcSummary> CollectAsync(int processId, TimeSpan duration, int maxEvents = 200, CancellationToken cancellationToken = default)
        {
            GcCalls++;
            processId.Should().Be(Environment.ProcessId);
            duration.Should().Be(TimeSpan.FromSeconds(3));
            maxEvents.Should().Be(7);
            if (FailGc) throw new InvalidOperationException("test-start-failure");
            await _bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            return new(processId, _start, duration, 1, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100), [], [],
                Suspension: new("no-detected-loss", _start, _start + duration, _lifetime, "normal-stop",
                    TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10), 1, 0,
                    [new(_start.AddSeconds(1), _start.AddSeconds(1).AddMilliseconds(10), 1, 1, 1, 1, TimeSpan.Zero)],
                    new Dictionary<string, long>()));
        }

        public Task<ActivityCapture> CollectAsync(int processId, TimeSpan duration, IReadOnlyList<string>? sources = null,
            int maxActivities = 200, CancellationToken cancellationToken = default) => throw new InvalidOperationException("targeted overload required");

        public Task<ActivityCapture> CollectAsync(int processId, TimeSpan duration, IReadOnlyList<string>? sources,
            int maxActivities, string? traceId, int maxMatchedActivities, CancellationToken cancellationToken = default)
        {
            ActivityCalls++;
            processId.Should().Be(Environment.ProcessId);
            duration.Should().Be(TimeSpan.FromSeconds(3));
            sources.Should().Equal("test-source");
            maxActivities.Should().Be(1);
            traceId.Should().Be(Trace);
            maxMatchedActivities.Should().Be(2);
            _bothEntered.TrySetResult();
            var state = new ActivityRetentionState(maxActivities, traceId, maxMatchedActivities);
            for (var i = 0; i < 12; i++) state.Observe(Span("11111111111111111111111111111111", "noise"));
            state.Observe(Span(Trace, "parent"));
            state.Observe(Span(Trace, "child"));
            return Task.FromResult(new ActivityCapture(processId, sources, _start.AddMilliseconds(100), duration,
                state.ObservedActivities, state.ObservedActivities, state.Activities, [], [], state.Retention, _lifetime)
                { Observation = new(duration, "normal-stop", 0) });
        }

        private CapturedActivity Span(string trace, string operation) => new("test-source", operation, operation,
            null, trace, operation, null, _start.AddMilliseconds(200), _start.AddSeconds(2), TimeSpan.FromMilliseconds(1800),
            new Dictionary<string, string>());
    }
}
