using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotnetDiagnostics.Core.Tests.HeapFragmentationSpike;

internal static class HeapFragmentationLiveFixtureProcess
{
    internal const int HoleCount = 8;
    internal const int HolePayloadBytes = 128 * 1024;

    private const int GuardPayloadBytes = 96 * 1024;
    private const int SeparatorPayloadBytes = 128 * 1024;

    internal static int Run(string scenario)
    {
        if (scenario is not ("tail-free" or "separated"))
        {
            Console.Error.WriteLine($"Unknown LOH fragmentation fixture scenario '{scenario}'.");
            return 2;
        }

        var leftGuard = AllocateLive(GuardPayloadBytes, 0x11);
        var separators = new byte[HoleCount - 1][];

        if (scenario == "tail-free")
        {
            for (var index = 0; index < separators.Length; index++)
            {
                separators[index] = AllocateLive(SeparatorPayloadBytes, (byte)(0x40 + index));
            }

            for (var index = 0; index < HoleCount; index++)
            {
                AllocateDead(HolePayloadBytes, (byte)(0x20 + index));
            }
        }
        else
        {
            for (var index = 0; index < HoleCount; index++)
            {
                AllocateDead(HolePayloadBytes, (byte)(0x20 + index));
                if (index < separators.Length)
                {
                    separators[index] = AllocateLive(SeparatorPayloadBytes, (byte)(0x40 + index));
                }
            }
        }

        var rightGuard = AllocateLive(GuardPayloadBytes, 0x71);
        var leftHandle = GCHandle.Alloc(leftGuard, GCHandleType.Pinned);
        var rightHandle = GCHandle.Alloc(rightGuard, GCHandleType.Pinned);
        try
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);

            var leftData = unchecked((ulong)leftHandle.AddrOfPinnedObject().ToInt64());
            var rightData = unchecked((ulong)rightHandle.AddrOfPinnedObject().ToInt64());
            Console.WriteLine($"loh-ready {leftData:x} {rightData:x}");
            Console.Out.Flush();
            _ = Console.ReadLine();

            GC.KeepAlive(separators);
            return 0;
        }
        finally
        {
            rightHandle.Free();
            leftHandle.Free();
            GC.KeepAlive(rightGuard);
            GC.KeepAlive(leftGuard);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static byte[] AllocateLive(int length, byte marker)
    {
        var value = new byte[length];
        value[0] = marker;
        value[^1] = marker;
        return value;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocateDead(int length, byte marker)
    {
        var value = new byte[length];
        value[0] = marker;
        value[^1] = marker;
        GC.KeepAlive(value);
    }
}
