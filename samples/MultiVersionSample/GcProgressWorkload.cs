using System.Diagnostics;
using System.Diagnostics.Tracing;

internal static class GcProgressWorkload
{
    internal static void Run()
    {
        // Same fixed ~34 MiB graph as the original pause fixture; no throughput-dependent growth.
        var roots = Enumerable.Range(0, 65_536).Select(_ => new byte[512]).ToArray();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        using var readiness = new Timer(_ => GcReadiness.Log.Pulse(), null, 0, 10);
        Console.WriteLine("READY");
        Console.Out.Flush();
        for (var request = 0; request < 8; request++)
        {
            var arm = Console.ReadLine();
            if (arm is null) return;
            if (arm != "arm") throw new InvalidOperationException("Expected witness arm command.");
            using var witness = new ProgressWitness(request);
            // The harness waits for this worker's actual EventPipe progress before sending a GC request.
            var command = Console.ReadLine();
            if (command is null) return;
            if (command is not ("blocking" or "background"))
                throw new InvalidOperationException("Expected blocking or background GC request.");
            GcReadiness.Log.Request(request);
            GC.Collect(2, GCCollectionMode.Forced, blocking: command == "blocking", compacting: false);
            GcReadiness.Log.Returned(request);
            // GC.Collect can return before background GCStop. Only the observing harness sends stop.
            if (Console.ReadLine() != "stop") throw new InvalidOperationException("Expected witness stop command.");
            witness.StopAndJoin();
            GcReadiness.Log.Completed(request, witness.Count, witness.Status);
            Console.WriteLine($"GcRequestDone: {request}; samples={witness.Count}; status={witness.Status}");
            Console.Out.Flush();
            GC.KeepAlive(roots);
        }
    }

    private sealed class ProgressWitness : IDisposable
    {
        private const int MaxSamples = 32_768;
        private readonly Thread _thread;
        private int _stop;
        internal int Count { get; private set; }
        internal int Status { get; private set; }

        internal ProgressWitness(int request)
        {
            _thread = new Thread(() => Observe(request)) { IsBackground = true };
            _thread.Start();
        }

        private void Observe(int request)
        {
            var deadline = Stopwatch.GetTimestamp() + 2 * Stopwatch.Frequency;
            GcReadiness.Log.Armed(request);
            while (Volatile.Read(ref _stop) == 0)
            {
                if (Stopwatch.GetTimestamp() >= deadline) { Status = 1; break; }
                if (Count == MaxSamples) { Status = 2; break; }
                GcReadiness.Log.Progress(request, Count++);
                // Bounded active sampling: Sleep(1) is commonly ~16ms on Windows, longer than a BGC.
                var next = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10_000;
                while (Stopwatch.GetTimestamp() < next && Volatile.Read(ref _stop) == 0)
                    Thread.SpinWait(10);
            }
        }

        internal void StopAndJoin()
        {
            Volatile.Write(ref _stop, 1);
            if (!_thread.Join(TimeSpan.FromSeconds(3)))
                throw new TimeoutException("GC progress witness did not terminate.");
        }

        public void Dispose() => StopAndJoin();
    }
}

[EventSource(Name = "DotnetDiagnostics.GcReadiness")]
internal sealed class GcReadiness : EventSource
{
    internal static readonly GcReadiness Log = new();
    [Event(1)] public void Pulse() => WriteEvent(1);
    [Event(2)] public void Armed(int request) => WriteEvent(2, request);
    [Event(3)] public void Progress(int request, int sequence) => WriteEvent(3, request, sequence);
    [Event(4)] public void Request(int request) => WriteEvent(4, request);
    [Event(5)] public void Returned(int request) => WriteEvent(5, request);
    [Event(6)] public void Completed(int request, int count, int status) => WriteEvent(6, request, count, status);
}
