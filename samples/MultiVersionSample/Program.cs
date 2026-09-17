using System.Diagnostics;
using System.Runtime.InteropServices;

// Minimal, TFM-agnostic workload used by cross-version diagnostic tests (see
// docs/research/multi-version-target-support.md). Prints a stable "READY" line once started so
// test harnesses can synchronize without an HTTP listener (this sample intentionally has none —
// keeping it a plain console app avoids dragging ASP.NET Core's own per-TFM package matrix into
// what should be a pure CoreCLR/diagnostic-IPC compatibility check).
Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"PID: {Environment.ProcessId}");
Console.Out.Flush();

if (args.Contains("--gc-pause-workload", StringComparer.Ordinal))
{
    // Bounded rooted graph (~34 MiB). Requests come only from the owned test harness.
    var roots = Enumerable.Range(0, 65_536).Select(_ => new byte[512]).ToArray();
    GC.Collect(2, GCCollectionMode.Forced, blocking: true);
    using var readiness = new Timer(_ => GcReadiness.Log.Pulse(), null, 0, 10);
    Console.WriteLine("READY");
    Console.Out.Flush();
    for (var request = 0; request < 8; request++)
    {
        var command = Console.ReadLine();
        if (command is not ("blocking" or "background")) break;
        GC.Collect(2, GCCollectionMode.Forced, blocking: command == "blocking", compacting: false);
        for (var heartbeat = 0; heartbeat < 100; heartbeat++)
        {
            Console.WriteLine($"Heartbeat: {DateTimeOffset.UtcNow.UtcTicks}");
            Thread.Sleep(1);
        }
        Console.Out.Flush();
        GC.KeepAlive(roots);
    }
    return;
}

var cache = new List<byte[]>();
var rng = new Random(42);
var generateGcEvents = args.Contains("--gc-events", StringComparer.Ordinal);
var progress = Stopwatch.StartNew();
var progressInterval = TimeSpan.FromMilliseconds(generateGcEvents ? 250 : 1_000);
long allocations = 0;
var inducedCollections = 0;

Console.WriteLine("READY");
Console.Out.Flush();

// Steady allocation + occasional CPU burn: enough signal for EventCounters and a
// heap dump to show non-trivial content, without needing per-endpoint HTTP routing.
while (true)
{
    cache.Add(new byte[40_000]);
    allocations++;
    if (cache.Count > 400)
    {
        cache.RemoveRange(0, 200);
    }

    if (rng.Next(20) == 0)
    {
        BurnCpu(TimeSpan.FromMilliseconds(20));
    }

    if (progress.Elapsed >= progressInterval)
    {
        // Allocation throughput and GC budgets vary by host. The GC-event fixture explicitly
        // induces collections throughout its lifetime, not just before EventPipe attaches.
        if (generateGcEvents)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            inducedCollections++;
        }

        Console.WriteLine($"Workload: allocations={allocations}, allocatedBytes={GC.GetTotalAllocatedBytes()}, gen0={GC.CollectionCount(0)}, gen1={GC.CollectionCount(1)}, gen2={GC.CollectionCount(2)}, induced={inducedCollections}");
        Console.Out.Flush();
        progress.Restart();
    }

    Thread.Sleep(2);
}

static void BurnCpu(TimeSpan duration)
{
    var deadline = DateTime.UtcNow + duration;
    var spin = new SpinWait();
    while (DateTime.UtcNow < deadline)
    {
        spin.SpinOnce();
    }

}

[System.Diagnostics.Tracing.EventSource(Name = "DotnetDiagnostics.GcReadiness")]
sealed class GcReadiness : System.Diagnostics.Tracing.EventSource
{
    internal static readonly GcReadiness Log = new();
    [System.Diagnostics.Tracing.Event(1)]
    public void Pulse() => WriteEvent(1);
}
