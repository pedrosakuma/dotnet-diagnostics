namespace DotnetDiagnostics.Core.Dump;

/// <summary>
/// Result of an opt-in <c>ClrHeap.VerifyHeap()</c> pass (issue #1119) — a corruption-triage check
/// distinct from (and more expensive than) the ordinary <see cref="ClrMdHeapWalker"/> type/instance
/// walk. Dump-only; gated by <see cref="DumpInspectionOptions.VerifyHeap"/>.
/// </summary>
/// <param name="TotalCorruptions">Total corrupted objects ClrMD reported across the full pass —
/// an exact count even when <see cref="Corruptions"/> is capped. When <see cref="Completed"/> is
/// <c>false</c>, this is only a lower bound (the count observed before the enumeration failed).</param>
/// <param name="Corruptions">Bounded list of corrupted objects, capped at
/// <see cref="HeapIntegrityAggregation.MaxCapturedCorruptions"/>.</param>
/// <param name="Notes">Non-empty when the cap in <see cref="Corruptions"/> was hit, or the
/// underlying <c>VerifyHeap()</c> enumeration failed partway through (see <see cref="Completed"/>).</param>
/// <param name="Completed">
/// <c>false</c> when <c>ClrHeap.VerifyHeap()</c> threw before finishing its pass. A <c>false</c>
/// value means <see cref="TotalCorruptions"/> is a lower bound, not an exact count — in
/// particular, <c>TotalCorruptions == 0 &amp;&amp; !Completed</c> must never be reported as a
/// "clean"/"passed" heap: verification did not actually finish.
/// </param>
public sealed record HeapIntegrityView(
    int TotalCorruptions,
    IReadOnlyList<HeapCorruptionStat> Corruptions,
    IReadOnlyList<string> Notes,
    bool Completed = true)
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
    /// <param name="captured">Corruption entries retained so far (already capped at <paramref name="cap"/>).</param>
    /// <param name="totalObserved">Exact count observed during the pass, or a lower bound when <paramref name="failureMessage"/> is set.</param>
    /// <param name="cap">Maximum number of entries <paramref name="captured"/> may hold.</param>
    /// <param name="failureMessage">
    /// Non-null when <c>ClrHeap.VerifyHeap()</c> threw before completing its enumeration. When
    /// set, the returned view has <see cref="HeapIntegrityView.Completed"/> = <c>false</c> and
    /// always carries a note, even if no corruption was observed before the failure — a
    /// zero-corruption count from an incomplete pass must never read as "healthy".
    /// </param>
    public static HeapIntegrityView Build(
        IReadOnlyList<HeapCorruptionStat> captured,
        int totalObserved,
        int cap = MaxCapturedCorruptions,
        string? failureMessage = null)
    {
        ArgumentNullException.ThrowIfNull(captured);
        var omitted = Math.Max(0, totalObserved - captured.Count);
        var notes = new List<string>();
        if (omitted > 0)
        {
            notes.Add(
                $"ClrHeap.VerifyHeap() found {totalObserved:N0} corrupted object(s); retained the first " +
                $"{captured.Count:N0} after reaching HeapIntegrityAggregation.MaxCapturedCorruptions={cap}. " +
                $"{omitted:N0} additional corruption entr{(omitted == 1 ? "y" : "ies")} were omitted.");
        }

        if (failureMessage is not null)
        {
            notes.Add(failureMessage);
        }

        return new HeapIntegrityView(totalObserved, captured, notes, Completed: failureMessage is null);
    }
}
