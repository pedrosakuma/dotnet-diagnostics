using System.Net;
using System.Threading.Channels;
using DotnetDiagnostics.TestSupport;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

public sealed class HttpReadinessTests
{
    private static readonly TimeSpan ShortDeadline = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task DeadlineCancelsTheObservedRequestAndWaitsForItsCompletion()
    {
        var clock = new ControlledClock();
        using var handler = new PendingHandler();
        using var http = Client(handler);
        http.Timeout.Should().Be(TimeSpan.FromSeconds(100));
        var readiness = WaitAsync(http, clock);
        try
        {
            var deadline = await clock.NextTimerAsync(ShortDeadline);
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            deadline.Fire();
            await handler.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            handler.RequestToken.IsCancellationRequested.Should().BeTrue();
            handler.Active.Should().BeTrue();
            readiness.IsCompleted.Should().BeFalse("deadline cancellation must not abandon the in-flight handler");
            handler.Finish.TrySetResult();
            var action = () => readiness.WaitAsync(TimeSpan.FromSeconds(5));
            (await action.Should().ThrowAsync<SkipException>()).Which.Message.Should()
                .Be("Sample did not accept HTTP requests on http://127.0.0.1:1/weatherforecast within the timeout.");
            handler.Exited.Task.IsCompletedSuccessfully.Should().BeTrue();
            handler.Active.Should().BeFalse("readiness must not return while its canceled request is still active");
            deadline.Disposed.Should().BeTrue();
        }
        finally
        {
            handler.Finish.TrySetResult();
            http.CancelPendingRequests();
            await handler.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await ObserveStoppedAsync(readiness);
        }
    }

    [Fact]
    public async Task SuccessCompletesOneRequestAndDisposesItsResponse()
    {
        var clock = new ControlledClock();
        var content = new DisposalContent();
        using var handler = new ScriptedHandler(_ => new(HttpStatusCode.OK) { Content = content });
        using var http = Client(handler);
        await WaitAsync(http, clock);
        handler.Calls.Should().Be(1);
        content.Disposed.Should().BeTrue();
        (await clock.NextTimerAsync(ShortDeadline)).Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task TransportFailureRetriesAtTheExistingPollingIntervalThenSucceeds()
    {
        var clock = new ControlledClock();
        using var handler = new ScriptedHandler(attempt => attempt == 1
            ? throw new HttpRequestException("owned simulated connection refusal")
            : new(HttpStatusCode.OK));
        using var http = Client(handler);
        var readiness = WaitAsync(http, clock);
        var deadline = await clock.NextTimerAsync(ShortDeadline);
        var poll = await clock.NextTimerAsync(TimeSpan.FromMilliseconds(250));
        handler.Calls.Should().Be(1);
        readiness.IsCompleted.Should().BeFalse();
        poll.Fire();
        await readiness.WaitAsync(TimeSpan.FromSeconds(5));
        handler.Calls.Should().Be(2);
        deadline.Disposed.Should().BeTrue();
        poll.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task RepeatedTransportFailureCannotResetOrOutliveTheOriginalDeadline()
    {
        var clock = new ControlledClock();
        using var handler = new ScriptedHandler(_ => throw new HttpRequestException("owned refusal"));
        using var http = Client(handler);
        var readiness = WaitAsync(http, clock);
        var deadline = await clock.NextTimerAsync(ShortDeadline);
        (await clock.NextTimerAsync(TimeSpan.FromMilliseconds(250))).Fire();
        var secondPoll = await clock.NextTimerAsync(TimeSpan.FromMilliseconds(250));
        handler.Calls.Should().Be(2);
        deadline.Fire();
        var action = () => readiness.WaitAsync(TimeSpan.FromSeconds(5));
        await action.Should().ThrowAsync<SkipException>();
        secondPoll.Disposed.Should().BeTrue();
        secondPoll.Fire();
        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task UnsuccessfulResponseIsDisposedAndPollingStopsAtTheDeadline()
    {
        var clock = new ControlledClock();
        var content = new DisposalContent();
        using var handler = new ScriptedHandler(_ => new(HttpStatusCode.ServiceUnavailable) { Content = content });
        using var http = Client(handler);
        var readiness = WaitAsync(http, clock);
        var deadline = await clock.NextTimerAsync(ShortDeadline);
        var poll = await clock.NextTimerAsync(TimeSpan.FromMilliseconds(250));
        content.Disposed.Should().BeTrue();
        deadline.Fire();
        var action = () => readiness.WaitAsync(TimeSpan.FromSeconds(5));
        await action.Should().ThrowAsync<SkipException>();
        poll.Disposed.Should().BeTrue();
        handler.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationIsNotMisclassifiedEvenWhenTheDeadlineAlsoExpires(bool expireDeadline)
    {
        var clock = new ControlledClock();
        using var cancellation = new CancellationTokenSource();
        using var handler = new PendingHandler();
        using var http = Client(handler);
        var readiness = WaitAsync(http, clock, cancellation.Token);
        try
        {
            var deadline = await clock.NextTimerAsync(ShortDeadline);
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (expireDeadline) deadline.Fire();
            await cancellation.CancelAsync();
            await handler.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            handler.Finish.TrySetResult();
            var action = () => readiness.WaitAsync(TimeSpan.FromSeconds(5));
            await action.Should().ThrowAsync<OperationCanceledException>();
            handler.Active.Should().BeFalse();
        }
        finally
        {
            handler.Finish.TrySetResult();
            http.CancelPendingRequests();
            await handler.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await ObserveStoppedAsync(readiness);
        }
    }

    [Fact]
    public async Task UnrelatedHandlerCancellationIsNotRelabeledAsReadinessTimeout()
    {
        using var handler = new ScriptedHandler(_ => throw new OperationCanceledException("not the readiness deadline"));
        using var http = Client(handler);
        var action = () => WaitAsync(http, new ControlledClock());
        await action.Should().ThrowAsync<OperationCanceledException>();
        handler.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonpositiveTimeoutPreservesImmediateSkipWithoutSendingARequest(int milliseconds)
    {
        using var handler = new ScriptedHandler(_ => new(HttpStatusCode.OK));
        using var http = Client(handler);
        var action = () => DiagnosticReadiness.WaitForHttpReadyAsync(http, TimeSpan.FromMilliseconds(milliseconds),
            "/weatherforecast", new ControlledClock());
        await action.Should().ThrowAsync<SkipException>();
        handler.Calls.Should().Be(0);
    }

    private static HttpClient Client(HttpMessageHandler handler)
        => new(handler) { BaseAddress = new Uri("http://127.0.0.1:1") };

    private static Task WaitAsync(HttpClient http, ControlledClock clock, CancellationToken token = default)
        => DiagnosticReadiness.WaitForHttpReadyAsync(http, ShortDeadline, "/weatherforecast", clock, token);

    private static async Task ObserveStoppedAsync(Task readiness)
    {
        try { await readiness.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (SkipException) { }
        catch (OperationCanceledException) { }
    }

    private sealed class PendingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken RequestToken { get; private set; }
        public bool Active { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Active = true;
            RequestToken = token;
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.OK); }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                await Finish.Task;
                throw;
            }
            finally { Active = false; Exited.TrySetResult(); }
        }
    }

    private sealed class ScriptedHandler(Func<int, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(response(++Calls));
    }

    private sealed class DisposalContent : HttpContent
    {
        public bool Disposed { get; private set; }
        protected override bool TryComputeLength(out long length) { length = 0; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    // Same manually fired TimeProvider/ITimer pattern as GcDumpFlushLifecycleTests.
    internal sealed class ControlledClock : TimeProvider
    {
        private readonly Channel<ControlledTimer> _created = Channel.CreateBounded<ControlledTimer>(4);
        public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("Readiness must not depend on wall-clock time.");
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            period.Should().Be(Timeout.InfiniteTimeSpan);
            var timer = new ControlledTimer(callback, state, dueTime);
            if (!_created.Writer.TryWrite(timer)) throw new InvalidOperationException("Unexpected timer growth.");
            return timer;
        }
        public async Task<ControlledTimer> NextTimerAsync(TimeSpan expectedDelay)
        {
            var timer = await _created.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            timer.Delay.Should().Be(expectedDelay);
            return timer;
        }
    }

    internal sealed class ControlledTimer(TimerCallback callback, object? state, TimeSpan delay) : ITimer
    {
        private int _disposed;
        public TimeSpan Delay => delay;
        public bool Disposed => Volatile.Read(ref _disposed) != 0;
        public void Fire() { if (!Disposed) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
