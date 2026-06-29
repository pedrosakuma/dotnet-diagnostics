using System.Runtime.InteropServices;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.NativeAlloc;
using FluentAssertions;
using Microsoft.Diagnostics.Tracing.Session;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Tests for the Windows ETW native-allocation sampler (issue #466). The event→envelope
/// aggregation is exercised OS-agnostically through <see cref="NativeAllocStackAggregator"/> with
/// synthetic stacks (a live ETW VirtualAlloc capture only runs on elevated Windows and is guarded
/// accordingly); the sampler's platform gating and argument validation are checked directly.
/// </summary>
public sealed class NativeAllocStackAggregatorTests
{
    private static IReadOnlyList<(string Module, string Method)> Stack(params string[] leafToRootMethods)
        => leafToRootMethods.Select(m => ("libc", m)).ToList();

    [Fact]
    public void Aggregate_CountsOneAllocationPerStack_AndBuildsRootToLeafTree()
    {
        // Two identical malloc stacks: main → DoWork → malloc.
        var stacks = new[]
        {
            Stack("malloc", "DoWork", "Main"),
            Stack("malloc", "DoWork", "Main"),
        };

        var result = NativeAllocStackAggregator.Aggregate(stacks, topN: 25);

        result.TotalSampledAllocations.Should().Be(2);

        // The tree is root→leaf: <root> → Main → DoWork → malloc.
        result.Root.Frame.Method.Should().Be("<root>");
        result.Root.InclusiveSamples.Should().Be(2);
        var main = result.Root.Children.Single();
        main.Frame.Method.Should().Be("Main");
        main.InclusiveSamples.Should().Be(2);
        var doWork = main.Children.Single();
        doWork.Frame.Method.Should().Be("DoWork");
        var malloc = doWork.Children.Single();
        malloc.Frame.Method.Should().Be("malloc");
        malloc.InclusiveSamples.Should().Be(2);
        malloc.ExclusiveSamples.Should().Be(2, "malloc is the leaf on every stack");
    }

    [Fact]
    public void Aggregate_RanksHotspotsByInclusiveSamples_AndCapsToTopN()
    {
        var stacks = new[]
        {
            Stack("malloc", "Hot", "Main"),
            Stack("malloc", "Hot", "Main"),
            Stack("malloc", "Hot", "Main"),
            Stack("calloc", "Cold", "Main"),
        };

        var result = NativeAllocStackAggregator.Aggregate(stacks, topN: 2);

        result.TotalSampledAllocations.Should().Be(4);
        result.Hotspots.Should().HaveCount(2);
        // Main appears on all 4 stacks → highest inclusive count, ranked first.
        result.Hotspots[0].Frame.Method.Should().Be("Main");
        result.Hotspots[0].InclusiveSamples.Should().Be(4);
    }

    [Fact]
    public void Aggregate_DeduplicatesInclusiveCountWithinARecursiveStack()
    {
        // recurse appears twice in the same stack — inclusive must count the stack once, not twice.
        var stacks = new[] { Stack("malloc", "recurse", "recurse", "Main") };

        var result = NativeAllocStackAggregator.Aggregate(stacks, topN: 25);

        var recurse = result.Hotspots.Single(h => h.Frame.Method == "recurse");
        recurse.InclusiveSamples.Should().Be(1);
    }

    [Fact]
    public void Aggregate_EmptyOrNullStacks_AreIgnored()
    {
        var stacks = new IReadOnlyList<(string, string)>[]
        {
            Array.Empty<(string, string)>(),
            null!,
            Stack("malloc", "Main"),
        };

        var result = NativeAllocStackAggregator.Aggregate(stacks, topN: 25);

        result.TotalSampledAllocations.Should().Be(1);
    }

    [Fact]
    public void Aggregate_NoStacks_ReportsUnknownSymbolSource()
    {
        var result = NativeAllocStackAggregator.Aggregate(Array.Empty<IReadOnlyList<(string, string)>>(), topN: 25);

        result.TotalSampledAllocations.Should().Be(0);
        result.Hotspots.Should().BeEmpty();
        result.SymbolSource.Should().Be(NativeAotSymbolDemangler.SymbolSource.Unknown);
    }

    [Fact]
    public void Aggregate_ResolvedMethodNames_ReportPdbResolved()
    {
        var result = NativeAllocStackAggregator.Aggregate(new[] { Stack("malloc", "Main") }, topN: 25);

        result.SymbolSource.Should().Be(NativeAotSymbolDemangler.SymbolSource.PdbResolved);
    }

    [Fact]
    public void Aggregate_OnlyRawAddressFrames_ReportStripped()
    {
        var stacks = new[]
        {
            new List<(string Module, string Method)> { ("ntdll", "0x7ff1234"), ("ntdll", "0x7ff5678") },
        };

        var result = NativeAllocStackAggregator.Aggregate(stacks, topN: 25);

        result.SymbolSource.Should().Be(NativeAotSymbolDemangler.SymbolSource.Stripped);
    }

    [Fact]
    public void Aggregate_KeysFramesByModule_SoSameMethodInDifferentModulesDoesNotCollide()
    {
        var stacks = new[]
        {
            new List<(string Module, string Method)> { ("a.dll", "Alloc"), ("a.dll", "Main") },
            new List<(string Module, string Method)> { ("b.dll", "Alloc"), ("b.dll", "Main") },
        };

        var result = NativeAllocStackAggregator.Aggregate(stacks, topN: 25);

        result.Hotspots.Where(h => h.Frame.Method == "Alloc").Should().HaveCount(2,
            "Alloc in a.dll and Alloc in b.dll are distinct hotspots");
    }

    [Fact]
    public void Aggregate_RejectsNonPositiveTopN()
    {
        var act = () => NativeAllocStackAggregator.Aggregate(Array.Empty<IReadOnlyList<(string, string)>>(), topN: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

public sealed class EtwNativeAllocSamplerUnitTests
{
    [Fact]
    public void IsAvailable_ReturnsFalse_OnNonWindows()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        new EtwNativeAllocSampler().IsAvailable().Should().BeFalse("ETW is a Windows-only technology");
    }

    [Fact]
    public void IsAvailable_WhenElevated_ReturnsTrue_OnWindows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        // Availability is admin OR SeSystemProfilePrivilege; elevation is a sufficient condition.
        if (TraceEventSession.IsElevated() == true)
        {
            new EtwNativeAllocSampler().IsAvailable().Should().BeTrue();
        }
    }

    [Fact]
    public async Task SampleAsync_RejectsOutOfRangeArguments()
    {
        var sampler = new EtwNativeAllocSampler();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => sampler.SampleAsync(1, TimeSpan.Zero));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => sampler.SampleAsync(1, TimeSpan.FromMinutes(6)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => sampler.SampleAsync(1, TimeSpan.FromSeconds(1), topN: 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => sampler.SampleAsync(1, TimeSpan.FromSeconds(1), samplePeriod: 0));
    }

    [Fact]
    public async Task SampleAsync_OnNonWindows_ThrowsPlatformNotSupported()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var sampler = new EtwNativeAllocSampler();
        var act = async () => await sampler.SampleAsync(1, TimeSpan.FromSeconds(1));
        var ex = await act.Should().ThrowAsync<PlatformNotSupportedException>();
        ex.Which.Message.Should().Contain("Windows");
    }

    [Fact]
    public async Task SampleAsync_WhenNotAvailable_ThrowsUnauthorized_OnWindows()
    {
        // Only meaningful on a non-elevated Windows host.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || TraceEventSession.IsElevated() == true) return;

        var sampler = new EtwNativeAllocSampler();
        var act = async () => await sampler.SampleAsync(1, TimeSpan.FromSeconds(1));
        var ex = await act.Should().ThrowAsync<UnauthorizedAccessException>();
        ex.Which.Message.Should().ContainAny("Administrators", "SeSystemProfilePrivilege");
    }
}

public sealed class RoutingNativeAllocSamplerTests
{
    private static RoutingNativeAllocSampler NewRouter()
        => new(new PerfNativeAllocSampler(), new EtwNativeAllocSampler());

    [Fact]
    public void IsAvailable_DelegatesToTheHostBackend()
    {
        var router = NewRouter();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            router.IsAvailable().Should().Be(new PerfNativeAllocSampler().IsAvailable());
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            router.IsAvailable().Should().Be(new EtwNativeAllocSampler().IsAvailable());
        }
        else
        {
            router.IsAvailable().Should().BeFalse();
        }
    }

    [Fact]
    public async Task SampleAsync_OnLinux_RoutesToPerfBackend()
    {
        if (!OperatingSystem.IsLinux()) return;

        // The perf backend rejects a zero duration before doing any capture — proves we routed to it.
        var router = NewRouter();
        var act = async () => await router.SampleAsync(1, TimeSpan.Zero);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task SampleAsync_OnUnsupportedOs_ThrowsNotSupported()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows()) return;

        var router = NewRouter();
        var act = async () => await router.SampleAsync(1, TimeSpan.FromSeconds(1));
        await act.Should().ThrowAsync<NotSupportedException>();
    }
}
