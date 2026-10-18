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
        "Root counts and retention paths cover handle, static and finalizer-queue roots only; analyze a dump " +
        "(collect_process_dump + inspect_heap source=dump) for stack roots, or set " +
        LiveStackRootsEnvironmentVariable + "=1 to accept the risk.";

    private static readonly ConditionalWeakTable<ClrRuntime, object> LiveRuntimes = new();

    internal static void MarkLive(ClrRuntime runtime) => LiveRuntimes.TryAdd(runtime, new object());

    internal static bool SkipsStackRoots(ClrRuntime runtime) =>
        LiveRuntimes.TryGetValue(runtime, out _) &&
        ShouldSkipStackRoots(isLive: true, Environment.GetEnvironmentVariable(LiveStackRootsEnvironmentVariable));

    internal static bool ShouldSkipStackRoots(bool isLive, string? liveStackRootsSetting) =>
        isLive && !string.Equals(liveStackRootsSetting, "1", StringComparison.Ordinal);

    internal static string CreateSkippedStackRootsWarning(string omittedEvidence) =>
        "Live stack-root enumeration was skipped because the CLR DAC can fault natively (SIGSEGV) while walking " +
        "a thread caught mid-transition, which would terminate this host (issue #1160). " +
        omittedEvidence + " Use a dump-origin inspection for complete stack-root evidence, or set " +
        LiveStackRootsEnvironmentVariable + "=1 to accept the risk.";

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
            if (!handle.IsStrong)
            {
                continue;
            }

            yield return handle;

            if (handle.HandleKind == ClrHandleKind.AsyncPinned)
            {
                foreach (var pinned in EnumerateAsyncPinnedTargets(handle))
                {
                    yield return pinned;
                }
            }
        }

        foreach (var root in runtime.Heap.EnumerateAdditionalRoots())
        {
            yield return root;
        }

        foreach (var root in runtime.Heap.EnumerateFinalizerRoots())
        {
            yield return root;
        }
    }

    // Mirrors ClrHeap.EnumerateRoots: an async-pinned handle roots OverlappedData.m_userObject
    // (or, for an object[] user object, each element) as pinned objects.
    private static IEnumerable<IClrRoot> EnumerateAsyncPinnedTargets(ClrHandle handle)
    {
        var overlapped = handle.Object;
        if (!overlapped.IsValid || overlapped.Type is null)
        {
            yield break;
        }

        var userObject = overlapped.ReadObjectField("m_userObject");
        if (!userObject.IsValid || userObject.IsNull)
        {
            yield break;
        }

        if (userObject.IsArray && userObject.Type?.ComponentType?.IsObjectReference == true)
        {
            var array = userObject.AsArray();
            for (var i = 0; i < array.Length; i++)
            {
                var element = array.GetObjectValue(i);
                if (element.IsValid && !element.IsNull)
                {
                    yield return new PinnedRoot(element, handle.Address);
                }
            }
        }
        else
        {
            yield return new PinnedRoot(userObject, handle.Address);
        }
    }

    private sealed class PinnedRoot(ClrObject target, ulong address) : IClrRoot
    {
        public ulong Address { get; } = address;

        public IClrValue Object { get; } = target;

        public ClrRootKind RootKind => ClrRootKind.AsyncPinnedHandle;

        public bool IsInterior => false;

        public bool IsPinned => true;

        public string? RegisterName => null;

        public int RegisterOffset => 0;

        public IClrStackFrame? StackFrame => null;

        public bool Equals(IClrRoot? other) =>
            other is not null && other.Address == Address && other.Object.Address == Object.Address;
    }
}
