using System.Diagnostics;

namespace DotnetDiagnostics.TestSupport;

/// <summary>Owns one published-DLL sample, bounded startup evidence, readers and process-tree teardown.</summary>
public sealed class LiveSampleProcess : IAsyncDisposable
{
    private readonly IOwnedSampleChild _child;
    private readonly LiveSampleOptions _options;
    private readonly LiveSampleHooks _hooks;
    private readonly CancellationTokenSource _readerCancellation = new();
    private readonly TaskCompletionSource<string> _url = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception> _readerFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _exit;
    private readonly Task _stdout;
    private readonly Task _stderr;
    private readonly object _disposeGate = new();
    private Task? _dispose;
    private string? _baseUrl;

    private LiveSampleProcess(IOwnedSampleChild child, string sampleDll, LiveSampleOptions options,
        LiveSampleHooks hooks, LiveSampleEvidence evidence)
    {
        _child = child;
        _options = options;
        _hooks = hooks;
        Evidence = evidence;
        SampleDll = sampleDll;
        ProcessId = child.Id;
        Evidence.Mark($"process-started pid={ProcessId}");
        _exit = Task.Run(() => child.WaitForExitAsync(CancellationToken.None));
        _stdout = Task.Run(() => DrainAsync(stderr: false));
        _stderr = Task.Run(() => DrainAsync(stderr: true));
    }

    public Process Process => _child.Process;
    public int ProcessId { get; }
    public string SampleDll { get; }
    public LiveSampleEvidence Evidence { get; }
    public bool IsRunning => !_child.HasExited;
    public string BaseUrl => _baseUrl ?? throw new InvalidOperationException("HTTP readiness has not completed.");

    public static Task<LiveSampleProcess> StartPublishedAsync(string sampleName, LiveSampleOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new();
        var evidence = options.Evidence ?? new();
        evidence.Mark($"sample-locate {sampleName}");
        var dll = SampleLocator.LocateSampleDll(sampleName)
            ?? throw SkipException.ForReason($"{sampleName}.dll not found. Build the sample before running this test.\n{evidence.Describe()}");
        return StartAsync(dll, options, new(), evidence, cancellationToken);
    }

    internal static async Task<LiveSampleProcess> StartAsync(string dll, LiveSampleOptions options,
        LiveSampleHooks hooks, LiveSampleEvidence evidence, CancellationToken cancellationToken = default)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(options.StartupTimeout);
        evidence.Mark($"startup-budget timeout={options.StartupTimeout} diagnostic={options.DiagnosticTimeout} http={options.HttpTimeout} cleanup={options.CleanupTimeout}");
        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(dll))!,
        };
        info.ArgumentList.Add(dll);
        if (options.BindHttpPort)
        {
            info.ArgumentList.Add("--urls");
            info.ArgumentList.Add("http://127.0.0.1:0");
        }
        info.Environment["DOTNET_NOLOGO"] = "1";
        info.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        if (options.Environment is not null)
            foreach (var (key, value) in options.Environment) info.Environment[key] = value;

        LiveSampleProcess? sample = null;
        try
        {
            startup.Token.ThrowIfCancellationRequested();
            evidence.Mark("process-start-request");
            var pending = Task.Run(() => hooks.Start(info), startup.Token);
            IOwnedSampleChild child;
            try { child = await pending.WaitAsync(startup.Token).ConfigureAwait(false); }
            catch
            {
                // A synchronous OS launch cannot be aborted. Keep ownership if it returns late.
                var cleanup = CleanupLateStartAsync(pending, dll, options, hooks, evidence);
                try { await cleanup.WaitAsync(options.CleanupTimeout).ConfigureAwait(false); }
                catch (Exception error)
                {
                    evidence.Error("pending-start-cleanup", error);
                    evidence.Mark("pending-start-cleanup-incomplete");
                }
                throw;
            }
            sample = new(child, dll, options, hooks, evidence);
            evidence.Mark("diagnostic-ready-enter");
            await sample.WaitBoundaryAsync(hooks.DiagnosticReady(sample.ProcessId, options.DiagnosticTimeout, startup.Token),
                startup.Token).ConfigureAwait(false);
            evidence.Mark("diagnostic-ready");
            if (options.WaitForHttpReady)
                await sample.WaitForListeningUrlAsync(options.HttpTimeout, options.ReadinessPath, startup.Token).ConfigureAwait(false);
            evidence.Mark("startup-complete");
            return sample;
        }
        catch (Exception error)
        {
            evidence.Error("startup", error);
            evidence.Mark("startup-failed");
            if (sample is not null)
            {
                evidence.Mark($"startup-failure-state {sample.ProcessStatus()} {sample.ReaderStatus()}");
                try { await sample.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { evidence.Error("startup-cleanup", cleanup); }
            }
            throw new InvalidOperationException($"Sample startup failed: {error.Message}\n{evidence.Describe()}", error);
        }
    }

    private static async Task CleanupLateStartAsync(Task<IOwnedSampleChild> pending, string dll,
        LiveSampleOptions options, LiveSampleHooks hooks, LiveSampleEvidence evidence)
    {
        try
        {
            var child = await pending.ConfigureAwait(false);
            evidence.Mark($"late-start-owned pid={child.Id}");
            await new LiveSampleProcess(child, dll, options, hooks, evidence).DisposeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (pending.IsCanceled) { evidence.Mark("process-start-cancelled-before-launch"); }
        catch (Exception error) { evidence.Error("late-start", error); evidence.Mark("late-start-failed"); }
    }

    private async Task DrainAsync(bool stderr)
    {
        var stream = stderr ? "stderr" : "stdout";
        Evidence.Mark($"{stream}-reader-enter");
        try
        {
            var reader = stderr ? _child.Stderr : _child.Stdout;
            await LiveSampleOutput.DrainAsync(reader, stderr, Evidence, line =>
            {
                if (stderr || !(_options.HarvestListeningUrl || _options.WaitForHttpReady)) return;
                const string prefix = "Now listening on:";
                var index = line.IndexOf(prefix, StringComparison.Ordinal);
                if (index < 0) return;
                var candidate = line[(index + prefix.Length)..].Trim();
                if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                    _url.TrySetResult(candidate))
                    Evidence.Mark($"url-harvested {candidate}");
            }, _readerCancellation.Token).ConfigureAwait(false);
            Evidence.Mark($"{stream}-reader-eof");
            if (!stderr && (_options.HarvestListeningUrl || _options.WaitForHttpReady) && !_url.Task.IsCompleted)
                _readerFailure.TrySetResult(new IOException("stdout ended before advertising a listening URL."));
        }
        catch (OperationCanceledException) when (_readerCancellation.IsCancellationRequested)
        {
            Evidence.Mark($"{stream}-reader-cancelled");
        }
        catch (Exception error)
        {
            Evidence.Error($"{stream}-reader", error);
            Evidence.Mark($"{stream}-reader-failed");
            if (!_readerCancellation.IsCancellationRequested) _readerFailure.TrySetResult(error);
        }
    }

    private async Task WaitBoundaryAsync(Task operation, CancellationToken token)
    {
        var completed = await Task.WhenAny(operation, _readerFailure.Task, _exit).WaitAsync(token).ConfigureAwait(false);
        if (completed == _readerFailure.Task) throw new IOException("Sample output reader failed.", await _readerFailure.Task.ConfigureAwait(false));
        if (completed == _exit) throw new InvalidOperationException($"Sample exited during startup: {ProcessStatus()}");
        await operation.WaitAsync(token).ConfigureAwait(false);
        if (_readerFailure.Task.IsCompleted) throw new IOException("Sample output reader failed.", await _readerFailure.Task.ConfigureAwait(false));
        if (_exit.IsCompleted) throw new InvalidOperationException($"Sample exited during startup: {ProcessStatus()}");
    }

    public async Task<string> WaitForListeningUrlAsync(TimeSpan timeout, string readinessPath = "/",
        CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            Evidence.Mark("url-harvest-enter");
            await WaitBoundaryAsync(_url.Task, deadline.Token).ConfigureAwait(false);
            var url = await _url.Task.ConfigureAwait(false);
            Evidence.Mark("http-ready-enter");
            await WaitBoundaryAsync(_hooks.HttpReady(url, timeout, readinessPath, deadline.Token), deadline.Token).ConfigureAwait(false);
            _baseUrl = url;
            Evidence.Mark("http-ready");
            return url;
        }
        catch (Exception error)
        {
            Evidence.Error("http-startup", error);
            Evidence.Mark($"http-startup-failed {ProcessStatus()} {ReaderStatus()}");
            throw new InvalidOperationException($"HTTP startup failed.\n{Evidence.Describe()}", error);
        }
    }

    private string ProcessStatus()
    {
        try
        {
            var exited = _child.HasExited;
            return $"pid={ProcessId} exited={exited} exitCode={(exited ? _child.ExitCode : null)}";
        }
        catch (Exception error) { Evidence.Error("process-status", error); return $"pid={ProcessId} exitStatus=unknown"; }
    }

    private string ReaderStatus() => $"stdoutTask={_stdout.Status} stderrTask={_stderr.Status}";

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new(_dispose ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Evidence.Mark($"cleanup-enter {ProcessStatus()}");
        using var deadline = new CancellationTokenSource(_options.CleanupTimeout);
        var errors = new List<Exception>(6);
        async Task AttemptAsync(string phase, Func<Task> action)
        {
            try { await action().WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch (Exception error) { Evidence.Error(phase, error); errors.Add(error); }
        }
        await AttemptAsync("owned-process-stop", async () =>
        {
            if (!_child.HasExited) await Task.Run(_child.Kill).ConfigureAwait(false);
            await _exit.ConfigureAwait(false);
        }).ConfigureAwait(false);
        await AttemptAsync("reader-cancellation", () => _readerCancellation.CancelAsync()).ConfigureAwait(false);
        await AttemptAsync("reader-drain", () => Task.WhenAll(_stdout, _stderr)).ConfigureAwait(false);
        Evidence.Mark($"cleanup-process-status {ProcessStatus()}");
        await AttemptAsync("stdout-dispose", () => Task.Run(_child.Stdout.Dispose)).ConfigureAwait(false);
        await AttemptAsync("stderr-dispose", () => Task.Run(_child.Stderr.Dispose)).ConfigureAwait(false);
        await AttemptAsync("process-dispose", () => Task.Run(_child.Dispose)).ConfigureAwait(false);
        _readerCancellation.Dispose();
        Evidence.Mark(errors.Count == 0 ? "cleanup-exit" : "cleanup-exit-with-errors");
        if (errors.Count > 0) throw new AggregateException($"Owned sample cleanup failed.\n{Evidence.Describe()}", errors);
    }
}
