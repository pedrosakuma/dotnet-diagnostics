namespace DotnetDiagnostics.Core.NativeLockContention;

/// <summary>
/// Evidence taxonomy for native synchronization diagnostics. Native-lock sampling reports activity;
/// off-CPU futex/native-sync spans provide blocking evidence when target/thread correlation is reliable.
/// </summary>
public sealed record NativeContentionEvidence(
    string Level,
    string Summary,
    long SampledLockCallCount = 0,
    long NativeSyncSpanCount = 0,
    long ClosedNativeSyncSpanCount = 0,
    long CensoredNativeSyncSpanCount = 0,
    long NativeSyncOffCpuMicros = 0,
    long ClosedNativeSyncOffCpuMicros = 0,
    long CensoredNativeSyncOffCpuMicros = 0,
    long AmbiguousNativeSyncFrameSpanCount = 0,
    long AmbiguousNativeSyncFrameOffCpuMicros = 0,
    IReadOnlyList<string>? EvidenceSources = null,
    IReadOnlyList<string>? ConfidenceRationale = null,
    IReadOnlyList<string>? UncertaintyNotes = null);

public static class NativeContentionEvidenceLevels
{
    public const string None = "none";
    public const string Activity = "activity";
    public const string ProbableBlocking = "probable-blocking";
    public const string ConfirmedBlocking = "confirmed-blocking";
}

internal enum NativeContentionSpanClassification
{
    None,
    AmbiguousNativeSyncFrame,
    ProbableNativeSync,
    ConfirmedFutexBlocking,
}

internal readonly record struct NativeContentionEvidenceStatistics(
    long NativeSyncSpanCount,
    long ClosedNativeSyncSpanCount,
    long CensoredNativeSyncSpanCount,
    long NativeSyncOffCpuMicros,
    long ClosedNativeSyncOffCpuMicros,
    long CensoredNativeSyncOffCpuMicros,
    long AmbiguousNativeSyncFrameSpanCount,
    long AmbiguousNativeSyncFrameOffCpuMicros,
    bool HasProbableNonFutexNativeSync);
