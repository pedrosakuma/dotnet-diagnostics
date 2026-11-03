using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace BadCodeSample;

internal static class AllocationShapeWorkload
{
    private static byte[]? s_retained;

    public static object RunPrimary(TimeSpan duration)
        => Run(duration, PrimaryOperation, "primary", 288_000);

    public static object RunPrimaryPaced(TimeSpan duration)
        => Run(
            duration,
            PrimaryOperation,
            "primary-paced",
            288_000,
            pauseMilliseconds: 1);

    public static object RunChanged(TimeSpan duration)
        => Run(duration, ChangedOperation, "changed", 288_000);

    public static object RunLow(TimeSpan duration)
        => Run(duration, LowOperation, "low", 64_000);

    public static object RunIncreased(TimeSpan duration)
        => Run(duration, IncreasedOperation, "increased", 576_000);

    public static object RunPeriodicA(TimeSpan duration)
        => Run(duration, PeriodicAOperation, "periodic-a", 192_000);

    public static object RunPeriodicB(TimeSpan duration)
        => Run(duration, PeriodicBOperation, "periodic-b", 192_000);

    public static int EnsureRetainedBytes(int bytes)
    {
        if (bytes <= 0)
        {
            return s_retained?.Length ?? 0;
        }

        if (s_retained?.Length != bytes)
        {
            s_retained = new byte[bytes];
            s_retained[0] = 1;
            s_retained[^1] = 2;
        }

        return s_retained.Length;
    }

    private static object Run(
        TimeSpan duration,
        Func<int> operation,
        string shape,
        int requestedPayloadBytesPerOperation,
        int pauseMilliseconds = 0)
    {
        var stopwatch = Stopwatch.StartNew();
        long checksum = 0;
        long operations = 0;
        while (stopwatch.Elapsed < duration)
        {
            checksum += operation();
            operations++;
            if (pauseMilliseconds > 0)
            {
                Thread.Sleep(pauseMilliseconds);
            }
        }

        return new
        {
            shape,
            operations,
            requestedPayloadBytesPerOperation,
            requestedPayloadBytes = operations * requestedPayloadBytesPerOperation,
            retainedBytes = s_retained?.Length ?? 0,
            checksum,
            elapsedMs = stopwatch.ElapsedMilliseconds,
        };
    }

    private static int PrimaryOperation()
        => AllocatePrimaryBytes() + AllocatePrimaryBytes() + AllocateSharedChars();

    private static int ChangedOperation()
        => AllocateChangedIntegers() + AllocateChangedIntegers() + AllocateSharedChars();

    private static int LowOperation()
        => AllocateLowBytes() + AllocateLowChars();

    private static int IncreasedOperation()
        => AllocateIncreasedBytes() + AllocateIncreasedBytes() + AllocateIncreasedChars();

    private static int PeriodicAOperation()
    {
        var checksum = 0;
        for (var index = 0; index < 16; index++)
        {
            checksum += AllocatePeriodicBytes();
        }
        for (var index = 0; index < 16; index++)
        {
            checksum += AllocatePeriodicChars();
        }
        return checksum;
    }

    private static int PeriodicBOperation()
    {
        var checksum = 0;
        for (var index = 0; index < 16; index++)
        {
            checksum += AllocatePeriodicBytes();
            checksum += AllocatePeriodicChars();
        }
        return checksum;
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
    private static int AllocateLowBytes()
    {
        var values = new byte[32_000];
        values[0] = 1;
        values[^1] = 2;
        return values[0] + values[^1];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int AllocateIncreasedBytes()
    {
        var values = new byte[192_000];
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
    private static int AllocateLowChars()
    {
        var values = new char[16_000];
        values[0] = 'a';
        values[^1] = 'z';
        return values[0] + values[^1];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int AllocateIncreasedChars()
    {
        var values = new char[96_000];
        values[0] = 'a';
        values[^1] = 'z';
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int AllocatePeriodicBytes()
    {
        var values = new byte[8_000];
        values[0] = 1;
        values[^1] = 2;
        return values[0] + values[^1];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int AllocatePeriodicChars()
    {
        var values = new char[2_000];
        values[0] = 'a';
        values[^1] = 'z';
        return values[0] + values[^1];
    }
}
