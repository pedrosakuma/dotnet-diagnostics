using DotnetDiagnostics.Core.CpuSampling;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class EtwPdbSymbolResolverTests
{
    private static readonly Guid PdbSignature = Guid.Parse("94451369-D782-EA5D-26A5-A3501C131722");

    [Theory]
    [InlineData(0x1000u)]
    [InlineData(0x10FFu)]
    public void FromPdb_AcceptsOnlyAddressesInsideTheHalfOpenFunctionRange(uint addressRva)
    {
        var resolution = NativeSymbolResolution.FromPdb(
            "??0Widget@@QEAA@XZ",
            addressRva,
            0x1000,
            0x100,
            PdbSignature,
            1);

        resolution.RangeStatus.Should().Be(NativeSymbolRangeStatus.InRange);
        resolution.CanInternName.Should().BeTrue();
        resolution.Name.Should().Be("??0Widget@@QEAA@XZ",
            "valid decorated names are not rejected based on their spelling");
        resolution.Provenance.Should().Be(NativeSymbolProvenance.PdbDia);
        resolution.PdbSignature.Should().Be(PdbSignature);
        resolution.PdbAge.Should().Be(1);
    }

    [Theory]
    [InlineData(0x0FFFu)]
    [InlineData(0x1100u)]
    public void FromPdb_DoesNotExposeNamesOutsideTheFunctionRange(uint addressRva)
    {
        var resolution = NativeSymbolResolution.FromPdb(
            "initialize_legacy_wide_specifiers",
            addressRva,
            0x1000,
            0x100,
            PdbSignature,
            1);

        resolution.RangeStatus.Should().Be(NativeSymbolRangeStatus.OutsideRange);
        resolution.CanInternName.Should().BeFalse();
        resolution.Name.Should().BeNull();
        resolution.Provenance.Should().Be(NativeSymbolProvenance.PdbDia);
    }

    [Fact]
    public void FromPdb_ZeroLengthSymbolsRemainUnverified()
    {
        var resolution = NativeSymbolResolution.FromPdb(
            "unknown_length",
            0x1000,
            0x1000,
            0,
            PdbSignature,
            1);

        resolution.RangeStatus.Should().Be(NativeSymbolRangeStatus.RangeMissing);
        resolution.CanInternName.Should().BeFalse();
        resolution.Name.Should().BeNull();
    }

    [Fact]
    public void FromPdb_RejectsMissingPdbIdentity()
    {
        var resolution = NativeSymbolResolution.FromPdb(
            "unverified",
            0x1000,
            0x1000,
            0x100,
            Guid.Empty,
            1);

        resolution.RangeStatus.Should().Be(NativeSymbolRangeStatus.Unavailable);
        resolution.Provenance.Should().Be(NativeSymbolProvenance.None);
        resolution.CanInternName.Should().BeFalse();
    }

    [Fact]
    public void FromPdb_RejectsInvalidPdbAge()
    {
        var resolution = NativeSymbolResolution.FromPdb(
            "unverified",
            0x1000,
            0x1000,
            0x100,
            PdbSignature,
            -1);

        resolution.RangeStatus.Should().Be(NativeSymbolRangeStatus.Unavailable);
        resolution.Provenance.Should().Be(NativeSymbolProvenance.None);
        resolution.CanInternName.Should().BeFalse();
    }
}
