using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Text.Json;
using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Internal;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Mcp.Tools;
using DotnetDiagnostics.Mcp.Security;
using Microsoft.Diagnostics.NETCore.Client;
using Xunit.Abstractions;

namespace DotnetDiagnostics.Core.Tests;

[Collection("LiveProcess")]
public sealed class HttpActivityTagLiveTests(ITestOutputHelper output)
{
    private static readonly string[] Sources = ["System.Net.Http", "HttpActivityTarget.Readiness"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [Theory]
    [InlineData("net8.0", false)]
    [InlineData("net8.0", true)]
    [InlineData("net9.0", false)]
    [InlineData("net9.0", true)]
    [InlineData("net10.0", false)]
    [InlineData("net10.0", true)]
    public async Task SourceTags_SurviveRawBridgeCaptureAndQueries(string framework, bool enrich)
        => await RunAsync(framework, enrich, false);

    [Theory]
    [InlineData("net8.0")]
    [InlineData("net9.0")]
    [InlineData("net10.0")]
    public async Task AuthorityBridge_ExportsOnlyAuthorityAndRealIdentity(string framework)
        => await RunAsync(framework, false, true);

    [Fact]
    public async Task Destination_StartBeforeCapture_RemainsUnavailable()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var token = budget.Token;
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DotnetDiagnostics.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var target = Path.Combine(root.FullName, "tests", "HttpActivityTarget", "bin", configuration, "net8.0", "HttpActivityTarget.dll");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                ArgumentList = { target, "window" }, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            },
        };
        Assert.True(process.Start());
        var stderr = process.StandardError.ReadToEndAsync();
        Task<ActivityCapture>? primer = null, late = null;
        try
        {
            var provenance = JsonSerializer.Deserialize<JsonElement>((await process.StandardOutput.ReadLineAsync(token))!);
            var expected = Environment.GetEnvironmentVariable("HTTP_ACTIVITY_EXPECTED_8");
            if (expected is not null) Assert.Equal(expected, provenance.GetProperty("runtime").GetString());
            var primerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            primer = new EventPipeActivityCollector { ActivityObserved = () => primerReady.TrySetResult() }
                .CollectAsync(process.Id, TimeSpan.FromSeconds(12), Sources, 200, token);
            await process.StandardInput.WriteLineAsync("observe");
            await primerReady.Task.WaitAsync(token);
            await process.StandardInput.WriteLineAsync("go");
            var start = JsonSerializer.Deserialize<JsonElement>((await process.StandardOutput.ReadLineAsync(token))!);
            Assert.Equal("diagnosticSource", start.GetProperty("kind").GetString());
            var lateReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            late = new EventPipeActivityCollector { ActivityObserved = () => lateReady.TrySetResult() }
                .CollectAsync(process.Id, TimeSpan.FromSeconds(10), Sources, 200, null, 200, true, token);
            await process.StandardInput.WriteLineAsync("late");
            await lateReady.Task.WaitAsync(token);
            await process.StandardInput.WriteLineAsync("release");
            var witness = new List<JsonElement>(2);
            while (await process.StandardOutput.ReadLineAsync(token) is { } line && line != "DONE")
            {
                Assert.True(witness.Count < 2);
                witness.Add(JsonSerializer.Deserialize<JsonElement>(line));
            }
            await Task.WhenAll(primer, late);
            var original = Assert.Single((await primer).Activities, a => a.SourceName == "System.Net.Http");
            var capture = await late;
            var activity = Assert.Single(capture.Activities, a => a.SourceName == "System.Net.Http");
            Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(activity with { Destination = null }));
            Assert.Equal(start.GetProperty("traceId").GetString(), activity.TraceId);
            Assert.Equal(start.GetProperty("spanId").GetString(), activity.SpanId);
            Assert.Equal("missing-start", activity.Destination?.Availability);
            Assert.Null(activity.Destination?.Host);
            Assert.Equal(0, capture.HttpDestinationCorrelation?.Starts);
            Assert.Equal(0, capture.HttpDestinationCorrelation?.Available);
            Assert.Equal(0, capture.Observation?.EventsLost);
            await process.StandardInput.WriteLineAsync("quit");
            await process.WaitForExitAsync(token);
            Assert.Equal(0, process.ExitCode);
            Assert.Empty(await stderr);
            if (Environment.GetEnvironmentVariable("HTTP_ACTIVITY_EVIDENCE") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(Path.Combine(directory, "missing-start.json"),
                    JsonSerializer.Serialize(new { provenance, start, witness, capture, original, process.ExitCode }, JsonOptions), token);
            }
        }
        finally
        {
            await budget.CancelAsync();
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            var tasks = new List<Task>(2);
            if (primer is not null) tasks.Add(primer);
            if (late is not null) tasks.Add(late);
            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) when (budget.IsCancellationRequested) { }
            output.WriteLine($"Owned target {process.Id} exited={process.HasExited}; stderr={await stderr}");
        }
    }

    private async Task RunAsync(string framework, bool enrich, bool bridge)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var token = budget.Token;
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DotnetDiagnostics.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var target = Path.Combine(root.FullName, "tests", "HttpActivityTarget", "bin", configuration, framework, "HttpActivityTarget.dll");
        Assert.True(File.Exists(target), target);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                ArgumentList = { target, bridge ? "destination" : enrich ? "enrich" : "plain" },
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        Assert.True(process.Start());
        var stderr = process.StandardError.ReadToEndAsync();
        Task<ActivityCapture>? collecting = null;
        Task<EventPipeCollectionRunner.Completion>? reading = null;
        var raw = new List<JsonElement>(64);
        var processingErrors = new ConcurrentQueue<Exception>();
        try
        {
            var provenance = JsonSerializer.Deserialize<JsonElement>(await ReadLine());
            output.WriteLine(provenance.ToString());
            Assert.StartsWith(framework[3..^2] + ".", provenance.GetProperty("runtime").GetString(), StringComparison.Ordinal);
            var expectedVersion = Environment.GetEnvironmentVariable("HTTP_ACTIVITY_EXPECTED_" + framework[3..^2]);
            if (expectedVersion is not null) Assert.Equal(expectedVersion, provenance.GetProperty("runtime").GetString());
            Assert.Equal("None", provenance.GetProperty("listenerSampling").GetString());

            var coreReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var rawReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var collector = new EventPipeActivityCollector { ActivityObserved = () => coreReady.TrySetResult() };
            collecting = collector.CollectAsync(process.Id, TimeSpan.FromSeconds(10), Sources, 200, null, 200, bridge, token);
            var arguments = EventPipeActivityCollector.BuildProviderArguments(Sources, bridge);
            using var session = await new DiagnosticsClient(process.Id).StartEventPipeSessionWithTimeoutAsync(
                [new EventPipeProvider("Microsoft-Diagnostics-DiagnosticSource", EventLevel.Verbose, 3, arguments)],
                requestRundown: false, circularBufferMB: 64, cancellationToken: token);
            // Separate raw session, identical subscription; preserve payload before the product parser.
            reading = EventPipeCollectionRunner.RunAsync(session, TimeSpan.FromSeconds(10), source =>
                source.Dynamic.All += e =>
                {
                    if (e.ProviderName != "Microsoft-Diagnostics-DiagnosticSource" ||
                        (!e.EventName.EndsWith("Stop", StringComparison.Ordinal) &&
                         !(bridge && e.PayloadNames.Contains("Arguments")))) return;
                    if (string.Equals(e.PayloadByName("SourceName")?.ToString(), "HttpActivityTarget.Readiness", StringComparison.Ordinal) ||
                        string.Equals(e.PayloadByName("ActivitySourceName")?.ToString(), "HttpActivityTarget.Readiness", StringComparison.Ordinal))
                        rawReady.TrySetResult();
                    if (raw.Count >= 100) throw new InvalidOperationException("Raw witness cap exceeded.");
                    raw.Add(JsonSerializer.SerializeToElement(new
                    {
                        e.ProviderName, e.EventName,
                        payload = e.PayloadNames.ToDictionary(name => name, e.PayloadByName),
                    }, JsonOptions));
                }, processingErrors.Enqueue, token);
            await process.StandardInput.WriteLineAsync("observe");
            await Task.WhenAll(coreReady.Task, rawReady.Task).WaitAsync(token);
            await process.StandardInput.WriteLineAsync("go");
            var witness = new List<JsonElement>(12);
            while (await ReadLine() is { } line && line != "DONE")
            {
                Assert.True(witness.Count < 12, "Bounded target witness inventory exceeded.");
                witness.Add(JsonSerializer.Deserialize<JsonElement>(line));
            }
            await Task.WhenAll(collecting, reading);
            var capture = await collecting;
            var rawCompletion = await reading;
            Assert.Empty(processingErrors);
            Assert.Equal("normal", rawCompletion.Status);
            Assert.Equal(0, rawCompletion.EventsLost);
            Assert.Equal("normal-stop", capture.Observation?.Completion);
            Assert.Equal(0, capture.Observation?.EventsLost);
            Assert.False(capture.Retention?.RetentionLimited);
            var captured = capture.Activities.Where(a => a.SourceName == "System.Net.Http").ToArray();
            Assert.Equal(4, captured.Length);
            if (bridge)
            {
                Assert.Equal(4, capture.HttpDestinationCorrelation?.Available);
                Assert.Equal(0, capture.HttpDestinationCorrelation?.DuplicateStarts);
                Assert.Equal(0, capture.HttpDestinationCorrelation?.DuplicateStops);
                Assert.Equal(2, captured.Select(a => a.Destination?.Port).Distinct().Count());
            }
            else
            {
                Assert.Null(capture.HttpDestinationCorrelation);
                Assert.All(captured, a => Assert.Null(a.Destination));
            }
            Assert.Equal(4, witness.Count(w => w.GetProperty("kind").GetString() == "source"));
            Assert.Equal(4, witness.Count(w => w.GetProperty("kind").GetString() == "diagnosticSource"));
            var outcomes = witness.Where(w => w.GetProperty("kind").GetString() == "outcome").ToArray();
            Assert.Equal(4, outcomes.Length);
            Assert.Equal(503, Assert.Single(outcomes, w => w.GetProperty("path").GetString() == "/unavailable").GetProperty("status").GetInt32());
            Assert.True(Assert.Single(outcomes, w => w.GetProperty("path").GetString() == "/cancel").GetProperty("cancelled").GetBoolean());
            var handles = new MemoryDiagnosticHandleStore();
            var handle = handles.Register(process.Id, CollectionHandleKinds.Activities, capture, TimeSpan.FromMinutes(1));
            var list = DiagnosticTools.QueryCollection(handles, new PrincipalAccessor(),
                new SensitiveDataRedactor(new SecurityOptions()), handle.Id, view: "activities", topN: 100);
            Assert.Null(list.Error);
            Assert.Equal(capture.Activities, Assert.IsType<ActivitiesListView>(list.Data!.Payload).Activities);
            var traces = new List<ActivityTraceProjection>(4);
            foreach (var activity in captured)
            {
                var source = Assert.Single(witness, w => w.GetProperty("kind").GetString() == "source" &&
                    w.GetProperty("spanId").GetString() == activity.SpanId);
                var diagnostic = Assert.Single(witness, w => w.GetProperty("kind").GetString() == "diagnosticSource" &&
                    w.GetProperty("spanId").GetString() == activity.SpanId);
                Assert.Equal("System.Net.Http", source.GetProperty("Name").GetString());
                Assert.Equal("System.Net.Http.HttpRequestOut", activity.OperationName);
                Assert.Equal(activity.OperationName, source.GetProperty("OperationName").GetString());
                Assert.Equal(activity.TraceId, source.GetProperty("traceId").GetString());
                Assert.Equal(activity.TraceId, diagnostic.GetProperty("traceId").GetString());
                Assert.Equal(activity.StartedAt.UtcTicks, source.GetProperty("startTicks").GetInt64());
                Assert.Equal(activity.Duration!.Value.Ticks, source.GetProperty("durationTicks").GetInt64());
                Assert.True(activity.Duration > TimeSpan.Zero);
                Assert.Equal(activity.StartedAt + activity.Duration, activity.StoppedAt);
                Assert.True(source.GetProperty("IsAllDataRequested").GetBoolean());
                Assert.True(source.GetProperty("Recorded").GetBoolean());
                Assert.Equal("127.0.0.1", diagnostic.GetProperty("host").GetString());
                var tags = source.GetProperty("tags").Deserialize<Dictionary<string, string>>()!;
                Assert.Equal(tags.OrderBy(p => p.Key, StringComparer.Ordinal), activity.Tags.OrderBy(p => p.Key, StringComparer.Ordinal));
                var rawArguments = Assert.Single(raw.Select(e => e.GetProperty("payload").GetProperty("Arguments"))
                    .Select(a => a.EnumerateArray().ToDictionary(p => p.GetProperty("Key").GetString()!, p => p.GetProperty("Value").GetString()!)),
                    a => a.GetValueOrDefault("SpanId") == activity.SpanId);
                Assert.Equal(activity.TraceId, rawArguments["TraceId"]);
                if (bridge)
                {
                    var start = Assert.Single(raw.Select(e => e.GetProperty("payload").GetProperty("Arguments"))
                        .Select(a => a.EnumerateArray().ToDictionary(p => p.GetProperty("Key").GetString()!, p => p.GetProperty("Value").GetString()!)),
                        a => a.GetValueOrDefault("ActivitySpanId") == activity.SpanId);
                    Assert.Equal(activity.TraceId, start["ActivityTraceId"]);
                    output.WriteLine(JsonSerializer.Serialize(start));
                    Assert.Equal(5, start.Count);
                    Assert.Equal("127.0.0.1", start["Host"]);
                    Assert.Equal("http", start["Scheme"]);
                    Assert.Equal(diagnostic.GetProperty("port").GetInt32().ToString(CultureInfo.InvariantCulture),
                        start["Port"]);
                    Assert.Equal(new HttpActivityDestination("available", "http", "127.0.0.1",
                        diagnostic.GetProperty("port").GetInt32(), "diagnostic-source-http-start"), activity.Destination);
                    foreach (var sensitive in new[] { "fixture-user", "fixture-password", "fixture-secret", "query-value", "fixture-fragment", "/fast", "/slow", "/cancel", "/unavailable" })
                        Assert.DoesNotContain(sensitive, JsonSerializer.Serialize(start), StringComparison.Ordinal);
                }
                Assert.Equal(activity.StartedAt.UtcTicks.ToString(CultureInfo.InvariantCulture), rawArguments["StartTimeTicks"]);
                Assert.Equal(activity.Duration.Value.Ticks.ToString(CultureInfo.InvariantCulture), rawArguments["DurationTicks"]);
                if (framework == "net8.0" && !enrich)
                {
                    Assert.Empty(tags);
                    Assert.Empty(rawArguments["Tags"]);
                }
                else
                {
                    Assert.Equal("127.0.0.1", tags["server.address"]);
                    Assert.Equal("GET", tags["http.request.method"]);
                    Assert.Contains(diagnostic.GetProperty("path").GetString()!, tags["url.full"], StringComparison.Ordinal);
                    foreach (var tag in tags) Assert.Contains($"[{tag.Key}, {tag.Value}]", rawArguments["Tags"], StringComparison.Ordinal);
                }
                var traceResult = DiagnosticTools.QueryCollection(handles, new PrincipalAccessor(),
                    new SensitiveDataRedactor(new SecurityOptions()), handle.Id, view: "trace", traceId: activity.TraceId);
                Assert.Null(traceResult.Error);
                var trace = Assert.IsType<ActivityTraceProjection>(traceResult.Data!.Payload);
                traces.Add(trace);
                var span = Assert.Single(trace.Spans);
                Assert.Equal(activity.SpanId, span.SpanId);
                Assert.Equal(activity.Destination, span.Destination);
                Assert.Equal(capture.HttpDestinationCorrelation, trace.HttpDestinationCorrelation);
                Assert.DoesNotContain("server.address", span.Tags.Keys);
                Assert.DoesNotContain("url.full", span.Tags.Keys);
                if (tags.Count == 0) Assert.Empty(span.Tags);
                else Assert.Equal("GET", span.Tags["http.request.method"]);
            }
            await process.StandardInput.WriteLineAsync("quit");
            await process.WaitForExitAsync(token);
            Assert.Equal(0, process.ExitCode);
            Assert.Empty(await stderr);
            var evidence = new
            {
                provenance, provider = "Microsoft-Diagnostics-DiagnosticSource", keywords = 3, arguments,
                rawSession = "Separate session with identical provider arguments", rawCompletion,
                witness, raw, capture, mcpList = list, mcpTraces = traces, targetExited = process.HasExited, process.ExitCode,
            };
            output.WriteLine(JsonSerializer.Serialize(evidence, JsonOptions));
            if (Environment.GetEnvironmentVariable("HTTP_ACTIVITY_EVIDENCE") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(Path.Combine(directory, $"{framework}-{(bridge ? "bridge" : enrich ? "enrich" : "plain")}.json"),
                    JsonSerializer.Serialize(evidence, JsonOptions), token);
            }
        }
        finally
        {
            await budget.CancelAsync();
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            var pending = new List<Task>(2);
            if (collecting is not null) pending.Add(collecting);
            if (reading is not null) pending.Add(reading);
            try { await Task.WhenAll(pending); }
            catch (OperationCanceledException) when (budget.IsCancellationRequested) { }
            output.WriteLine($"Owned target {process.Id} exited={process.HasExited}; stderr={await stderr}");
        }

        async Task<string> ReadLine() =>
            await process.StandardOutput.ReadLineAsync(token) ?? throw new IOException("Target stdout ended before protocol completion.");
    }

    private sealed class PrincipalAccessor : IPrincipalAccessor
    {
        public BearerPrincipal Current { get; } = new("http-activity-test", ImmutableHashSet.Create("eventpipe"));
    }
}
