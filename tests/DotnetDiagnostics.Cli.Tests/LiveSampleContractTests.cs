using System.Diagnostics;
using DotnetDiagnostics.TestSupport;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

public sealed class LiveSampleContractTests
{
    [Fact]
    public async Task OutputRetentionAndUnterminatedLineAreBoundedAtInsertion()
    {
        var evidence = new LiveSampleEvidence();
        var lines = new List<string>();
        await LiveSampleOutput.DrainAsync(new StringReader(new string('x', 100_000)), false, evidence,
            lines.Add, CancellationToken.None);
        lines.Should().BeEmpty();
        evidence.Describe().Should().Contain("droppedCharacters=95904").And.Contain("oversizedLines=1");
        evidence.Describe().Length.Should().BeLessThan(5000);
    }

    [Fact]
    public async Task UrlCanSpanReaderChunksAndBeFollowedByUnboundedNoise()
    {
        var evidence = new LiveSampleEvidence();
        using var reader = new ChunkReader("Now listening on: http://127.0.0.1:1234\n" + new string('n', 20_000));
        var lines = new List<string>();
        await LiveSampleOutput.DrainAsync(reader, false, evidence, lines.Add, CancellationToken.None);
        lines.Should().ContainSingle().Which.Should().Be("Now listening on: http://127.0.0.1:1234");
        evidence.Describe().Should().Contain("oversizedLines=1");
    }

    [Theory]
    [InlineData("diagnostic-ready-enter")]
    [InlineData("url-harvest-enter")]
    [InlineData("http-ready-enter")]
    public async Task CancellationAtOwnedStartupBoundaryPreservesPhaseAndCleansChild(string phase)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var evidence = new LiveSampleEvidence(text => { if (text.EndsWith(phase, StringComparison.Ordinal)) entered.TrySetResult(); });
        using var cancellation = new CancellationTokenSource();
        var child = new FakeChild
        {
            Stdout = phase == "url-harvest-enter" ? new BlockingReader() :
                new StringReader("Now listening on: http://127.0.0.1:1234\n"),
        };
        var hooks = Hooks(child) with
        {
            DiagnosticReady = (_, _, token) => phase == "diagnostic-ready-enter" ? Task.Delay(Timeout.Infinite, token) : Task.CompletedTask,
            HttpReady = (_, _, _, token) => Task.Delay(Timeout.Infinite, token),
        };
        var startup = LiveSampleProcess.StartAsync("sample.dll", new() { WaitForHttpReady = true },
            hooks, evidence, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        var action = () => startup;
        var failure = await action.Should().ThrowAsync<InvalidOperationException>();
        failure.Which.Message.Should().Contain(phase).And.Contain("cleanup-exit").And.Contain("pid=4242");
        child.Kills.Should().Be(1);
        child.Disposed.Should().BeTrue();
        child.HasExited.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReaderFailureIsNotSilentlyConvertedToMissingUrl(bool stderr)
    {
        var child = new FakeChild
        {
            Stdout = stderr ? new BlockingReader() : new BrokenReader(),
            Stderr = stderr ? new BrokenReader() : new StringReader("retained stderr\n"),
        };
        var action = () => LiveSampleProcess.StartAsync("sample.dll", new() { WaitForHttpReady = true },
            Hooks(child), new());
        var failure = await action.Should().ThrowAsync<InvalidOperationException>();
        failure.Which.Message.Should().Contain($"{(stderr ? "stderr" : "stdout")}-reader")
            .And.Contain("injected reader failure").And.Contain("cleanup-exit");
        child.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task ExitBeforeUrlRetainsExitStatusAndDoesNotKillAnAlreadyExitedChild()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var evidence = new LiveSampleEvidence(text => { if (text.EndsWith("url-harvest-enter", StringComparison.Ordinal)) entered.TrySetResult(); });
        var child = new FakeChild { Stdout = new BlockingReader() };
        var startup = LiveSampleProcess.StartAsync("sample.dll", new() { WaitForHttpReady = true }, Hooks(child), evidence);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        child.Exit(17);
        var action = () => startup;
        (await action.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("exited=True exitCode=17");
        child.Kills.Should().Be(0);
        child.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task CleanupFailureIsExplicitAndNeverTouchesAnUnownedChild()
    {
        var child = new FakeChild { KillError = new InvalidOperationException("injected kill failure") };
        var other = new FakeChild();
        var evidence = new LiveSampleEvidence();
        var sample = await LiveSampleProcess.StartAsync("sample.dll", new(), Hooks(child), evidence);
        var action = async () => await sample.DisposeAsync();
        (await action.Should().ThrowAsync<AggregateException>()).Which.Message
            .Should().Contain("owned-process-stop").And.Contain("exited=False").And.Contain("injected kill failure");
        child.Disposed.Should().BeTrue();
        other.Kills.Should().Be(0);
        child.Exit(1);
        other.Dispose();
    }

    [Fact]
    public async Task SuccessfulCleanupIsIdempotentAndRetainsBothOutputTails()
    {
        var child = new FakeChild { Stdout = new StringReader("stdout evidence"), Stderr = new StringReader("stderr evidence") };
        var sample = await LiveSampleProcess.StartAsync("sample.dll", new(), Hooks(child), new());
        await sample.DisposeAsync();
        await sample.DisposeAsync();
        child.Kills.Should().Be(1);
        sample.Evidence.Describe().Should().Contain("stdout evidence").And.Contain("stderr evidence").And.Contain("cleanup-exit");
    }

    [Fact]
    public async Task CancelledSynchronousLaunchRetainsOwnershipOfTheLateChild()
    {
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var child = new FakeChild();
        var evidence = new LiveSampleEvidence();
        var hooks = Hooks(child) with
        {
            Start = _ => { entered.TrySetResult(); release.Wait(); return child; },
        };
        var startup = LiveSampleProcess.StartAsync("sample.dll",
            new() { CleanupTimeout = TimeSpan.FromMilliseconds(100) }, hooks, evidence, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cancellation.CancelAsync();
            var action = () => startup.WaitAsync(TimeSpan.FromSeconds(5));
            (await action.Should().ThrowAsync<InvalidOperationException>()).Which.Message
                .Should().Contain("pending-start-cleanup-incomplete");
            child.Kills.Should().Be(0);
        }
        finally { release.Set(); }
        await child.Disposal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        child.Kills.Should().Be(1);
        evidence.Describe().Should().Contain("late-start-owned pid=4242");
    }

    [Fact]
    public async Task FaultedCancellationExceptionIsNotSilentlyTreatedAsOwnedTaskCancellation()
    {
        var evidence = new LiveSampleEvidence();
        var budget = new LiveTestBudget(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1), evidence);
        budget.Own(Task.FromException(new OperationCanceledException("faulted, not cancelled")));
        var action = async () => await budget.DisposeAsync();
        (await action.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("collection-cleanup-failed").And.Contain("Faulted");
    }

    [Fact]
    public async Task HttpRequestReceivesGateCancellationInsteadOfItsDefaultHundredSecondTimeout()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new BlockingHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1") };
        var readiness = DiagnosticReadiness.WaitForHttpReadyAsync(client, TimeSpan.FromMinutes(1), "/", cancellation.Token);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        var action = () => readiness.WaitAsync(TimeSpan.FromSeconds(5));
        await action.Should().ThrowAsync<OperationCanceledException>();
        handler.RequestToken.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task StuckOwnedCollectionFailsWithinCleanupReserveWithItsPhase()
    {
        var evidence = new LiveSampleEvidence();
        var budget = new LiveTestBudget(TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(50), evidence);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        budget.Own(stopped.Task);
        try
        {
            var action = async () => await budget.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            (await action.Should().ThrowAsync<InvalidOperationException>()).Which.Message
                .Should().Contain("collection-cleanup-failed").And.Contain("WaitingForActivation");
        }
        finally { stopped.TrySetResult(); }
    }

    private static LiveSampleHooks Hooks(FakeChild child) => new()
    {
        Start = _ => child,
        DiagnosticReady = (_, _, _) => Task.CompletedTask,
        HttpReady = (_, _, _, _) => Task.CompletedTask,
    };

    private sealed class FakeChild : IOwnedSampleChild
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Process Process => throw new NotSupportedException("No OS process belongs to this deterministic fixture.");
        public int Id => 4242;
        public bool HasExited { get; private set; }
        public int ExitCode { get; private set; }
        public TextReader Stdout { get; init; } = new StringReader("");
        public TextReader Stderr { get; init; } = new StringReader("");
        public Exception? KillError { get; init; }
        public int Kills { get; private set; }
        public bool Disposed { get; private set; }
        public TaskCompletionSource Disposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Exit(int code) { ExitCode = code; HasExited = true; _exit.TrySetResult(); }
        public void Kill() { Kills++; if (KillError is not null) throw KillError; Exit(137); }
        public Task WaitForExitAsync(CancellationToken token) => _exit.Task.WaitAsync(token);
        public void Dispose() { Disposed = true; Disposal.TrySetResult(); }
    }

    private sealed class BlockingReader : TextReader
    {
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
    private sealed class BrokenReader : TextReader
    {
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new IOException("injected reader failure"));
    }
    private sealed class ChunkReader(string text) : StringReader(text)
    {
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);
    }
    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken RequestToken { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestToken = cancellationToken;
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new();
        }
    }
}
