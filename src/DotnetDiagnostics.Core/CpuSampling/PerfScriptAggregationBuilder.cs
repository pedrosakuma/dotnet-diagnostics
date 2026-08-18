using System.Globalization;
using DotnetDiagnostics.Core.Memory;

namespace DotnetDiagnostics.Core.CpuSampling;

internal readonly record struct PerfScriptAggregationResult(
    long Total,
    IReadOnlyList<Hotspot> Hotspots,
    CallTreeNode Root,
    NativeAotSymbolDemangler.SymbolSource SymbolSource,
    IReadOnlyDictionary<SymbolRef, MethodIdentity> Identities,
    long JitCandidateFrames = 0,
    long ResolvedJitFrames = 0,
    long UnresolvedJitCandidateFrames = 0,
    bool Truncated = false);

internal sealed class PerfScriptAggregationBuilder
{
    private readonly Dictionary<string, long> _inclusive = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _exclusive = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _modules = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _displays = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MethodIdentity> _identityByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _displayCache = new(StringComparer.Ordinal);
    private readonly Dictionary<SymbolRef, MethodIdentity> _identities = new();
    private readonly CallTreeBuilder _callTree = new();
    private readonly NativeAotMethodMap? _methodMap;
    private readonly string? _moduleName;
    private readonly string? _modulePath;
    private NativeAotSymbolDemangler.SymbolSource _symbolSource = NativeAotSymbolDemangler.SymbolSource.Unknown;
    private bool _anyMangledFrameDemangled;

    public PerfScriptAggregationBuilder(
        NativeAotMethodMap? methodMap = null,
        string? moduleName = null,
        string? modulePath = null)
    {
        _methodMap = methodMap;
        _moduleName = moduleName;
        _modulePath = modulePath;
    }

    public long TotalSamples { get; private set; }

    public void AddSample(PerfSample sample)
    {
        if (sample.Frames.Count == 0)
        {
            return;
        }

        TotalSamples++;

        var rootToLeaf = new List<(string Key, string Module, string Display, MethodIdentity? Identity)>(sample.Frames.Count);
        for (var i = sample.Frames.Count - 1; i >= 0; i--)
        {
            var frame = sample.Frames[i];
            var classification = frame.Identity is null
                ? NativeAotSymbolDemangler.Classify(frame.Symbol)
                : NativeAotSymbolDemangler.SymbolSource.Unknown;
            _symbolSource = NativeAotSymbolDemangler.Combine(_symbolSource, classification);
            var cacheKey = frame.Identity is null ? frame.Symbol : "\0jit:" + frame.Symbol;
            if (!_displayCache.TryGetValue(cacheKey, out var demangled))
            {
                demangled = frame.Identity is null
                    ? NativeAotSymbolDemangler.Demangle(frame.Symbol)
                    : frame.Symbol;
                _displayCache[cacheKey] = demangled;
                if (classification == NativeAotSymbolDemangler.SymbolSource.ElfMangled &&
                    !ReferenceEquals(demangled, frame.Symbol) &&
                    !string.Equals(demangled, frame.Symbol, StringComparison.Ordinal))
                {
                    _anyMangledFrameDemangled = true;
                }
            }

            var key = BuildAggregationKey(frame.Module, demangled, frame.Identity);
            rootToLeaf.Add((key, frame.Module, demangled, frame.Identity));
            _modules.TryAdd(key, frame.Module);
            _displays.TryAdd(key, demangled);

            if (frame.Identity is not null)
            {
                _identityByKey.TryAdd(key, frame.Identity);
                _identities.TryAdd(new SymbolRef(frame.Module, demangled), frame.Identity);
            }

            if (_methodMap is not null && _methodMap.ContainsMethod(frame.Symbol))
            {
                var symbolRef = new SymbolRef(frame.Module, demangled);
                if (!_identities.ContainsKey(symbolRef))
                {
                    _identities[symbolRef] = PerfNativeAotCpuSampler.BuildAotIdentity(
                        frame.Symbol,
                        demangled,
                        _moduleName,
                        _modulePath);
                }
            }
        }

        var leafKey = rootToLeaf[^1].Key;
        _exclusive[leafKey] = _exclusive.GetValueOrDefault(leafKey) + 1;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, _, _, _) in rootToLeaf)
        {
            if (seen.Add(key))
            {
                _inclusive[key] = _inclusive.GetValueOrDefault(key) + 1;
            }
        }

        _callTree.AddStack(rootToLeaf, leafKey, new SelfSampleBreakdown(1, 0));
    }

    public PerfScriptAggregationResult Build(int topN, bool truncated = false)
    {
        var hotspots = _inclusive
            .OrderByDescending(kv => kv.Value)
            .Take(topN)
            .Select(kv =>
            {
                var module = _modules.GetValueOrDefault(kv.Key, string.Empty);
                var display = _displays.GetValueOrDefault(kv.Key, kv.Key);
                if (!_identityByKey.TryGetValue(kv.Key, out var identity))
                {
                    _identities.TryGetValue(new SymbolRef(module, display), out identity);
                }

                return new Hotspot(
                    Frame: new SampledFrame(Module: module, Method: display),
                    InclusiveSamples: kv.Value,
                    ExclusiveSamples: _exclusive.GetValueOrDefault(kv.Key),
                    Identity: identity)
                {
                    SelfSamples = new SelfSampleBreakdown(_exclusive.GetValueOrDefault(kv.Key), 0),
                };
            })
            .ToList();

        if (_anyMangledFrameDemangled)
        {
            _symbolSource = NativeAotSymbolDemangler.Combine(
                _symbolSource,
                NativeAotSymbolDemangler.SymbolSource.ElfDemangled);
        }

        IReadOnlyDictionary<SymbolRef, MethodIdentity> identityView = _identities;
        return new PerfScriptAggregationResult(
            Total: TotalSamples,
            Hotspots: hotspots,
            Root: _callTree.Build(),
            SymbolSource: _symbolSource,
            Identities: identityView,
            Truncated: truncated);
    }

    private static string BuildAggregationKey(string module, string display, MethodIdentity? identity)
    {
        var key = string.IsNullOrEmpty(module) ? display : module + "!" + display;
        if (identity is null)
        {
            return key;
        }

        return string.Concat(
            key,
            "\0jit:",
            identity.ModuleVersionId,
            ':',
            identity.MetadataToken?.ToString(CultureInfo.InvariantCulture),
            ':',
            identity.ModulePath,
            ':',
            identity.TypeFullName,
            ':',
            identity.MethodName,
            ':',
            identity.GenericArity.ToString(CultureInfo.InvariantCulture));
    }
}
