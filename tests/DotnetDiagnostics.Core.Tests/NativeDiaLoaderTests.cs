using System.Runtime.InteropServices;
using DotnetDiagnostics.Core.CpuSampling;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class NativeDiaLoaderTests
{
    [Fact]
    public void CreateDataSource_UsesShippedLibraryWithoutComRegistration()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = NativeDiaLoader.CreateDataSource();
        try
        {
            Marshal.IsComObject(source).Should().BeTrue();
        }
        finally
        {
            _ = Marshal.FinalReleaseComObject(source);
        }
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("94451369-D782-EA5D-26A5-A3501C131722", -1)]
    public void TryOpenPdb_RejectsMissingIdentityBeforeOpeningFile(string signature, int age)
    {
        var guid = signature.Length == 0 ? Guid.Empty : Guid.Parse(signature);
        EtwPdbSymbolResolver.TryOpenPdb("not-a-pdb", guid, age, out var resolver, out var status)
            .Should().BeFalse();
        resolver.Should().BeNull();
        status.Should().Be(NativeSymbolResolverOpenStatus.MissingPdbIdentity);
    }
}
