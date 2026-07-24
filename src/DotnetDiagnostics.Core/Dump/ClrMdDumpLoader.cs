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
/// (see issue #686 / <c>docs/hotpaths/group-c-remaining.md</c>) at ~1.8-2x faster wall-clock for a
/// full heap-walk + root-enumeration pass over a 1.5 GiB dump, with identical object/byte counts, so
/// it is safe to enable unconditionally. The resulting reader is not thread-safe, but every dump-based
/// call site in this codebase opens its own short-lived <see cref="DataTarget"/> inside a single
/// method call and never shares it across threads, so this has no observable effect here.
/// </remarks>
internal static class ClrMdDumpLoader
{
    public static DataTarget Load(string dumpFilePath) =>
        DataTarget.LoadDump(dumpFilePath, new DataTargetOptions { UseLockFreeMemoryMapReader = true });
}
