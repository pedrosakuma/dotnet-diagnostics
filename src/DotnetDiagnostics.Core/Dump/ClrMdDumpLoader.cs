using Microsoft.Diagnostics.Runtime;

namespace DotnetDiagnostics.Core.Dump;

/// <summary>
/// Central place to open a <see cref="DataTarget"/> against a dump file, so every dump-based
/// call site picks up the same reader options.
/// </summary>
/// <remarks>
/// Uses ClrMD 4.0's opt-in lock-free memory-mapped reader
/// (<see cref="DataTargetOptions.UseLockFreeMemoryMapReader"/>), which satisfies reads directly
/// from a memory-mapped view of the file instead of per-read locks/seeks over a stream. Benchmarked
/// (see issue #686 / <c>docs/hotpaths/group-c-remaining.md</c>) at ~1.3-1.8x faster wall-clock for a
/// full heap-walk + root-enumeration pass over a 1.5 GiB dump, with identical object/byte counts, so
/// it is safe to enable by default. The resulting reader is not thread-safe, but every dump-based
/// call site in this codebase opens its own short-lived <see cref="DataTarget"/> inside a single
/// method call and never shares it across threads, so this has no observable effect here.
/// Memory-mapping the whole file needs a contiguous address-space reservation the size of the
/// dump; on a 32-bit process this can fail (<see cref="OutOfMemoryException"/>) well before
/// physical memory is exhausted for dumps approaching 2 GiB, so it is only enabled when
/// <see cref="Environment.Is64BitProcess"/> — the sidecar always runs as a 64-bit process, but this
/// keeps the helper safe if it's ever consumed from a 32-bit host.
/// </remarks>
internal static class ClrMdDumpLoader
{
    public static DataTarget Load(string dumpFilePath) =>
        DataTarget.LoadDump(dumpFilePath, new DataTargetOptions
        {
            UseLockFreeMemoryMapReader = Environment.Is64BitProcess,
        });
}
