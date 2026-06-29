using System.Threading;

namespace DotnetDiagnostics.Core.Etw;

/// <summary>
/// Process-wide gate serializing every NT Kernel Logger ETW capture across all kernel-mode
/// samplers (off-CPU, native-allocation, NativeAOT CPU). The NT Kernel Logger is a single global
/// slot on Windows: two overlapping kernel sessions — even from different sampler types — cause
/// buffer starvation or session start failures. A per-class semaphore only serializes captures of
/// the same type, so all kernel samplers must share this one gate instead.
/// </summary>
internal static class KernelEtwSessionGate
{
    /// <summary>The shared, process-wide kernel ETW session gate (permits one capture at a time).</summary>
    public static readonly SemaphoreSlim Gate = new(1, 1);
}
