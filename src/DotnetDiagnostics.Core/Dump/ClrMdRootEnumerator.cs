using System.Runtime.CompilerServices;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Diagnostics.Runtime.Interfaces;

namespace DotnetDiagnostics.Core.Dump;

/// <summary>
/// Root enumeration that avoids the DAC stack-root walk on live, suspended processes (issue #1160).
/// <c>ClrHeap.EnumerateRoots()</c> reaches <c>libmscordaccore</c>'s <c>GcInfoDecoder::ReportUntrackedSlots</c>
/// for every managed thread, and for threads caught mid-transition (explicit <c>Frame</c> at the top of
/// the stack such as <c>DynamicHelperFrame</c> or <c>SoftwareExceptionFrame</c>) it can dereference a
/// target-process address as a host pointer. That is a native SIGSEGV that cannot be caught and takes
/// down the host process, so live walks use handle and finalizer-queue roots only unless the caller
/// opts in via <see cref="LiveStackRootsEnvironmentVariable"/>.
/// </summary>
internal static class ClrMdRootEnumerator
{
    internal const string LiveStackRootsEnvironmentVariable = "DOTNET_DBG_MCP_LIVE_STACK_ROOTS";

    internal const string SkippedStackRootsWarning =
        "Stack roots were not enumerated for this live process: the CLR DAC can fault natively (SIGSEGV) while " +
        "walking the stack of a thread caught mid-transition, which would terminate this host (issue #1160). " +
        "Root counts and retention paths cover handle and finalizer-queue roots only; analyze a dump " +
        "(collect_process_dump + inspect_heap source=dump) for stack roots, or set " +
        LiveStackRootsEnvironmentVariable + "=1 to accept the risk.";

    private static readonly ConditionalWeakTable<ClrRuntime, object> LiveRuntimes = new();

    internal static void MarkLive(ClrRuntime runtime) => LiveRuntimes.TryAdd(runtime, new object());

    internal static bool SkipsStackRoots(ClrRuntime runtime) =>
        LiveRuntimes.TryGetValue(runtime, out _) && !IsLiveStackRootOptIn();

    private static bool IsLiveStackRootOptIn() =>
        string.Equals(Environment.GetEnvironmentVariable(LiveStackRootsEnvironmentVariable), "1", StringComparison.Ordinal);

    internal static IEnumerable<IClrRoot> Enumerate(ClrRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        if (!SkipsStackRoots(runtime))
        {
            return runtime.Heap.EnumerateRoots();
        }

        return EnumerateWithoutStackRoots(runtime);
    }

    private static IEnumerable<IClrRoot> EnumerateWithoutStackRoots(ClrRuntime runtime)
    {
        foreach (var handle in runtime.EnumerateHandles())
        {
            if (handle.IsStrong)
            {
                yield return handle;
            }
        }

        foreach (var root in runtime.Heap.EnumerateFinalizerRoots())
        {
            yield return root;
        }
    }
}
