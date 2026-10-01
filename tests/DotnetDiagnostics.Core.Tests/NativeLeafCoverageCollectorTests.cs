using DotnetDiagnostics.Core.CpuSampling;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class NativeLeafCoverageCollectorTests
{
    private static readonly Guid PdbSignature = Guid.Parse("94451369-D782-EA5D-26A5-A3501C131722");

    [Fact]
    public void Build_ReportsCapturedIdentityAndVerifiedRangeCoverage()
    {
        var collector = new NativeLeafCoverageCollector(retainedPcLimit: 4);
        var module = Module(NativeSymbolResolverOpenStatus.Ready);

        collector.Observe(module, 0x1800_0100, NativeSymbolResolution.FromPdb(
            "icu_function", 0x100, 0x80, 0x100, PdbSignature, 1));
        collector.Observe(module, 0x1800_0100, NativeSymbolResolution.FromPdb(
            "icu_function", 0x100, 0x80, 0x100, PdbSignature, 1));
        collector.Observe(module, 0x1800_0200, NativeSymbolResolution.FromPdb(
            "next_function", 0x200, 0x200, 0, PdbSignature, 1));

        var result = collector.Build();

        result.TotalLeafSamples.Should().Be(3);
        result.VerifiedRangeSamples.Should().Be(2);
        result.RetainedDistinctPcs.Should().Be(2);
        result.RetainedVerifiedDistinctPcs.Should().Be(1);
        result.UnretainedSampleWeight.Should().Be(0);
        result.RetainedModules.Should().Be(1);
        result.UnretainedModules.Should().Be(0);

        var icu = result.Modules.Should().ContainSingle().Subject;
        icu.Module.Should().Be("icu.dll");
        icu.ImagePath.Should().Be(@"C:\Windows\System32\icu.dll");
        icu.ImageBase.Should().Be(0x1800_0000);
        icu.ImageSize.Should().Be(0x2A0000);
        icu.PdbName.Should().Be("icu.pdb");
        icu.PdbSignature.Should().Be(PdbSignature);
        icu.PdbAge.Should().Be(1);
        icu.ResolverStatus.Should().Be(nameof(NativeSymbolResolverOpenStatus.Ready));
        icu.TotalLeafSamples.Should().Be(3);
        icu.VerifiedRangeSamples.Should().Be(2);
        icu.RetainedPcs.Should().ContainSingle(pc =>
            pc.Address == 0x1800_0100
            && pc.Rva == 0x100
            && pc.Samples == 2
            && pc.Resolution == NativeLeafResolutionStatus.VerifiedContainingRange
            && pc.FunctionName == "icu_function");
        icu.RetainedPcs.Should().ContainSingle(pc =>
            pc.Address == 0x1800_0200
            && pc.Resolution == NativeLeafResolutionStatus.RangeMissing
            && pc.FunctionName == null);
    }

    [Fact]
    public void Build_MapsUnavailableResolutionToModuleOpenFailure()
    {
        var collector = new NativeLeafCoverageCollector();
        collector.Observe(
            Module(NativeSymbolResolverOpenStatus.MatchingPdbUnavailable),
            0x1800_0100,
            NativeSymbolResolution.Unavailable(NativeSymbolRangeStatus.Unavailable));

        var pc = collector.Build().Modules.Single().RetainedPcs.Single();

        pc.Resolution.Should().Be(NativeLeafResolutionStatus.MatchingPdbUnavailable);
        pc.FunctionName.Should().BeNull();
    }

    [Fact]
    public void Build_DropsNewPcDetailAtInsertionAndPreservesWeights()
    {
        var collector = new NativeLeafCoverageCollector(retainedPcLimit: 1);
        var module = Module(NativeSymbolResolverOpenStatus.Ready);
        var verified = NativeSymbolResolution.FromPdb(
            "icu_function", 0x100, 0x80, 0x100, PdbSignature, 1);

        collector.Observe(module, 0x1800_0100, verified);
        collector.Observe(module, 0x1800_0100, verified);
        collector.Observe(module, 0x1800_0200, verified with
        {
            FunctionStartRva = 0x200,
        });
        collector.Observe(module, 0x1800_0200, verified with
        {
            FunctionStartRva = 0x200,
        });

        var result = collector.Build();

        result.TotalLeafSamples.Should().Be(4);
        result.VerifiedRangeSamples.Should().Be(4);
        result.RetainedDistinctPcs.Should().Be(1);
        result.RetainedVerifiedDistinctPcs.Should().Be(1);
        result.UnretainedSampleWeight.Should().Be(2);
        result.UnretainedVerifiedRangeSampleWeight.Should().Be(2);
        result.Modules.Single().TotalLeafSamples.Should().Be(4);
        result.Modules.Single().UnretainedSampleWeight.Should().Be(2);
        result.Modules.Single().RetainedPcs.Single().Samples.Should().Be(2);
    }

    [Fact]
    public void RegisterModule_RetainsZeroSampleIdentityAndBoundsLaterModules()
    {
        var collector = new NativeLeafCoverageCollector(retainedPcLimit: 4, retainedModuleLimit: 1);
        collector.RegisterModule(Module(NativeSymbolResolverOpenStatus.Ready));
        collector.RegisterModule(Module(NativeSymbolResolverOpenStatus.Ready) with
        {
            Module = "other.dll",
            ImagePath = @"C:\other.dll",
            ImageBase = 0x1900_0000,
        });

        var result = collector.Build();

        result.Modules.Should().ContainSingle(module =>
            module.Module == "icu.dll" && module.TotalLeafSamples == 0);
        result.RetainedModules.Should().Be(1);
        result.UnretainedModules.Should().Be(1);
    }

    [Fact]
    public void Observe_DoesNotInflateDroppedModuleCountForRepeatedSamples()
    {
        var collector = new NativeLeafCoverageCollector(retainedPcLimit: 4, retainedModuleLimit: 1);
        var retained = Module(NativeSymbolResolverOpenStatus.Ready);
        var dropped = retained with
        {
            Module = "other.dll",
            ImagePath = @"C:\other.dll",
            ImageBase = 0x1900_0000,
        };
        collector.RegisterModule(retained);
        collector.RegisterModule(dropped);

        collector.Observe(
            dropped,
            0x1900_0100,
            NativeSymbolResolution.Unavailable(NativeSymbolRangeStatus.Unavailable));
        collector.Observe(
            dropped,
            0x1900_0200,
            NativeSymbolResolution.Unavailable(NativeSymbolRangeStatus.Unavailable));

        var result = collector.Build();

        result.UnretainedModules.Should().Be(1);
        result.UnretainedModuleSampleWeight.Should().Be(2);
        result.UnretainedSampleWeight.Should().Be(2);
    }

    private static NativeLeafModuleIdentity Module(NativeSymbolResolverOpenStatus status)
        => new(
            "icu.dll",
            @"C:\Windows\System32\icu.dll",
            0x1800_0000,
            0x2A0000,
            "icu.pdb",
            PdbSignature,
            1,
            status);
}
