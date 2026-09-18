using System.Diagnostics.Tracing;
using DotnetDiagnostics.Core.Gc;
using Microsoft.Diagnostics.NETCore.Client;

namespace DotnetDiagnostics.Cli.Tests;

internal sealed class CliGcActivitiesReadiness
{
    private const string ProviderName = "System.Runtime";
    private readonly TaskCompletionSource _streamReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal bool IsStreamReady => _streamReady.Task.IsCompletedSuccessfully;

    internal EventPipeGcCollector CreateCollector(Action<string> collectionStarted) => new()
    {
        CollectionStarted = collectionStarted,
        ReadinessProvider = new EventPipeProvider(ProviderName, EventLevel.Informational,
            (long)EventKeywords.All, new Dictionary<string, string> { ["EventCounterIntervalSec"] = "1" }),
        ConfigureReadiness = source => source.Dynamic.All += e => Observe(e.ProviderName, e.EventName),
    };

    internal void Observe(string provider, string eventName)
    {
        if (provider == ProviderName && eventName == "EventCounters")
            _streamReady.TrySetResult();
    }

    internal async Task ObserveWorkloadAsync(Task bothStreamsObserved,
        Func<CancellationToken, Task> emitNoise, CancellationToken cancellationToken)
    {
        // Forced GC during session startup can leave orphan boundaries and permanently invalidate
        // the entire capture. A counter observed in this GC stream is readiness, not a timed guess.
        await _streamReady.Task.WaitAsync(cancellationToken);
        for (var attempt = 0; attempt < 60 && !bothStreamsObserved.IsCompleted; attempt++)
        {
            await emitNoise(cancellationToken);
            await Task.WhenAny(bothStreamsObserved, Task.Delay(50, cancellationToken));
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!bothStreamsObserved.IsCompleted)
            throw new TimeoutException("Both collectors did not observe the bounded readiness workload.");
        await bothStreamsObserved;
    }
}
