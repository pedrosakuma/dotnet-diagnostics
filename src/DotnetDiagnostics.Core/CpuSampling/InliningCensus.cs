namespace DotnetDiagnostics.Core.CpuSampling;

/// <summary>
/// One aggregated JIT inlining decision, keyed by the compiled code version that took it. The decision is
/// re-taken at every compilation, so the same (inliner, inlinee) pair appears once per version.
/// </summary>
public sealed record InliningDecisionRow(
    string VersionId,
    string CompiledMethod,
    string OptimizationTier,
    string Inliner,
    string Inlinee,
    bool Succeeded,
    string? FailReason,
    long Count);

/// <summary>Bounded inlining decisions joined to the code version that took them (issue #1076).</summary>
public sealed record InliningProfile(
    long TotalDecisions,
    long AttributedDecisions,
    long UnattributedDecisions,
    long SucceededDecisions,
    long RefusedDecisions,
    long VersionsWithDecisions,
    IReadOnlyList<InliningDecisionRow> Decisions,
    IReadOnlyList<string> Notes);

/// <summary>A single inlining event as delivered by the runtime (names only; no method id or address).</summary>
public readonly record struct InliningDecision(
    string Inliner,
    string Inlinee,
    bool Succeeded,
    string? FailReason);

/// <summary>
/// Joins JIT inlining events to the code version that took them. The events carry names only, so the join is
/// per thread: <c>MethodJittingStarted</c> opens a compilation on a thread, inlining events on that thread
/// are buffered, and the matching <c>MethodLoadVerbose</c> (same method id, same thread) publishes the
/// version the buffered decisions belong to. Decisions that cannot be tied to a published version are
/// counted as unattributed instead of being guessed. TraceEvent-free so it can be driven synthetically.
/// </summary>
public sealed class InliningCensus
{
    /// <summary>Maximum distinct (version, inliner, inlinee, outcome) rows retained.</summary>
    public const int MaxInliningRecords = 65_536;

    /// <summary>Maximum threads with an in-flight compilation tracked at once.</summary>
    public const int MaxPendingThreads = 1_024;

    /// <summary>Maximum buffered decisions for one in-flight compilation.</summary>
    public const int MaxDecisionsPerCompilation = 4_096;

    /// <summary>Maximum buffered decisions across all in-flight compilations.</summary>
    public const int MaxPendingDecisions = 65_536;

    /// <summary>Maximum length of any retained name or reason string; longer ones are truncated.</summary>
    public const int MaxNameLength = 512;

    private readonly Dictionary<uint, Pending> _pending = [];
    private readonly Dictionary<RowKey, Row> _rows = [];
    private readonly HashSet<string> _versions = [];
    private long _total;
    private long _attributed;
    private long _succeeded;
    private long _refused;
    private long _droppedRecords;
    private long _droppedPending;
    private long _droppedPerCompilation;
    private long _droppedPendingBudget;
    private int _pendingDecisions;
    private long _truncatedStrings;

    private sealed class Pending(ulong methodId, string compiledMethod)
    {
        public ulong MethodId { get; } = methodId;
        public string CompiledMethod { get; } = compiledMethod;
        public List<InliningDecision> Decisions { get; } = [];
    }

    private readonly record struct RowKey(string VersionId, string Inliner, string Inlinee, bool Succeeded, string? Reason);

    private sealed class Row(string compiled, string tier)
    {
        public string Compiled { get; } = compiled;
        public string Tier { get; } = tier;
        public long Count;
    }

    /// <summary>Opens a compilation on <paramref name="threadId"/>; an unfinished previous one is discarded as unattributed.</summary>
    public void OnJittingStarted(uint threadId, ulong methodId, string compiledMethod)
    {
        // An unfinished previous compilation is dropped; its decisions stay counted but unattributed.
        DiscardPending(threadId);

        if (_pending.Count >= MaxPendingThreads)
        {
            _droppedPending++;
            return;
        }

        _pending[threadId] = new Pending(methodId, Clip(compiledMethod));
    }

    /// <summary>Records one inlining decision seen on <paramref name="threadId"/>.</summary>
    public void OnInlining(uint threadId, InliningDecision decision)
    {
        _total++;
        if (decision.Succeeded)
        {
            _succeeded++;
        }
        else
        {
            _refused++;
        }

        if (!_pending.TryGetValue(threadId, out var pending))
        {
            return;
        }

        if (pending.Decisions.Count >= MaxDecisionsPerCompilation)
        {
            _droppedPerCompilation++;
            return;
        }

        if (_pendingDecisions >= MaxPendingDecisions)
        {
            _droppedPendingBudget++;
            return;
        }

        _pendingDecisions++;

        pending.Decisions.Add(new InliningDecision(
            Clip(decision.Inliner), Clip(decision.Inlinee), decision.Succeeded,
            decision.FailReason is null ? null : Clip(decision.FailReason)));
    }

    /// <summary>
    /// Publishes a code version loaded on <paramref name="threadId"/>. When it closes the thread's open
    /// compilation (same method id) the buffered decisions are committed to that version.
    /// </summary>
    public void OnMethodLoaded(uint threadId, PublishedCodeVersion version)
    {
        if (!_pending.TryGetValue(threadId, out var pending) || pending.MethodId != version.MethodId)
        {
            return;
        }

        DiscardPending(threadId);
        if (pending.Decisions.Count == 0 || version.StartAddress == 0)
        {
            return;
        }

        var versionId = CodeVersionCensus.FormatVersionId(version);
        foreach (var d in pending.Decisions)
        {
            var key = new RowKey(versionId, d.Inliner, d.Inlinee, d.Succeeded, d.FailReason);
            if (_rows.TryGetValue(key, out var row))
            {
                row.Count++;
                _attributed++;
                continue;
            }

            if (_rows.Count >= MaxInliningRecords)
            {
                _droppedRecords++;
                continue;
            }

            _rows[key] = new Row(pending.CompiledMethod, version.OptimizationTier) { Count = 1 };
            _versions.Add(versionId);
            _attributed++;
        }
    }

    private void DiscardPending(uint threadId)
    {
        if (_pending.Remove(threadId, out var removed))
        {
            _pendingDecisions -= removed.Decisions.Count;
        }
    }

    /// <summary>Builds the deterministic profile. Compilations still open at the end are unattributed.</summary>
    public InliningProfile Build()
    {
        _pending.Clear();
        _pendingDecisions = 0;

        var rows = _rows
            .Select(kv => new InliningDecisionRow(
                kv.Key.VersionId, kv.Value.Compiled, kv.Value.Tier, kv.Key.Inliner, kv.Key.Inlinee,
                kv.Key.Succeeded, kv.Key.Reason, kv.Value.Count))
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.VersionId, StringComparer.Ordinal)
            .ThenBy(r => r.Inliner, StringComparer.Ordinal)
            .ThenBy(r => r.Inlinee, StringComparer.Ordinal)
            .ThenBy(r => r.Succeeded)
            .ThenBy(r => r.FailReason, StringComparer.Ordinal)
            .ToArray();

        var notes = new List<string>();
        if (_total == 0)
        {
            notes.Add("No JIT inlining events were found in the trace. Either nothing was compiled during the capture, or the JitTracing keyword (0x1000) did not reach the runtime.");
        }

        if (_droppedRecords > 0)
        {
            notes.Add($"MaxInliningRecords ({MaxInliningRecords}) reached; {_droppedRecords} inlining decisions were dropped.");
        }

        if (_droppedPendingBudget > 0)
        {
            notes.Add($"MaxPendingDecisions ({MaxPendingDecisions}) reached; {_droppedPendingBudget} decisions were not buffered and are unattributed.");
        }

        if (_droppedPending > 0)
        {
            notes.Add($"MaxPendingThreads ({MaxPendingThreads}) reached; {_droppedPending} compilations were not tracked, so their decisions are unattributed.");
        }

        if (_droppedPerCompilation > 0)
        {
            notes.Add($"MaxDecisionsPerCompilation ({MaxDecisionsPerCompilation}) reached; {_droppedPerCompilation} inlining decisions were dropped.");
        }

        if (_truncatedStrings > 0)
        {
            notes.Add($"MaxNameLength ({MaxNameLength}) reached; {_truncatedStrings} name or reason strings were truncated.");
        }

        var unattributed = _total - _attributed;
        if (unattributed > 0)
        {
            notes.Add($"{unattributed} inlining decisions could not be tied to a published code version (compilation not observed from its start, no matching load event, or dropped by a cap) and are reported as unattributed rather than guessed.");
        }

        return new InliningProfile(_total, _attributed, unattributed, _succeeded, _refused, _versions.Count, rows, notes);
    }

    private string Clip(string value)
    {
        if (value.Length <= MaxNameLength)
        {
            return value;
        }

        _truncatedStrings++;
        return value[..MaxNameLength];
    }
}
