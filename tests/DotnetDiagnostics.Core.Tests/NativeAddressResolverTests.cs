using DotnetDiagnostics.Core.Memory;
using DotnetDiagnostics.Core.Symbols;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class NativeAddressResolverTests
{
    private static NativeModuleRange Mod(ulong @base, ulong size, string file, string? buildId = null, bool managed = false)
        => new(@base, size, file, buildId, managed);

    [Fact]
    public void Build_DropsZeroSizedAndOverflowingModules()
    {
        var map = NativeModuleMap.Build(new[]
        {
            Mod(0x1000, 0, "zero.so"),
            Mod(ulong.MaxValue - 0x10, 0x100, "overflow.so"),
            Mod(0x2000, 0x1000, "ok.so"),
        });

        map.Count.Should().Be(1);
        map.TryResolve(0x2500, out var module, out var rva).Should().BeTrue();
        module.FileName.Should().Be("ok.so");
        rva.Should().Be(0x500);
    }

    [Fact]
    public void TryResolve_AddressInsideModule_ReturnsRva()
    {
        var map = NativeModuleMap.Build(new[]
        {
            Mod(0x7f18cca00000, 0x200000, "/usr/lib/libcrypto.so.3", "abc123"),
        });

        map.TryResolve(0x7f18cca1edc0, out var module, out var rva).Should().BeTrue();
        module.FileName.Should().Be("/usr/lib/libcrypto.so.3");
        rva.Should().Be(0x1edc0);
    }

    [Fact]
    public void TryResolve_AddressInGap_ReturnsFalse()
    {
        var map = NativeModuleMap.Build(new[]
        {
            Mod(0x1000, 0x1000, "a.so"),
            Mod(0x5000, 0x1000, "b.so"),
        });

        // 0x3000 is between the two modules.
        map.TryResolve(0x3000, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryResolve_Overlap_PicksSmallestContainingRange()
    {
        var map = NativeModuleMap.Build(new[]
        {
            Mod(0x1000, 0x4000, "big.so"),
            Mod(0x2000, 0x100, "small.so"),
        });

        map.TryResolve(0x2050, out var module, out var rva).Should().BeTrue();
        module.FileName.Should().Be("small.so");
        rva.Should().Be(0x50);
    }

    [Fact]
    public void Resolve_ModuleHit_ClassifiesAsModuleWithBuildId()
    {
        var map = NativeModuleMap.Build(new[] { Mod(0x400000, 0x10000, "/app/libcrypto.so.3", "deadbeef") });

        var loc = NativeAddressClassifier.Resolve(0x401234, map);

        loc.Kind.Should().Be(NativeAddressKind.Module);
        loc.Module.Should().Be("libcrypto.so.3");
        loc.ModulePath.Should().Be("/app/libcrypto.so.3");
        loc.Rva.Should().Be(0x1234);
        loc.LoadBase.Should().Be(0x400000);
        loc.LoadBase!.Value.Should().Be(loc.Address - loc.Rva!.Value);
        loc.BuildId.Should().Be("deadbeef");
        loc.Readable.Should().BeTrue();
        loc.Display.Should().Be("libcrypto.so.3+0x1234");
    }

    [Fact]
    public void Resolve_NoModule_NotReadable_ClassifiesUnmapped()
    {
        var map = NativeModuleMap.Build(new[] { Mod(0x400000, 0x1000, "a.so") });

        var loc = NativeAddressClassifier.Resolve(
            0x7f18cc41edc0,
            map,
            resolveManaged: null,
            probeReadable: _ => false);

        loc.Kind.Should().Be(NativeAddressKind.UnmappedOrNotCaptured);
        loc.Module.Should().BeNull();
        loc.Rva.Should().BeNull();
        loc.LoadBase.Should().BeNull();
        loc.Readable.Should().BeFalse();
        loc.Display.Should().Be("<unmapped-or-not-captured 0x7f18cc41edc0>");
    }

    [Fact]
    public void Resolve_NoModule_Readable_ClassifiesMappedNonModule()
    {
        var map = NativeModuleMap.Build(new[] { Mod(0x400000, 0x1000, "a.so") });

        var loc = NativeAddressClassifier.Resolve(0x9000000, map, probeReadable: _ => true);

        loc.Kind.Should().Be(NativeAddressKind.MappedNonModule);
        loc.Readable.Should().BeTrue();
        loc.Display.Should().StartWith("<mapped-non-module 0x");
    }

    [Fact]
    public void Resolve_NoModule_NoProbe_ConservativelyUnmapped()
    {
        var map = NativeModuleMap.Build(new[] { Mod(0x400000, 0x1000, "a.so") });

        var loc = NativeAddressClassifier.Resolve(0x9000000, map);

        loc.Kind.Should().Be(NativeAddressKind.UnmappedOrNotCaptured);
        loc.Readable.Should().BeNull();
    }

    [Fact]
    public void Resolve_ManagedMethodInsideManagedModule_SurfacesBothDimensions()
    {
        var map = NativeModuleMap.Build(new[] { Mod(0x400000, 0x10000, "MyApp.dll", "r2rbuild", managed: true) });
        var method = new MethodIdentity("Handle", 0, ModuleName: "MyApp.dll", TypeFullName: "MyApp.Handler");

        var loc = NativeAddressClassifier.Resolve(0x405000, map, resolveManaged: _ => method);

        loc.Kind.Should().Be(NativeAddressKind.Managed);
        loc.ManagedMethod.Should().Be(method);
        loc.Rva.Should().Be(0x5000);
        loc.LoadBase.Should().Be(0x400000);
        loc.BuildId.Should().Be("r2rbuild");
        loc.Display.Should().Contain("MyApp.Handler.Handle");
        loc.Display.Should().Contain("MyApp.dll+0x5000");
    }

    [Fact]
    public void Resolve_ManagedMethodNoModule_StillClassifiesManaged()
    {
        var map = NativeModuleMap.Build(Array.Empty<NativeModuleRange>());
        var method = new MethodIdentity("Run", 0, TypeFullName: "App.Worker");

        var loc = NativeAddressClassifier.Resolve(0x123456, map, resolveManaged: _ => method);

        loc.Kind.Should().Be(NativeAddressKind.Managed);
        loc.Display.Should().Contain("App.Worker.Run");
    }

    [Fact]
    public void TryResolve_LargeContainingModuleManyEntriesBefore_IsStillFound()
    {
        var modules = new List<NativeModuleRange>
        {
            // A large outer module at the very start that contains the target address.
            Mod(0x1000, 0x1000000, "big.so"),
        };
        // 200 small, non-containing modules whose bases all sit below the target address,
        // so the binary-search start lands far to the right of big.so (>64 entries away).
        for (ulong i = 0; i < 200; i++)
        {
            var b = 0x2000 + (i * 0x100);
            modules.Add(Mod(b, 0x10, $"small{i}.so"));
        }

        var map = NativeModuleMap.Build(modules);

        // Target sits inside big.so but >64 module records before the binary-search start.
        map.TryResolve(0x100000, out var module, out var rva).Should().BeTrue();
        module.FileName.Should().Be("big.so");
        rva.Should().Be(0xff000);
    }

    [Fact]
    public void Resolve_AslrBasedPieModule_LoadBaseRebasesAbsoluteAddress()
    {
        // PIE / NativeAOT: on-disk image base is 0; the loader places it at a random ASLR base.
        // The consumer must rebase the absolute IP, so LoadBase has to be the runtime base.
        const ulong aslrBase = 0x55a3c0d00000;
        var map = NativeModuleMap.Build(new[] { Mod(aslrBase, 0x80000, "/app/MyAotApp", "gnubuildid") });

        var loc = NativeAddressClassifier.Resolve(aslrBase + 0x2a3f0, map);

        loc.Kind.Should().Be(NativeAddressKind.Module);
        loc.Rva.Should().Be(0x2a3f0);
        loc.LoadBase.Should().Be(aslrBase);
        // Consumer contract: Address - LoadBase == Rva, so rebasing recovers the module-relative offset.
        (loc.Address - loc.LoadBase!.Value).Should().Be(loc.Rva!.Value);
    }

    [Fact]
    public void Resolve_MappedNonModule_HasNoLoadBase()
    {
        var map = NativeModuleMap.Build(new[] { Mod(0x400000, 0x1000, "a.so") });

        var loc = NativeAddressClassifier.Resolve(0x9000000, map, probeReadable: _ => true);

        loc.Kind.Should().Be(NativeAddressKind.MappedNonModule);
        loc.LoadBase.Should().BeNull();
    }

    [Theory]
    [InlineData("0x7f18cc41edc0", true, 0x7f18cc41edc0UL)]
    [InlineData("0X1000", true, 0x1000UL)]
    [InlineData("4096", true, 4096UL)]
    [InlineData("", false, 0UL)]
    [InlineData("zzz", false, 0UL)]
    public void TryParseAddress_ParsesHexAndDecimal(string input, bool expected, ulong value)
    {
        NativeAddressClassifier.TryParseAddress(input, out var addr).Should().Be(expected);
        if (expected)
        {
            addr.Should().Be(value);
        }
    }
}
