using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace BadCodeSample;

internal static class AllocationShapeWorkload
{
    public static object RunPrimary(TimeSpan duration)
        => Run(duration, AllocatePrimaryBytes, AllocateSharedChars, "primary");

    public static object RunChanged(TimeSpan duration)
        => Run(duration, AllocateChangedIntegers, AllocateSharedChars, "changed");

    private static object Run(
        TimeSpan duration,
        Func<int> dominant,
        Func<int> secondary,
        string shape)
    {
        var stopwatch = Stopwatch.StartNew();
        long checksum = 0;
        long iterations = 0;
        while (stopwatch.Elapsed < duration)
        {
            checksum += dominant();
            checksum += dominant();
            checksum += secondary();
            iterations++;
        }

        return new
        {
            shape,
            iterations,
            checksum,
            elapsedMs = stopwatch.ElapsedMilliseconds,
        };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int AllocatePrimaryBytes()
    {
        var values = new byte[96_000];
        values[0] = 1;
        values[^1] = 2;
        return values[0] + values[^1];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int AllocateChangedIntegers()
    {
        var values = new int[24_000];
        values[0] = 3;
        values[^1] = 4;
        return values[0] + values[^1];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int AllocateSharedChars()
    {
        var values = new char[48_000];
        values[0] = 'a';
        values[^1] = 'z';
        return values[0] + values[^1];
    }
}
