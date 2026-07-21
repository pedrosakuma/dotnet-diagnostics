using DotnetDiagnostics.Core.Memory;

namespace DotnetDiagnostics.Core.CpuSampling;

internal readonly record struct PerfScriptAggregationResult(
    long Total,
    IReadOnlyList<Hotspot> Hotspots,
    CallTreeNode Root,
    NativeAotSymbolDemangler.SymbolSource SymbolSource,
    IReadOnlyDictionary<SymbolRef, MethodIdentity> Identities,
    bool Truncated = false);

internal sealed class PerfScriptAggregationBuilder
{
    private readonly Dictionary<string, long> _inclusive = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _exclusive = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _modules = new(StringComparer.Ordinal);
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

        var rootToLeaf = new List<(string Key, string Module, string Display)>(sample.Frames.Count);
        for (var i = sample.Frames.Count - 1; i >= 0; i--)
        {
            var frame = sample.Frames[i];
            var classification = NativeAotSymbolDemangler.Classify(frame.Symbol);
            _symbolSource = NativeAotSymbolDemangler.Combine(_symbolSource, classification);
            if (!_displayCache.TryGetValue(frame.Symbol, out var demangled))
            {
                demangled = NativeAotSymbolDemangler.Demangle(frame.Symbol);
                _displayCache[frame.Symbol] = demangled;
                if (classification == NativeAotSymbolDemangler.SymbolSource.ElfMangled &&
                    !ReferenceEquals(demangled, frame.Symbol) &&
                    !string.Equals(demangled, frame.Symbol, StringComparison.Ordinal))
                {
                    _anyMangledFrameDemangled = true;
                }
            }

            var key = string.IsNullOrEmpty(frame.Module) ? demangled : frame.Module + "!" + demangled;
            rootToLeaf.Add((key, frame.Module, demangled));
            _modules.TryAdd(key, frame.Module);

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
        foreach (var (key, _, _) in rootToLeaf)
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
                var display = !string.IsNullOrEmpty(module) && kv.Key.StartsWith(module + "!", StringComparison.Ordinal)
                    ? kv.Key[(module.Length + 1)..]
                    : kv.Key;
                _identities.TryGetValue(new SymbolRef(module, display), out var identity);
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
}
