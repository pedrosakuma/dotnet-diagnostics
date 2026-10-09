using System.Globalization;

namespace DotnetDiagnostics.Core.CpuSampling;

/// <summary>
/// One compiled body (code version) of a managed method that received leaf samples. A method name
/// does not identify machine code: under tiered compilation one method is published several times
/// (QuickJitted, OptimizedTier1OSR, OptimizedTier1, ...), so rows for the same method with different
/// <see cref="VersionId"/> values must not be silently summed.
/// </summary>
public sealed record CodeVersionSampleRow(
    string VersionId,
    string Module,
    string Method,
    string OptimizationTier,
    string StartAddress,
    long Size,
    long ReJitId,
    long Samples,
    int MethodVersionCount,
    long MethodSamples);

/// <summary>
/// Bounded join of leaf samples to the published code version they landed in (issue #1075).
/// Only leaf (self-time) instruction pointers are attributed; callers' return addresses are not.
/// </summary>
public sealed record CodeVersionProfile(
    long TotalSamples,
    long ResolvedSamples,
    long UnresolvedSamples,
    long PublishedVersions,
    IReadOnlyList<CodeVersionSampleRow> Versions,
    IReadOnlyList<string> Notes);

/// <summary>A published native code range as reported by the runtime (rundown or load events).</summary>
public readonly record struct PublishedCodeVersion(
    ulong MethodId,
    ulong ModuleId,
    uint MethodToken,
    ulong StartAddress,
    uint Size,
    ulong ReJitId,
    string OptimizationTier);

/// <summary>
/// Collects published code versions from method load / rundown events and joins leaf instruction
/// pointers to them deterministically. Event kinds are passed as flags so the type has no dependency
/// on TraceEvent and can be driven with synthetic data.
/// </summary>
/// <remarks>
/// A sampling capture carries no <c>MethodLoadVerbose</c>; names and tiers arrive in the session-end
/// rundown (<c>MethodDCStopVerbose</c>). The rundown also emits <c>MethodDCStartVerbose</c> for the same
/// bodies, so versions are de-duplicated by (MethodId, StartAddress) and a version is counted once.
/// Both the published-version table and the distinct-leaf-address table are capped at insertion.
/// </remarks>
public sealed class CodeVersionCensus
{
    /// <summary>Maximum distinct published code versions retained for the join.</summary>
    public const int MaxPublishedVersions = 262_144;

    /// <summary>Maximum distinct leaf instruction pointers tracked; further ones are counted as overflow.</summary>
    public const int MaxDistinctLeafAddresses = 16_384;

    private readonly Dictionary<(ulong MethodId, ulong Start), PublishedCodeVersion> _versions = [];
    private readonly Dictionary<ulong, LeafAddress> _leaves = [];
    private long _droppedVersions;
    private long _overflowSamples;
    private long _total;

    private sealed class LeafAddress(string module, string method)
    {
        public string Module { get; } = module;
        public string Method { get; } = method;
        public long Count;
    }

    /// <summary>Number of distinct published versions retained.</summary>
    public int PublishedVersionCount => _versions.Count;

    /// <summary>Records a published code range. Duplicates (DCStart + DCStop, Load + DCStop) collapse.</summary>
    public void AddPublishedVersion(PublishedCodeVersion version)
    {
        if (version.StartAddress == 0 || version.Size == 0)
        {
            return;
        }

        var key = (version.MethodId, version.StartAddress);
        if (_versions.ContainsKey(key))
        {
            return;
        }

        if (_versions.Count >= MaxPublishedVersions)
        {
            _droppedVersions++;
            return;
        }

        _versions[key] = version;
    }

    /// <summary>Records one sample's leaf instruction pointer with the frame name the sampler resolved for it.</summary>
    public void AddLeafSample(ulong instructionPointer, string module, string method)
    {
        _total++;
        if (instructionPointer == 0)
        {
            return;
        }

        if (_leaves.TryGetValue(instructionPointer, out var existing))
        {
            existing.Count++;
            return;
        }

        if (_leaves.Count >= MaxDistinctLeafAddresses)
        {
            _overflowSamples++;
            return;
        }

        _leaves[instructionPointer] = new LeafAddress(module, method) { Count = 1 };
    }

    /// <summary>Joins the recorded leaf addresses to the published versions.</summary>
    public CodeVersionProfile Build()
    {
        var ranges = _versions.Values
            .OrderBy(v => v.StartAddress)
            .ThenBy(v => v.MethodId)
            .ToArray();
        var starts = new ulong[ranges.Length];
        for (var i = 0; i < ranges.Length; i++)
        {
            starts[i] = ranges[i].StartAddress;
        }

        var perVersion = new Dictionary<int, (long Samples, string Module, string Method)>();
        long resolved = 0;
        long ambiguous = 0;
        foreach (var (ip, leaf) in _leaves.OrderBy(kv => kv.Key))
        {
            // Upper bound: first range starting after ip. Scan back a few entries so versions sharing a
            // start address (distinct MethodIds) are still considered; overlaps are reported as ambiguous.
            int lo = 0, hi = starts.Length;
            while (lo < hi)
            {
                var mid = (lo + hi) >>> 1;
                if (starts[mid] <= ip) lo = mid + 1; else hi = mid;
            }

            var match = -1;
            var containing = 0;
            for (var i = lo - 1; i >= 0 && i >= lo - 4; i--)
            {
                if (ip - ranges[i].StartAddress < ranges[i].Size)
                {
                    match = i;
                    containing++;
                }
            }

            // Overlapping ranges (reused or shared code) cannot be told apart without lifetimes; refuse to guess.
            if (containing > 1)
            {
                ambiguous += leaf.Count;
                continue;
            }

            if (match < 0)
            {
                continue;
            }

            resolved += leaf.Count;
            perVersion.TryGetValue(match, out var acc);
            perVersion[match] = (acc.Samples + leaf.Count, acc.Module ?? leaf.Module, acc.Method ?? leaf.Method);
        }

        var methodTotals = new Dictionary<(string, string), (int Versions, long Samples)>();
        foreach (var acc in perVersion.Values)
        {
            var key = (acc.Module, acc.Method);
            methodTotals.TryGetValue(key, out var t);
            methodTotals[key] = (t.Versions + 1, t.Samples + acc.Samples);
        }

        var rows = perVersion
            .Select(kv =>
            {
                var v = ranges[kv.Key];
                var (versions, methodSamples) = methodTotals[(kv.Value.Module, kv.Value.Method)];
                return new CodeVersionSampleRow(
                    VersionId: FormatVersionId(v),
                    Module: kv.Value.Module,
                    Method: kv.Value.Method,
                    OptimizationTier: v.OptimizationTier,
                    StartAddress: "0x" + v.StartAddress.ToString("x", CultureInfo.InvariantCulture),
                    Size: v.Size,
                    ReJitId: (long)v.ReJitId,
                    Samples: kv.Value.Samples,
                    MethodVersionCount: versions,
                    MethodSamples: methodSamples);
            })
            .OrderByDescending(r => r.Samples)
            .ThenBy(r => r.VersionId, StringComparer.Ordinal)
            .ToArray();

        var notes = new List<string>();
        if (_versions.Count == 0 && _total > 0)
        {
            notes.Add("No method load or rundown events were found in the trace; samples cannot be attributed to code versions.");
        }

        if (_droppedVersions > 0)
        {
            notes.Add($"MaxPublishedVersions ({MaxPublishedVersions}) reached; {_droppedVersions} published code versions were dropped, so some samples may be unresolved.");
        }

        if (_overflowSamples > 0)
        {
            notes.Add($"MaxDistinctLeafAddresses ({MaxDistinctLeafAddresses}) reached; {_overflowSamples} samples with new leaf addresses were not attributed.");
        }

        if (ambiguous > 0)
        {
            notes.Add($"{ambiguous} samples fell in overlapping published code ranges and were left unresolved rather than attributed arbitrarily.");
        }

        return new CodeVersionProfile(_total, resolved, _total - resolved, _versions.Count, rows, notes);
    }

    private static string FormatVersionId(PublishedCodeVersion v)
        => string.Create(CultureInfo.InvariantCulture, $"{v.ModuleId:x}:{v.MethodToken:x8}@{v.StartAddress:x}");
}
