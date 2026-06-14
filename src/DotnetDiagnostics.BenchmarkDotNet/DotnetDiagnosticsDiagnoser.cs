using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using BenchmarkDotNet.Analysers;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Validators;

namespace DotnetDiagnostics.BenchmarkDotNet;

/// <summary>
/// A BenchmarkDotNet <see cref="IDiagnoser"/> that runs the dotnet-diagnostics engine
/// <b>in-process</b> against a benchmark's child process while it runs and captures EventPipe perf
/// indicators (GC, contention, thread pool, exceptions, JIT, counters, …) as JSON artifacts plus a
/// per-run "biggest offenders" report.
///
/// <para>
/// This diagnoses the benchmark rather than measuring it: EventPipe collectors are observe-only
/// (no ptrace / no code injection) but still add modest overhead, so use it on a dedicated
/// diagnostic job (e.g. a <c>RunStrategy.Monitoring</c> job) and treat its timing numbers as
/// non-publication-grade. The native <c>MemoryDiagnoser</c>/<c>ThreadingDiagnoser</c> remain the
/// right tools for clean measurement; this diagnoser adds the drill-down the MCP/CLI provides.
/// </para>
/// </summary>
public sealed class DotnetDiagnosticsDiagnoser : IDiagnoser, IDisposable
{
    private readonly InProcessDiagnosticCollector _collector = new();
    private readonly ConcurrentDictionary<string, RunningCapture> _inFlight = new();
    private readonly ConcurrentBag<BenchmarkDiagnosticEntry> _entries = new();
    private readonly List<string> _resultLines = new();
    private string? _artifactsDir;
    private int _captureSequence;

    /// <summary>The diagnostic entries captured across the whole run, consumed by the report exporter.</summary>
    public IReadOnlyCollection<BenchmarkDiagnosticEntry> Entries => _entries;

    public IEnumerable<string> Ids => new[] { "dotnet-diagnostics" };

    public IEnumerable<IExporter> Exporters => new IExporter[] { new DotnetDiagnosticsReportExporter(this) };

    public IEnumerable<IAnalyser> Analysers => Array.Empty<IAnalyser>();

    public RunMode GetRunMode(BenchmarkCase benchmarkCase)
        => GetDiagnosticKind(benchmarkCase) is null ? RunMode.None : RunMode.NoOverhead;

    public void Handle(HostSignal signal, DiagnoserActionParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var attribute = GetDiagnosticKind(parameters.BenchmarkCase);
        if (attribute is null)
        {
            return;
        }

        switch (signal)
        {
            case HostSignal.BeforeActualRun:
                StartCapture(parameters, attribute);
                break;
            case HostSignal.AfterActualRun:
                FinishCapture(parameters);
                break;
            default:
                break;
        }
    }

    private void StartCapture(DiagnoserActionParameters parameters, DiagnosticKindAttribute attribute)
    {
        var process = parameters.Process;
        if (process is null || process.HasExited)
        {
            return;
        }

        var pid = process.Id;
        _artifactsDir ??= Path.Combine(parameters.Config.ArtifactsPath, "diagnostics");
        Directory.CreateDirectory(_artifactsDir);

        var key = BenchmarkKey(parameters);
        var capture = new RunningCapture(attribute, Interlocked.Increment(ref _captureSequence));
        var token = capture.Cancellation.Token;

        // Run each kind sequentially (EventPipe collectors must not overlap on one PID) on a
        // background task so the benchmark's actual run proceeds unblocked.
        capture.Task = Task.Run(async () =>
        {
            foreach (var kind in attribute.KindList)
            {
                if (process.HasExited || token.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    capture.Captures[kind] = await _collector
                        .CollectAsync(pid, kind, attribute.DurationSeconds, token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
#pragma warning disable CA1031 // capture-and-report: one wedged collector must not abort the others
                catch (Exception ex)
                {
                    capture.Captures[kind] = new KindCapture(
                        kind, IsError: true, Summary: $"collect '{kind}' failed", Headline: ex.GetBaseException().Message, Json: $"// {ex.GetBaseException().Message}");
                }
#pragma warning restore CA1031
            }
        }, token);

        _inFlight[key] = capture;
    }

    private void FinishCapture(DiagnoserActionParameters parameters)
    {
        var key = BenchmarkKey(parameters);
        if (!_inFlight.TryRemove(key, out var capture) || capture.Task is null)
        {
            return;
        }

        var label = parameters.BenchmarkCase.DisplayInfo;

        // Bound the wait: sum of per-kind windows plus generous startup/teardown slack.
        var budget = TimeSpan.FromSeconds((capture.Attribute.KindList.Count * (capture.Attribute.DurationSeconds + 8)) + 15);
        bool completed;
        try
        {
            completed = capture.Task.Wait(budget);
        }
#pragma warning disable CA1031 // teardown is best-effort; surface the failure in the report, never throw
        catch (Exception ex)
        {
            // Wait throws only after the task has completed (faulted); disposing the capture is safe.
            capture.Dispose();
            _resultLines.Add($"{label}: diagnostics failed — {ex.GetBaseException().Message}");
            return;
        }
#pragma warning restore CA1031

        if (!completed)
        {
            // Cancel the wedged collector so a hung EventPipe session can't leak past the run.
            // In-process we cannot kill a child; we cancel and abandon. The background task may
            // still be running, so we must NOT dispose the capture (and its CTS) now — defer the
            // disposal to a continuation that fires once the task actually unwinds.
            capture.Cancellation.Cancel();
            capture.Task.ContinueWith(
                static (_, state) => ((RunningCapture)state!).Dispose(),
                capture,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            _resultLines.Add($"{label}: diagnostics timed out after {budget.TotalSeconds:N0}s (collector canceled)");
            return;
        }

        using (capture)
        {
            foreach (var (kind, capt) in capture.Captures)
            {
                var fileName = $"{Sanitize(key)}.{capture.Sequence}.{kind}.json";
                var path = Path.Combine(_artifactsDir!, fileName);
                File.WriteAllText(path, capt.Json);
                _entries.Add(new BenchmarkDiagnosticEntry(label, kind, capt.IsError, capt.Summary, capt.Headline, path));
                _resultLines.Add($"{label} [{kind}] {(capt.IsError ? "⚠ " : string.Empty)}{capt.Headline} -> {path}");
            }
        }
    }

    public IEnumerable<Metric> ProcessResults(DiagnoserResults results) => Array.Empty<Metric>();

    public void DisplayResults(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        logger.WriteLine();
        logger.WriteLineHeader("// * dotnet-diagnostics indicators *");
        if (_artifactsDir is not null)
        {
            logger.WriteLineInfo($"//   artifacts: {_artifactsDir}");
        }

        if (_resultLines.Count == 0)
        {
            logger.WriteLineInfo("//   (no diagnostic captures were recorded — is the benchmark tagged with [DiagnosticKind]?)");
            return;
        }

        foreach (var line in _resultLines)
        {
            logger.WriteLineInfo($"//   {line}");
        }
    }

    public IEnumerable<ValidationError> Validate(ValidationParameters validationParameters)
    {
        ArgumentNullException.ThrowIfNull(validationParameters);

        foreach (var benchmark in validationParameters.Benchmarks)
        {
            var attribute = GetDiagnosticKind(benchmark);
            if (attribute is null)
            {
                continue;
            }

            foreach (var kind in attribute.KindList)
            {
                if (!InProcessDiagnosticCollector.IsSupported(kind))
                {
                    yield return new ValidationError(
                        isCritical: true,
                        $"[DiagnosticKind] on '{benchmark.Descriptor.WorkloadMethod.Name}' references unsupported collect kind '{kind}'. Supported: {string.Join(", ", InProcessDiagnosticCollector.SupportedKinds)}.",
                        benchmark);
                }
            }
        }
    }

    private static DiagnosticKindAttribute? GetDiagnosticKind(BenchmarkCase benchmarkCase)
        => benchmarkCase.Descriptor.WorkloadMethod.GetCustomAttribute<DiagnosticKindAttribute>();

    private static string BenchmarkKey(DiagnoserActionParameters parameters)
    {
        // FolderInfo is filesystem-safe and unique per parameterized case; combine with the job id
        // so distinct jobs of the same case don't overwrite each other's artifacts.
        var benchmarkCase = parameters.BenchmarkCase;
        return $"{benchmarkCase.FolderInfo}-{benchmarkCase.Job.ResolvedId}";
    }

    private static string Sanitize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        }

        return sb.ToString();
    }

    public void Dispose() => _collector.Dispose();

    private sealed class RunningCapture(DiagnosticKindAttribute attribute, int sequence) : IDisposable
    {
        public DiagnosticKindAttribute Attribute { get; } = attribute;

        public int Sequence { get; } = sequence;

        public Task? Task { get; set; }

        public ConcurrentDictionary<string, KindCapture> Captures { get; } = new(StringComparer.Ordinal);

        public CancellationTokenSource Cancellation { get; } = new();

        public void Dispose() => Cancellation.Dispose();
    }
}

/// <summary>
/// A single per-benchmark, per-kind diagnostic capture surfaced in the offenders report.
/// </summary>
/// <param name="Benchmark">The benchmark case display name.</param>
/// <param name="Kind">The <c>collect</c> kind that was run.</param>
/// <param name="IsError">True when the collection returned an error envelope.</param>
/// <param name="Summary">The Core envelope's human-readable summary.</param>
/// <param name="Headline">A one-line headline (summary, or the error kind+message on failure).</param>
/// <param name="ArtifactPath">Path to the serialized JSON envelope on disk.</param>
public sealed record BenchmarkDiagnosticEntry(
    string Benchmark,
    string Kind,
    bool IsError,
    string Summary,
    string Headline,
    string ArtifactPath);
