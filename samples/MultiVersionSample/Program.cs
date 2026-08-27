using System.Runtime.InteropServices;

// Minimal, TFM-agnostic workload used by cross-version diagnostic tests (see
// docs/research/multi-version-target-support.md). Prints a stable "READY" line once started so
// test harnesses can synchronize without an HTTP listener (this sample intentionally has none —
// keeping it a plain console app avoids dragging ASP.NET Core's own per-TFM package matrix into
// what should be a pure CoreCLR/diagnostic-IPC compatibility check).
Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"PID: {Environment.ProcessId}");
Console.Out.Flush();

var cache = new List<byte[]>();
var rng = new Random(42);

Console.WriteLine("READY");
Console.Out.Flush();

// Steady allocation + occasional CPU burn: enough signal for EventCounters, GC events, and a
// heap dump to show non-trivial content, without needing per-endpoint HTTP routing.
while (true)
{
    cache.Add(new byte[40_000]);
    if (cache.Count > 400)
    {
        cache.RemoveRange(0, 200);
    }

    if (rng.Next(20) == 0)
    {
        BurnCpu(TimeSpan.FromMilliseconds(20));
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
