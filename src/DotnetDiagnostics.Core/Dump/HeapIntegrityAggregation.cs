namespace DotnetDiagnostics.Core.Dump;

/// <summary>
/// Result of an opt-in <c>ClrHeap.VerifyHeap()</c> pass (issue #1119) — a corruption-triage check
/// distinct from (and more expensive than) the ordinary <see cref="ClrMdHeapWalker"/> type/instance
/// walk. Dump-only; gated by <see cref="DumpInspectionOptions.VerifyHeap"/>.
/// </summary>
/// <param name="TotalCorruptions">Total corrupted objects ClrMD reported across the full pass —
/// an exact count even when <see cref="Corruptions"/> is capped.</param>
/// <param name="Corruptions">Bounded list of corrupted objects, capped at
/// <see cref="HeapIntegrityAggregation.MaxCapturedCorruptions"/>.</param>
/// <param name="Notes">Non-empty only when the cap in <see cref="Corruptions"/> was hit, or the
/// underlying <c>VerifyHeap()</c> enumeration failed partway through.</param>
public sealed record HeapIntegrityView(
    int TotalCorruptions,
    IReadOnlyList<HeapCorruptionStat> Corruptions,
    IReadOnlyList<string> Notes)
{
    /// <summary>True when <see cref="TotalCorruptions"/> exceeds the number of entries retained in <see cref="Corruptions"/>.</summary>
    public bool Truncated => TotalCorruptions > Corruptions.Count;
}

/// <summary>
/// One corrupted object reported by <c>ClrHeap.VerifyHeap()</c>, projected from ClrMD's
/// <c>ObjectCorruption</c> (<c>Object</c>, <c>Offset</c>, <c>Kind</c>, <c>SyncBlockIndex</c>,
/// <c>ClrSyncBlockIndex</c>).
/// </summary>
public sealed record HeapCorruptionStat(
    ulong ObjectAddress,
    string? TypeFullName,
    int Offset,
    string Kind,
    int SyncBlockIndex,
    int ClrSyncBlockIndex);

/// <summary>
/// Bounds the corruption list ClrMD's <c>ClrHeap.VerifyHeap()</c> can return. Per
/// docs/resource-boundedness.md convention #1, the cap must be enforced during collection (the
/// caller stops appending to its accumulator once the cap is reached) rather than by materializing
/// everything and slicing afterward — see <see cref="ClrMdDumpInspector"/>'s heap-integrity walk.
/// </summary>
public static class HeapIntegrityAggregation
{
    /// <summary>Maximum corrupted-object entries retained in a <see cref="HeapIntegrityView"/>.</summary>
    public const int MaxCapturedCorruptions = 500;

    /// <summary>
    /// Builds the bounded view from the entries retained during collection (already capped at
    /// <see cref="MaxCapturedCorruptions"/>) plus the exact total observed during the full pass.
    /// </summary>
    public static HeapIntegrityView Build(
        IReadOnlyList<HeapCorruptionStat> captured,
        int totalObserved,
        int cap = MaxCapturedCorruptions)
    {
        ArgumentNullException.ThrowIfNull(captured);
        var omitted = Math.Max(0, totalObserved - captured.Count);
        var notes = omitted > 0
            ? new[]
              {
                  $"ClrHeap.VerifyHeap() found {totalObserved:N0} corrupted object(s); retained the first " +
                  $"{captured.Count:N0} after reaching HeapIntegrityAggregation.MaxCapturedCorruptions={cap}. " +
                  $"{omitted:N0} additional corruption entr{(omitted == 1 ? "y" : "ies")} were omitted.",
              }
            : Array.Empty<string>();

        return new HeapIntegrityView(totalObserved, captured, notes);
    }
}
