using System.Runtime.CompilerServices;

// Fixed population, one pending task and one parked thread; no steady allocation loop.
internal static class CompatibilityFixture
{
    private static readonly TaskCompletionSource Signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void Run()
    {
        var retained = Enumerable.Range(0, 32).Select(_ => new RetainedMarker()).ToArray();
        var pending = PendingAsync();
        using var stop = new ManualResetEventSlim();
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() => ClosedGenericHold(931, ready, stop))
        {
            Name = "compatibility-closed-int32",
            IsBackground = true,
        };
        thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("Closed generic fixture did not park.");
        Console.WriteLine("Fixture: live-compatibility; retained=32; generic=System.Int32; lifetimeSeconds=180");
        Console.WriteLine("READY");
        Console.Out.Flush();
        var joined = false;
        try
        {
            Thread.Sleep(TimeSpan.FromSeconds(180));
        }
        finally
        {
            stop.Set();
            Signal.TrySetResult();
            joined = thread.Join(TimeSpan.FromSeconds(5));
            GC.KeepAlive(retained);
            GC.KeepAlive(pending);
        }
        if (!joined)
            throw new TimeoutException("Closed generic fixture did not stop.");
    }

    private static async Task PendingAsync() => await Signal.Task.ConfigureAwait(false);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static void ClosedGenericHold<T>(T value, ManualResetEventSlim ready, ManualResetEventSlim stop)
        where T : struct
    {
        ready.Set();
        stop.Wait(TimeSpan.FromSeconds(180));
        GC.KeepAlive(value);
    }
}

internal sealed class RetainedMarker
{
    // An identifiable nonempty retained graph, bounded to 128 KiB of payload.
    private readonly byte[] _payload = new byte[4096];
    public int Length => _payload.Length;
}
