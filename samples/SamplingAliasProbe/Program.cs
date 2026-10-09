using System.Diagnostics;
using System.Runtime.CompilerServices;

// Calibration workload for issue #1078 (sampler timer aliasing).
// Paths A and B run byte-identical work; M runs ~1% of that. Wall-clock truth is recorded with
// Stopwatch. Args: <chunkMs> <totalSeconds>. The chunk length is the loop period knob;
// total work per path is held constant regardless of it.
var chunkMs = args.Length > 0 ? double.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture) : 1.0;
var totalSeconds = args.Length > 1 ? double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 30.0;

var itersPerMs = Calibrate();
Console.WriteLine($"READY pid={Environment.ProcessId} itersPerMs={itersPerMs:F0}");
Console.Out.Flush();

var chunk = (long)(chunkMs * itersPerMs);
var minorChunk = Math.Max(1, chunk / 98);
long ticksA = 0, ticksB = 0, ticksM = 0;
var total = Stopwatch.StartNew();
var sw = new Stopwatch();
while (total.Elapsed.TotalSeconds < totalSeconds)
{
    sw.Restart(); PathA(chunk); ticksA += sw.ElapsedTicks;
    sw.Restart(); PathB(chunk); ticksB += sw.ElapsedTicks;
    sw.Restart(); PathM(minorChunk); ticksM += sw.ElapsedTicks;
}

double ms(long t) => t * 1000.0 / Stopwatch.Frequency;
Console.WriteLine($"TRUTH A={ms(ticksA):F1} B={ms(ticksB):F1} M={ms(ticksM):F1}");
return 0;

static long Calibrate()
{
    Work(1_000_000);
    var s = Stopwatch.StartNew();
    Work(20_000_000);
    return (long)(20_000_000 / s.Elapsed.TotalMilliseconds);
}

[MethodImpl(MethodImplOptions.NoInlining)]
static long Work(long n)
{
    long x = 1;
    for (long i = 0; i < n; i++)
    {
        x = (x * 6364136223846793005L) + 1442695040888963407L;
    }
    return x;
}

[MethodImpl(MethodImplOptions.NoInlining)]
static long PathA(long n) => Work(n);

[MethodImpl(MethodImplOptions.NoInlining)]
static long PathB(long n) => Work(n);

[MethodImpl(MethodImplOptions.NoInlining)]
static long PathM(long n) => Work(n);
