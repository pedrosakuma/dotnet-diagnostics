using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Signals;
using DotnetDiagnostics.Core.Threads;
using DotnetDiagnostics.TestSupport;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class ScenarioLiveRunner
{
    private const int MaxNotes = 20;
    internal const CpuSamplingMode CultureLookupSamplingMode = CpuSamplingMode.Os;

    public static async Task<ScenarioEvidence> CaptureAsync(
        ScenarioManifest manifest,
        int trial,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!SupportsCurrentPlatform(manifest))
        {
            throw new PlatformNotSupportedException(
                $"Scenario '{manifest.Id}' does not support live capture on {CurrentPlatformName()}.");
        }

        using var runtimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        runtimeCts.CancelAfter(TimeSpan.FromSeconds(manifest.Budget.MaximumRuntimeSeconds));
        try
        {
            return manifest.Id switch
            {
                "culture-lookup" => await CaptureCultureLookupAsync(manifest, trial, runtimeCts.Token).ConfigureAwait(false),
                "sync-over-async" => await CaptureSyncOverAsyncAsync(manifest, trial, runtimeCts.Token).ConfigureAwait(false),
                "healthy-sync-over-async" => await CaptureSyncOverAsyncAsync(manifest, trial, runtimeCts.Token).ConfigureAwait(false),
                "lock-storm" => await CaptureLockStormAsync(manifest, trial, runtimeCts.Token).ConfigureAwait(false),
                "gc-storm" => await CaptureGcStormAsync(manifest, trial, runtimeCts.Token).ConfigureAwait(false),
                _ => throw new InvalidDataException($"No live driver is registered for scenario '{manifest.Id}'."),
            };
        }
        catch (ScenarioRunException)
        {
            throw;
        }
        catch (PlatformNotSupportedException exception)
        {
            throw new ScenarioRunException(exception.Message, ScenarioFailureKind.Environment, exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new ScenarioRunException(exception.Message, ScenarioFailureKind.Environment, exception);
        }
        catch (HttpRequestException exception)
        {
            throw new ScenarioRunException(exception.Message, ScenarioFailureKind.Workload, exception);
        }
        catch (OperationCanceledException exception) when (
            runtimeCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new ScenarioRunException(
                $"Scenario '{manifest.Id}' exceeded its {manifest.Budget.MaximumRuntimeSeconds}-second runtime budget.",
                ScenarioFailureKind.Environment,
                exception);
        }
    }

    public static bool SupportsCurrentPlatform(ScenarioManifest manifest)
    {
        var platform = OperatingSystem.IsWindows()
            ? ScenarioPlatform.Windows
            : OperatingSystem.IsLinux()
                ? ScenarioPlatform.Linux
                : (ScenarioPlatform?)null;
        return platform is not null && manifest.SupportedLivePlatforms.Contains(platform.Value);
    }

    private static async Task<ScenarioEvidence> CaptureCultureLookupAsync(
        ScenarioManifest manifest,
        int trial,
        CancellationToken cancellationToken)
    {
        var culture = await CaptureCulturePhaseAsync(manifest, trial, ordinal: false, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var ordinal = await CaptureCulturePhaseAsync(manifest, trial, ordinal: true, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return CultureLookupCpuContract.Combine(culture, ordinal, manifest.Budget.MaximumEvidenceItems);
    }

    private static async Task<ScenarioEvidence> CaptureCulturePhaseAsync(
        ScenarioManifest manifest, int trial, bool ordinal, CancellationToken cancellationToken)
    {
        await using var sample = await StartSampleAsync(manifest).ConfigureAwait(false);
        using var http = CreateHttpClient(sample.BaseUrl);
        using var loadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var load = new LoadCounters();
        var activationWatch = Stopwatch.StartNew();
        var iterations = RequiredPositiveIntParameter(manifest, "iterations");
        var comparer = ordinal ? "OrdinalIgnoreCase" : "InvariantCultureIgnoreCase";
        var driver = DriveCultureRequestsAsync(
            http,
            $"{RequiredParameter(manifest, ordinal ? "controlEndpoint" : "endpoint")}?iterations={iterations}",
            iterations,
            comparer,
            Math.Clamp(
                Environment.ProcessorCount,
                RequiredPositiveIntParameter(manifest, "minimumWorkers"),
                RequiredPositiveIntParameter(manifest, "maximumWorkers")),
            TimeSpan.FromMilliseconds(manifest.Workload.WarmupMilliseconds),
            load,
            loadCts.Token);

        CpuSampleResult result;
        try
        {
            var perf = new PerfNativeAotCpuSampler();
            var etw = new EtwNativeAotCpuSampler();
            var sampler = new RoutingCpuSampler(
                new CapabilityDetector(perfSampler: perf, etwSampler: etw),
                new EventPipeCpuSampler(), perf, etw);
            result = await sampler.SampleAsync(
                sample.ProcessId,
                TimeSpan.FromSeconds(manifest.Workload.ObservationSeconds),
                topN: 25,
                sourceResolution: null,
                methodInstantiationResolution: null,
                nativeAotSymbols: null,
                exportTrace: false,
                mode: CultureLookupSamplingMode,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is Microsoft.Diagnostics.NETCore.Client.DiagnosticsClientException
            or InvalidOperationException
            or UnauthorizedAccessException)
        {
            throw new ScenarioRunException(
                $"OS CPU collection failed for '{manifest.Id}': {exception.Message}",
                ScenarioFailureClassifier.Classify(exception, ScenarioFailureKind.Collection),
                exception);
        }
        finally
        {
            await loadCts.CancelAsync().ConfigureAwait(false);
            await DrainDriverAsync(driver).ConfigureAwait(false);
        }

        activationWatch.Stop();
        EnsureActivated(manifest.Id, load);
        if (load.Successes == 0 || load.Failures != 0)
        {
            throw new ScenarioRunException(
                $"Culture lookup responses were not verified (comparer={comparer}, verified={load.Successes}, failures={load.Failures}).",
                ScenarioFailureKind.Workload);
        }
        var signals = NormalizeSignals(
            manifest,
            CpuSampleSignals.Detect(result.Artifact, "replay"),
            manifest.Budget.MaximumEvidenceItems);

        return CompleteCultureCpuEvidence(Evidence(
            manifest,
            trial,
            activationWatch.Elapsed,
            result.Summary.Duration,
            load,
            metrics:
            [
                new ObservedMetric("total-samples", result.Summary.TotalSamples, "samples"),
            ],
            signals,
            frames: [],
            relations: [],
            notes: []), result, manifest.Budget.MaximumEvidenceItems,
            ordinal ? CultureLookupCpuContract.OrdinalMethod : CultureLookupCpuContract.CultureMethod,
            ordinal ? CultureLookupCpuContract.CultureMethod : CultureLookupCpuContract.OrdinalMethod,
            load.Successes);
    }

    internal static ScenarioEvidence CompleteCultureCpuEvidence(
        ScenarioEvidence evidence, CpuSampleResult result, int maximumEvidenceItems,
        string? activeMethod = null, string? inactiveMethod = null, int verifiedResponses = 0)
    {
        var retainedLimit = Math.Clamp(maximumEvidenceItems, 1, 30);
        var methods = CpuSampleQueryDispatcher.RenderTopMethods(
            result.Artifact, "replay", "running", retainedLimit).Data!;
        var modules = CpuSampleQueryDispatcher.RenderByModule(result.Artifact, "replay", 5).Data!;
        var totalRunning = result.Artifact.SelfSamples?.RunningSamples ?? result.Artifact.TotalSamples;
        var topRunning = methods.Methods.Count > 0
            ? methods.Methods[0].SelfSamples?.RunningSamples ?? methods.Methods[0].ExclusiveSamples
            : 0;
        var topShare = totalRunning > 0 ? topRunning * 100d / totalRunning : 0;
        var diagnostics = evidence with
        {
            Metrics = evidence.Metrics.Concat(
            [
                new ObservedMetric("cpu-running-self-samples", totalRunning, "samples"),
                new ObservedMetric("cpu-top1-running-self-share", topShare, "%"),
                new ObservedMetric("cpu-concentration-min-top1-share", CpuSelfTimeConcentrationProvider.MinTop1Share * 100, "%"),
                new ObservedMetric("cpu-retained-exclusive-samples", methods.Methods.Sum(method => method.ExclusiveSamples), "samples"),
            ]).OrderBy(metric => metric.Name, StringComparer.Ordinal).ToArray(),
            Frames = methods.Methods.Where(method => method.ExclusiveSamples > 0)
                .Select(method => new ObservedFrame(
                    $"{method.Module}!{method.Method}", checked((int)method.ExclusiveSamples)))
                .ToArray(),
            Notes = new[]
                {
                    $"CPU backend={result.Artifact.Evidence?.Backend}; evidence={result.Artifact.Evidence?.Kind}; artifactSymbolSource={result.Artifact.SymbolSource}; summarySymbolSource={result.Summary.SymbolSource?.ToString() ?? "missing"}.",
                    $"CPU frames retain at most {retainedLimit} exclusive candidates even when no concentration signal is emitted; matchCount is exclusive sample count, not inclusive attribution or thread count.",
                    "Unresolved addresses remain separate candidates. Resolved ancestors or module totals do not establish a resolved hashing leaf.",
                }
                .Concat(modules.Groups.Select(module => FormattableString.Invariant(
                    $"Exclusive module: {module.Group}; samples={module.ExclusiveSamples}; share={module.ExclusivePercent:0.##}%.")))
                .Concat(result.Summary.Notes)
                .Take(MaxNotes)
                .ToArray(),
        };
        try
        {
            ValidateCultureCpuEvidence(result);
            if (activeMethod is not null)
            {
                diagnostics = diagnostics with
                {
                    Metrics = diagnostics.Metrics.Concat(CultureLookupCpuContract.ProjectOwnership(
                        result.Artifact, activeMethod, inactiveMethod!, verifiedResponses))
                        .OrderBy(metric => metric.Name, StringComparer.Ordinal).ToArray(),
                    Notes = new[]
                        {
                            $"Acceptance measures distinct-stack inclusive ownership of {activeMethod}; the other route {inactiveMethod} must be absent. This is not exclusive managed/native leaf cost.",
                        }.Concat(diagnostics.Notes).Take(MaxNotes).ToArray(),
                };
            }
        }
        catch (InvalidOperationException exception)
        {
            return diagnostics with
            {
                Collection = diagnostics.Collection with
                {
                    Status = ScenarioStageStatus.Failed,
                    FailureKind = ScenarioFailureKind.Collection,
                    Detail = exception.Message,
                },
                Signals = [],
            };
        }
        return diagnostics;
    }

    private static async Task DriveCultureRequestsAsync(
        HttpClient http, string path, int iterations, string comparer, int workers,
        TimeSpan delay, LoadCounters counters, CancellationToken cancellationToken)
    {
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(Enumerable.Range(0, workers).Select(async _ =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref counters.Attempts);
                try
                {
                    using var response = await http.GetAsync(path, cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    var result = await response.Content.ReadFromJsonAsync<CultureLookupResponse>(
                        cancellationToken).ConfigureAwait(false);
                    ValidateCultureResponse(result, iterations, comparer);
                    Interlocked.Increment(ref counters.Successes);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException)
                {
                    Interlocked.Increment(ref counters.Failures);
                }
            }
        })).ConfigureAwait(false);
    }

    internal static void ValidateCultureResponse(CultureLookupResponse? result, int iterations, string comparer)
    {
        if (result is null || result.Loops != iterations || result.Hits != iterations
            || !string.Equals(result.Comparer, comparer, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The lookup route did not return the expected iterations, hits and comparer.");
        }
    }

    internal sealed record CultureLookupResponse(int Loops, long Hits, string Comparer);

    internal static void ValidateCultureCpuEvidence(CpuSampleResult result)
    {
        if (result.Artifact.Evidence?.Kind != CpuSampleEvidenceKind.OsOnCpuSamples)
        {
            throw new InvalidOperationException("The culture-lookup scenario requires measured OS on-CPU evidence; no EventPipe fallback is accepted.");
        }

        if (result.Summary.TotalSamples == 0)
        {
            throw new InvalidOperationException("The OS CPU backend collected no samples for the target process.");
        }

        if (result.Artifact.SymbolSource is NativeAotSymbolDemangler.SymbolSource.Stripped
            or NativeAotSymbolDemangler.SymbolSource.Unknown
            || result.Summary.SymbolSource == NativeAotSymbolDemangler.SymbolSource.Stripped)
        {
            throw new InvalidOperationException(
                $"OS CPU collection acquired {result.Summary.TotalSamples} samples, but no usable symbols for attribution. {string.Join(" ", result.Summary.Notes ?? [])}");
        }
    }

    private static async Task<ScenarioEvidence> CaptureSyncOverAsyncAsync(
        ScenarioManifest manifest,
        int trial,
        CancellationToken cancellationToken)
    {
        await using var sample = await StartSampleAsync(manifest).ConfigureAwait(false);
        using var http = CreateHttpClient(sample.BaseUrl);
        using var loadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var snapshotCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var load = new LoadCounters();
        var activationWatch = Stopwatch.StartNew();
        var driver = DriveRepeatedRequestsAsync(
            http,
            $"{RequiredParameter(manifest, "endpoint")}?n={RequiredPositiveIntParameter(manifest, "n")}&delaySeconds={RequiredPositiveIntParameter(manifest, "delaySeconds")}",
            RequiredPositiveIntParameter(manifest, "concurrentRequests"),
            TimeSpan.FromMilliseconds(manifest.Workload.WarmupMilliseconds),
            load,
            loadCts.Token);

        var snapshotTask = CaptureThreadSnapshotAfterDelayAsync(
            sample.ProcessId,
            TimeSpan.FromMilliseconds(manifest.Workload.WarmupMilliseconds + 1800),
            snapshotCts.Token);
        var snapshotObserved = false;

        CounterSnapshot counters;
        ThreadSnapshotArtifact snapshot;
        try
        {
            counters = await new EventPipeCounterCollector().CollectAsync(
                sample.ProcessId,
                TimeSpan.FromSeconds(manifest.Workload.ObservationSeconds),
                intervalSeconds: 1,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            snapshotObserved = true;
            snapshot = await snapshotTask.ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is Microsoft.Diagnostics.NETCore.Client.DiagnosticsClientException
            or InvalidOperationException
            or UnauthorizedAccessException)
        {
            throw new ScenarioRunException(
                $"Counter/thread collection failed for '{manifest.Id}'.",
                ScenarioFailureClassifier.Classify(exception, ScenarioFailureKind.Collection),
                exception);
        }
        finally
        {
            await snapshotCts.CancelAsync().ConfigureAwait(false);
            await loadCts.CancelAsync().ConfigureAwait(false);
            await DrainDriverAsync(driver).ConfigureAwait(false);
            if (!snapshotObserved)
            {
                await DrainSnapshotAsync(snapshotTask).ConfigureAwait(false);
            }
        }

        activationWatch.Stop();
        EnsureActivated(manifest.Id, load);
        var metrics = SelectCounters(counters, "cpu-usage", "threadpool-queue-length", "threadpool-thread-count");
        var signals = NormalizeSignals(
            manifest,
            ThreadWaitSignals.Detect(snapshot, "replay"),
            manifest.Budget.MaximumEvidenceItems);
        var frames = NormalizeThreadFrames(snapshot, manifest);
        var notes = counters.Notes
            .Concat(snapshot.Warnings ?? [])
            .OrderBy(note => note, StringComparer.Ordinal)
            .Take(MaxNotes)
            .ToArray();

        return Evidence(
            manifest,
            trial,
            activationWatch.Elapsed,
            counters.Duration + snapshot.WalkDuration,
            load,
            metrics,
            signals,
            frames,
            NormalizeRelations(snapshot, manifest.Budget.MaximumEvidenceItems),
            notes);
    }

    private static async Task<ScenarioEvidence> CaptureLockStormAsync(
        ScenarioManifest manifest,
        int trial,
        CancellationToken cancellationToken)
    {
        await using var sample = await StartSampleAsync(manifest).ConfigureAwait(false);
        using var http = CreateHttpClient(sample.BaseUrl);
        using var loadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var load = new LoadCounters();
        var activationWatch = Stopwatch.StartNew();
        var driver = DriveRepeatedRequestsAsync(
            http,
            $"{RequiredParameter(manifest, "endpoint")}?seconds={RequiredPositiveIntParameter(manifest, "seconds")}&blockers={RequiredPositiveIntParameter(manifest, "blockers")}",
            workers: 1,
            delay: TimeSpan.Zero,
            load,
            loadCts.Token);

        ThreadSnapshotArtifact snapshot;
        try
        {
            snapshot = await CaptureThreadSnapshotAfterDelayAsync(
                sample.ProcessId,
                TimeSpan.FromMilliseconds(manifest.Workload.WarmupMilliseconds),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
            or UnauthorizedAccessException)
        {
            throw new ScenarioRunException(
                $"Thread collection failed for '{manifest.Id}'.",
                ScenarioFailureClassifier.Classify(exception, ScenarioFailureKind.Collection),
                exception);
        }
        finally
        {
            await loadCts.CancelAsync().ConfigureAwait(false);
            await DrainDriverAsync(driver).ConfigureAwait(false);
        }

        activationWatch.Stop();
        EnsureActivated(manifest.Id, load);
        return Evidence(
            manifest,
            trial,
            activationWatch.Elapsed,
            snapshot.WalkDuration,
            load,
            metrics:
            [
                new ObservedMetric("managed-thread-count", snapshot.Threads.Count, "threads"),
                new ObservedMetric("monitor-lock-count", snapshot.Locks.Count, "locks"),
            ],
            NormalizeSignals(manifest, ThreadWaitSignals.Detect(snapshot, "replay"), manifest.Budget.MaximumEvidenceItems),
            frames: [],
            NormalizeRelations(snapshot, manifest.Budget.MaximumEvidenceItems),
            (snapshot.Warnings ?? []).OrderBy(note => note, StringComparer.Ordinal).Take(MaxNotes).ToArray());
    }

    private static async Task<ScenarioEvidence> CaptureGcStormAsync(
        ScenarioManifest manifest,
        int trial,
        CancellationToken cancellationToken)
    {
        await using var sample = await StartSampleAsync(manifest).ConfigureAwait(false);
        using var http = CreateHttpClient(sample.BaseUrl);
        using var loadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var stopIssuingCts = new CancellationTokenSource();
        var load = new LoadCounters();
        var activationStartedAt = DateTimeOffset.UtcNow;
        var activationWatch = Stopwatch.StartNew();
        var driver = DriveRepeatedRequestsAsync(
            http,
            $"{RequiredParameter(manifest, "endpoint")}?count={RequiredPositiveIntParameter(manifest, "count")}",
            RequiredPositiveIntParameter(manifest, "concurrentRequests"),
            TimeSpan.FromMilliseconds(manifest.Workload.WarmupMilliseconds),
            load,
            loadCts.Token,
            stopIssuingCts.Token);

        CounterSnapshot counters;
        GcSummary gc;
        DateTimeOffset quiescedAt;
        try
        {
            var duration = TimeSpan.FromSeconds(manifest.Workload.ObservationSeconds);
            var countersTask = new EventPipeCounterCollector().CollectAsync(
                sample.ProcessId,
                duration,
                intervalSeconds: 1,
                cancellationToken: cancellationToken);
            var gcTask = new EventPipeGcCollector().CollectAsync(
                sample.ProcessId,
                duration + TimeSpan.FromSeconds(RequiredPositiveIntParameter(manifest, "drainSeconds")),
                // gc-storm drives a sustained LOH-allocation workload that produces far more
                // than the collector's 200-sample default across an 8s window; a higher cap
                // keeps GCHeapStats representative of the full observation window instead of
                // truncating to only the earliest samples.
                maxEvents: 4000,
                cancellationToken: cancellationToken);
            var quiescenceTask = QuiesceGcWorkloadAsync(countersTask, stopIssuingCts, driver);
            await Task.WhenAll(countersTask, gcTask, quiescenceTask).ConfigureAwait(false);
            counters = countersTask.Result;
            gc = gcTask.Result;
            quiescedAt = quiescenceTask.Result;
        }
        catch (Exception exception) when (
            exception is Microsoft.Diagnostics.NETCore.Client.DiagnosticsClientException
            or InvalidOperationException
            or UnauthorizedAccessException)
        {
            throw new ScenarioRunException(
                $"Counter/GC collection failed for '{manifest.Id}'.",
                ScenarioFailureClassifier.Classify(exception, ScenarioFailureKind.Collection),
                exception);
        }
        finally
        {
            await loadCts.CancelAsync().ConfigureAwait(false);
            await DrainDriverAsync(driver).ConfigureAwait(false);
        }

        activationWatch.Stop();
        EnsureActivated(manifest.Id, load);
        var metrics = SelectCounters(counters, "gen-2-gc-count", "loh-size", "time-in-gc", "alloc-rate")
            // `loh-size` above is the last-observed EventCounter tick: by the time the window
            // closes, gen2/LOH churn can already have been collected back down to (near) zero, so
            // it is not evidence that LOH pressure never happened (#858). `loh-size-max` is the
            // maximum observed across every tick of the window and is what actually reflects
            // transient LOH churn.
            .Concat(SelectMaxCounters(counters, "loh-size"))
            .Append(new ObservedMetric("gc-total-collections", gc.TotalCollections, "collections"))
            .Concat(gc.Generations.Where(generation => generation.Generation is >= 0 and <= 2)
                .Select(generation => new ObservedMetric(
                    $"gc-gen{generation.Generation}-completed", generation.Count, "collections")))
            .Append(new ObservedMetric("gc-capture-start-offset", (gc.StartedAt - activationStartedAt).TotalSeconds, "seconds"))
            .Append(new ObservedMetric("gc-workload-quiesced-offset", (quiescedAt - activationStartedAt).TotalSeconds, "seconds"))
            .Append(new ObservedMetric("gc-quiescent-tail-seconds", (gc.StartedAt + gc.Duration - quiescedAt).TotalSeconds, "seconds"))
            .OrderBy(metric => metric.Name, StringComparer.Ordinal)
            .ToArray();
        var signals = NormalizeSignals(
            manifest,
            GcSignals.Detect(gc, "replay"),
            manifest.Budget.MaximumEvidenceItems);
        var notes = DescribeGcQuality(gc)
            .Concat(counters.Notes.OrderBy(note => note, StringComparer.Ordinal))
            .Concat(CollectGcNotes(gc))
            .Take(MaxNotes)
            .ToArray();

        return Evidence(
            manifest,
            trial,
            activationWatch.Elapsed,
            counters.Duration + gc.Duration,
            load,
            metrics,
            signals,
            frames: [],
            relations: [],
            notes);
    }

    private static async Task<LiveSampleProcess> StartSampleAsync(ScenarioManifest manifest)
    {
        var startupStageTimeout = TimeSpan.FromSeconds(
            Math.Max(1, manifest.Budget.MaximumRuntimeSeconds / 4.0));
        return await LiveSampleProcess.StartPublishedAsync(
            "BadCodeSample",
            new LiveSampleOptions
            {
                HarvestListeningUrl = true,
                WaitForHttpReady = true,
                ReadinessPath = "/",
                DiagnosticTimeout = startupStageTimeout,
                HttpTimeout = startupStageTimeout,
                Environment = new Dictionary<string, string>
                {
                    ["DOTNET_gcServer"] = "0",
                    ["DOTNET_TieredCompilation"] = "1",
                    ["DOTNET_TC_QuickJit"] = "1",
                    ["DOTNET_TieredPGO"] = "1",
                },
            }).ConfigureAwait(false);
    }

    private static HttpClient CreateHttpClient(string baseUrl)
        => new()
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(30),
        };

    private static async Task DriveRepeatedRequestsAsync(
        HttpClient http,
        string path,
        int workers,
        TimeSpan delay,
        LoadCounters counters,
        CancellationToken cancellationToken,
        CancellationToken stopIssuingCancellationToken = default)
    {
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        var tasks = Enumerable.Range(0, workers).Select(async _ =>
        {
            while (!cancellationToken.IsCancellationRequested && !stopIssuingCancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref counters.Attempts);
                try
                {
                    using var response = await http.GetAsync(
                        path,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    Interlocked.Increment(ref counters.Successes);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (HttpRequestException)
                {
                    Interlocked.Increment(ref counters.Failures);
                }
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    internal static async Task<DateTimeOffset> QuiesceGcWorkloadAsync(
        Task countersTask,
        CancellationTokenSource stopIssuing,
        Task driver)
    {
        await countersTask.ConfigureAwait(false);
        await stopIssuing.CancelAsync().ConfigureAwait(false);
        // Do not cancel HTTP requests: their synchronous allocation handlers must
        // finish before the remaining GC observation tail closes.
        await driver.ConfigureAwait(false);
        return DateTimeOffset.UtcNow;
    }

    private static async Task DrainDriverAsync(Task driver)
    {
        try
        {
            await driver.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task DrainSnapshotAsync(Task<ThreadSnapshotArtifact> snapshot)
    {
        try
        {
            await snapshot.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task<ThreadSnapshotArtifact> CaptureThreadSnapshotAfterDelayAsync(
        int processId,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        return await new ClrMdThreadSnapshotInspector().InspectLiveAsync(
            processId,
            new ThreadSnapshotOptions(MaxFramesPerThread: 64),
            cancellationToken).ConfigureAwait(false);
    }

    private static ScenarioEvidence Evidence(
        ScenarioManifest manifest,
        int trial,
        TimeSpan activationDuration,
        TimeSpan collectionDuration,
        LoadCounters load,
        IReadOnlyList<ObservedMetric> metrics,
        IReadOnlyList<ObservedSignal> signals,
        IReadOnlyList<ObservedFrame> frames,
        IReadOnlyList<ObservedRelation> relations,
        IReadOnlyList<string> notes)
        => new(
            SchemaVersion: ScenarioJson.CurrentEvidenceSchemaVersion,
            ScenarioId: manifest.Id,
            ScenarioVersion: manifest.Version,
            Trial: trial,
            Environment: CurrentEnvironment(),
            Activation: new ScenarioStageResult(
                ScenarioStageStatus.Passed,
                ScenarioFailureKind.None,
                $"attempts={load.Attempts}; successes={load.Successes}; requestFailures={load.Failures}",
                RoundSeconds(activationDuration)),
            Collection: new ScenarioStageResult(
                ScenarioStageStatus.Passed,
                ScenarioFailureKind.None,
                null,
                RoundSeconds(collectionDuration)),
            Metrics: metrics.OrderBy(metric => metric.Name, StringComparer.Ordinal).ToArray(),
            Signals: signals,
            Frames: frames,
            Relations: relations,
            Notes: notes);

    private static ObservedMetric[] SelectCounters(CounterSnapshot snapshot, params string[] names)
        => names.Select(name =>
            {
                var counter = snapshot.Counters.FirstOrDefault(value =>
                    string.Equals(value.Provider, "System.Runtime", StringComparison.Ordinal)
                    && string.Equals(value.Name, name, StringComparison.Ordinal));
                return counter is null ? null : new ObservedMetric(name, counter.Value, counter.Unit);
            })
            .Where(metric => metric is not null)
            .Cast<ObservedMetric>()
            .OrderBy(metric => metric.Name, StringComparer.Ordinal)
            .ToArray();

    // Emits a "<name>-max" evidence metric sourced from CounterSnapshot.MaxCounters — the maximum
    // value observed for that counter across every tick of the collection window, not just the
    // last tick. This is what lets a scenario assert transient churn (e.g. LOH growth that is
    // already collected back down by the final sample) instead of only retained final state (#858).
    private static ObservedMetric[] SelectMaxCounters(CounterSnapshot snapshot, params string[] names)
        => names.Select(name =>
            {
                var counter = snapshot.MaxCounters?.FirstOrDefault(value =>
                    string.Equals(value.Provider, "System.Runtime", StringComparison.Ordinal)
                    && string.Equals(value.Name, name, StringComparison.Ordinal));
                return counter is null ? null : new ObservedMetric($"{name}-max", counter.Value, counter.Unit);
            })
            .Where(metric => metric is not null)
            .Cast<ObservedMetric>()
            .OrderBy(metric => metric.Name, StringComparer.Ordinal)
            .ToArray();

    private static ObservedSignal[] NormalizeSignals(
        ScenarioManifest manifest,
        IReadOnlyList<SignalGroup> signals,
        int maximumEvidenceItems)
    {
        var selectedSignals = manifest.ExpectedEvidence
            .Select(invariant => invariant.Signal)
            .Where(signal => signal is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        return signals
            .Where(signal => selectedSignals.Contains(signal.Signal))
            .OrderBy(signal => signal.Signal, StringComparer.Ordinal)
            .Take(maximumEvidenceItems)
            .Select(signal => new ObservedSignal(
                signal.Signal,
                Math.Round(signal.Salience, 6),
                signal.Buckets
                    .OrderByDescending(bucket => bucket.Magnitude)
                    .ThenBy(bucket => bucket.Key, StringComparer.Ordinal)
                    .Take(Math.Min(5, maximumEvidenceItems))
                    .Select(bucket => new ObservedSignalBucket(
                        NormalizeBucketKey(bucket.Key),
                        Math.Round(bucket.Magnitude, 6),
                        bucket.Unit))
                    .ToArray(),
                signal.NextAction?.NextTool))
            .ToArray();
    }

    private static ObservedFrame[] NormalizeThreadFrames(
        ThreadSnapshotArtifact snapshot,
        ScenarioManifest manifest)
    {
        var terms = manifest.ExpectedEvidence
            .Where(invariant => invariant.Kind == EvidenceInvariantKind.StackFrameMatch)
            .SelectMany(invariant => invariant.ContainsAny ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return snapshot.Threads
            .SelectMany(thread => thread.Frames)
            .Where(frame => terms.Any(term => frame.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(frame => frame.DisplayName, StringComparer.Ordinal)
            .Select(group => new ObservedFrame(group.Key, group.Count()))
            .OrderByDescending(frame => frame.MatchCount)
            .ThenBy(frame => frame.DisplayName, StringComparer.Ordinal)
            .Take(manifest.Budget.MaximumEvidenceItems)
            .ToArray();
    }

    private static ObservedRelation[] NormalizeRelations(
        ThreadSnapshotArtifact snapshot,
        int maximumEvidenceItems)
    {
        var threads = snapshot.Threads.ToDictionary(thread => thread.ManagedThreadId);
        return snapshot.Locks
            .Where(lockState => lockState.WaitingThreadCount > 0)
            .Select(lockState =>
            {
                threads.TryGetValue(lockState.OwnerManagedThreadId, out var owner);
                return owner?.InferredWaitReason is null
                    ? null
                    : new ObservedRelation(
                        "thread-owner-overlap",
                        owner.InferredWaitReason,
                        lockState.WaitingThreadCount);
            })
            .Where(relation => relation is not null)
            .Cast<ObservedRelation>()
            .OrderByDescending(relation => relation.WaitingThreadCount)
            .ThenBy(relation => relation.OwnerWaitReason, StringComparer.Ordinal)
            .Take(Math.Min(10, maximumEvidenceItems))
            .ToArray();
    }

    private static string NormalizeBucketKey(string key)
    {
        if (!key.StartsWith("thread ", StringComparison.Ordinal))
        {
            return key;
        }

        var owns = key.IndexOf(" owns ", StringComparison.Ordinal);
        if (owns < 0)
        {
            return key;
        }

        var value = $"thread{key[owns..]}";
        var address = value.IndexOf(" @ 0x", StringComparison.Ordinal);
        return address < 0 ? value : value[..address];
    }

    private static string RequiredParameter(ScenarioManifest manifest, string name)
    {
        if (!manifest.Workload.Parameters.TryGetValue(name, out var value)
            || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException(
                $"Scenario '{manifest.Id}' requires workload parameter '{name}'.");
        }

        return value;
    }

    private static int RequiredPositiveIntParameter(ScenarioManifest manifest, string name)
    {
        var value = RequiredParameter(manifest, name);
        if (!int.TryParse(value, out var parsed) || parsed < 1)
        {
            throw new InvalidDataException(
                $"Scenario '{manifest.Id}' workload parameter '{name}' must be a positive integer.");
        }

        return parsed;
    }

    private static void EnsureActivated(string scenarioId, LoadCounters load)
    {
        if (load.Attempts == 0 || (load.Failures > 0 && load.Successes == 0))
        {
            throw new ScenarioRunException(
                $"Scenario '{scenarioId}' did not activate successfully (attempts={load.Attempts}, successes={load.Successes}, failures={load.Failures}).",
                ScenarioFailureKind.Workload);
        }
    }

    private static ScenarioEnvironment CurrentEnvironment()
        => new(
            CurrentPlatformName(),
            RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            RuntimeInformation.FrameworkDescription,
            Environment.Version.ToString());

    private static string CurrentPlatformName()
        => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "unsupported";

    private static double RoundSeconds(TimeSpan duration) => Math.Round(duration.TotalSeconds, 6);

    private static IEnumerable<string> CollectGcNotes(GcSummary gc)
    {
        if (gc.DroppedEvents > 0)
        {
            yield return $"Dropped {gc.DroppedEvents} GC event(s) due to maxEvents cap.";
        }

        if (gc.DroppedHeapStats > 0)
        {
            yield return $"Dropped {gc.DroppedHeapStats} GCHeapStats sample(s) due to maxEvents cap.";
        }
    }

    internal static IReadOnlyList<string> DescribeGcQuality(GcSummary gc)
    {
        var quality = gc.Suspension;
        var notes = new List<string>
        {
            "gc.provenance=EventPipe Microsoft-Windows-DotNETRuntime GCStart/GCStop completed pairs; collection elapsed is not suspension.",
            $"gc.completion={quality?.Completion ?? "unavailable"}; suspensionStatus={gc.PauseMeasurementStatus}.",
            FormattableString.Invariant($"gc.window: requestedSeconds={gc.RequestedDuration?.TotalSeconds}; observedSeconds={gc.Duration.TotalSeconds}."),
        };
        if (quality is not null)
        {
            notes.AddRange(quality.Limitations.OrderBy(pair => pair.Key, StringComparer.Ordinal).Take(10)
                .Select(pair => $"gc.limitation:{pair.Key}={pair.Value}"));
            if (quality.Limitations.Count > 10)
                notes.Add($"gc.limitations-omitted={quality.Limitations.Count - 10}");
        }
        return notes;
    }

    private sealed class LoadCounters
    {
        public int Attempts;
        public int Successes;
        public int Failures;
    }
}
