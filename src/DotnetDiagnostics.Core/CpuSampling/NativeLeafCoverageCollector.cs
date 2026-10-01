namespace DotnetDiagnostics.Core.CpuSampling;

internal readonly record struct NativeLeafModuleIdentity(
    string Module,
    string? ImagePath,
    ulong ImageBase,
    ulong ImageSize,
    string? PdbName,
    Guid? PdbSignature,
    int? PdbAge,
    NativeSymbolResolverOpenStatus ResolverStatus);

internal sealed class NativeLeafCoverageCollector
{
    internal const int DefaultRetainedPcLimit = 4096;
    internal const int DefaultRetainedModuleLimit = 512;

    private readonly int retainedPcLimit;
    private readonly int retainedModuleLimit;
    private readonly Dictionary<NativeLeafPcKey, MutablePcCoverage> pcs = [];
    private readonly Dictionary<NativeLeafModuleKey, MutableModuleCoverage> modules = [];
    private long totalLeafSamples;
    private long verifiedRangeSamples;
    private long unretainedSampleWeight;
    private long unretainedVerifiedRangeSampleWeight;
    private long unretainedModuleSampleWeight;
    private int unretainedModules;

    public NativeLeafCoverageCollector(
        int retainedPcLimit = DefaultRetainedPcLimit,
        int retainedModuleLimit = DefaultRetainedModuleLimit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retainedPcLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(retainedModuleLimit, 1);
        this.retainedPcLimit = retainedPcLimit;
        this.retainedModuleLimit = retainedModuleLimit;
    }

    public void RegisterModule(NativeLeafModuleIdentity module)
    {
        var moduleKey = Key(module);
        if (modules.ContainsKey(moduleKey))
        {
            return;
        }

        if (modules.Count >= retainedModuleLimit)
        {
            unretainedModules++;
            return;
        }

        modules.Add(moduleKey, new MutableModuleCoverage(module));
    }

    public void Observe(
        NativeLeafModuleIdentity module,
        ulong address,
        NativeSymbolResolution resolution)
    {
        totalLeafSamples++;
        var status = MapStatus(module.ResolverStatus, resolution);
        var verified = status == NativeLeafResolutionStatus.VerifiedContainingRange;
        if (verified)
        {
            verifiedRangeSamples++;
        }

        var moduleKey = Key(module);
        if (!modules.TryGetValue(moduleKey, out var mutableModule))
        {
            if (modules.Count < retainedModuleLimit)
            {
                RegisterModule(module);
            }
            if (!modules.TryGetValue(moduleKey, out mutableModule))
            {
                unretainedSampleWeight++;
                unretainedModuleSampleWeight++;
                if (verified)
                {
                    unretainedVerifiedRangeSampleWeight++;
                }
                return;
            }
        }

        var pcKey = new NativeLeafPcKey(moduleKey, address);
        if (pcs.TryGetValue(pcKey, out var existing))
        {
            existing.Samples++;
            mutableModule.ObserveSample(verified, retained: true);
            return;
        }

        if (pcs.Count >= retainedPcLimit)
        {
            unretainedSampleWeight++;
            if (verified)
            {
                unretainedVerifiedRangeSampleWeight++;
            }

            mutableModule.ObserveSample(verified, retained: false);
            return;
        }

        uint? rva = address >= module.ImageBase && address - module.ImageBase <= uint.MaxValue
            ? (uint)(address - module.ImageBase)
            : null;
        pcs.Add(pcKey, new MutablePcCoverage(
            address,
            rva,
            status,
            verified ? resolution.Name : null));
        mutableModule.ObserveNewPc(verified);
    }

    public NativeLeafCoverage Build()
    {
        var moduleRows = modules
            .Select(pair => pair.Value.Build(
                pcs.Where(pc => pc.Key.Module == pair.Key).Select(pc => pc.Value)))
            .OrderByDescending(module => module.TotalLeafSamples)
            .ThenBy(module => module.Module, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new NativeLeafCoverage(
            retainedPcLimit,
            retainedModuleLimit,
            totalLeafSamples,
            verifiedRangeSamples,
            unretainedSampleWeight,
            unretainedVerifiedRangeSampleWeight,
            unretainedModuleSampleWeight,
            pcs.Count,
            pcs.Values.Count(pc => pc.Resolution == NativeLeafResolutionStatus.VerifiedContainingRange),
            modules.Count,
            unretainedModules,
            moduleRows);
    }

    private static NativeLeafModuleKey Key(NativeLeafModuleIdentity module)
        => new(module.ImageBase, module.ImagePath, module.Module);

    private static NativeLeafResolutionStatus MapStatus(
        NativeSymbolResolverOpenStatus resolverStatus,
        NativeSymbolResolution resolution)
    {
        if (resolution.CanInternName)
        {
            return NativeLeafResolutionStatus.VerifiedContainingRange;
        }

        return resolution.RangeStatus switch
        {
            NativeSymbolRangeStatus.RangeMissing => NativeLeafResolutionStatus.RangeMissing,
            NativeSymbolRangeStatus.OutsideRange => NativeLeafResolutionStatus.OutsideRange,
            NativeSymbolRangeStatus.LookupFailed => NativeLeafResolutionStatus.LookupFailed,
            _ => resolverStatus switch
            {
                NativeSymbolResolverOpenStatus.MissingPdbIdentity => NativeLeafResolutionStatus.MissingPdbIdentity,
                NativeSymbolResolverOpenStatus.SymbolSourceUnavailable => NativeLeafResolutionStatus.SymbolSourceUnavailable,
                NativeSymbolResolverOpenStatus.MatchingPdbUnavailable => NativeLeafResolutionStatus.MatchingPdbUnavailable,
                NativeSymbolResolverOpenStatus.DiaUnavailable => NativeLeafResolutionStatus.DiaUnavailable,
                NativeSymbolResolverOpenStatus.PdbRejected => NativeLeafResolutionStatus.PdbRejected,
                _ => NativeLeafResolutionStatus.Unavailable,
            },
        };
    }

    private readonly record struct NativeLeafModuleKey(
        ulong ImageBase,
        string? ImagePath,
        string Module);

    private readonly record struct NativeLeafPcKey(
        NativeLeafModuleKey Module,
        ulong Address);

    private sealed class MutablePcCoverage(
        ulong address,
        uint? rva,
        NativeLeafResolutionStatus resolution,
        string? functionName)
    {
        public ulong Address { get; } = address;
        public uint? Rva { get; } = rva;
        public NativeLeafResolutionStatus Resolution { get; } = resolution;
        public string? FunctionName { get; } = functionName;
        public long Samples { get; set; } = 1;

        public NativeLeafPcCoverage Build()
            => new(Address, Rva, Samples, Resolution, FunctionName);
    }

    private sealed class MutableModuleCoverage(NativeLeafModuleIdentity identity)
    {
        private long totalLeafSamples;
        private long verifiedRangeSamples;
        private long unretainedSampleWeight;
        private int retainedDistinctPcs;
        private int retainedVerifiedDistinctPcs;

        public void ObserveNewPc(bool verified)
        {
            retainedDistinctPcs++;
            if (verified)
            {
                retainedVerifiedDistinctPcs++;
            }
            ObserveSample(verified, retained: true);
        }

        public void ObserveSample(bool verified, bool retained)
        {
            totalLeafSamples++;
            if (verified)
            {
                verifiedRangeSamples++;
            }
            if (!retained)
            {
                unretainedSampleWeight++;
            }
        }

        public NativeModuleLeafCoverage Build(IEnumerable<MutablePcCoverage> retainedPcs)
            => new(
                identity.Module,
                identity.ImagePath,
                identity.ImageBase,
                identity.ImageSize,
                identity.PdbName,
                identity.PdbSignature,
                identity.PdbAge,
                identity.ResolverStatus.ToString(),
                totalLeafSamples,
                verifiedRangeSamples,
                unretainedSampleWeight,
                retainedDistinctPcs,
                retainedVerifiedDistinctPcs,
                retainedPcs
                    .OrderByDescending(pc => pc.Samples)
                    .ThenBy(pc => pc.Address)
                    .Select(pc => pc.Build())
                    .ToArray());
    }
}
