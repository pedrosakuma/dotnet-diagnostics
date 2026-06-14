using System.IO;
using System.Text;
using DotnetDiagnostics.Core.CpuSampling;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Tests for <see cref="NativeAotMethodMap"/> (issue #395). The fixture mirrors the shape of a
/// real ILC <c>*.map.xml</c> (produced by <c>XmlObjectDumper</c>): one element per object node,
/// tag = node kind, mangled symbol in the <c>Name</c> attribute, plus <c>Length</c> / <c>Hash</c>
/// and crucially no address. Only <c>MethodCode</c> nodes are real managed method bodies.
/// </summary>
public class NativeAotMethodMapTests
{
    private const string SampleMap = """
        <?xml version="1.0" encoding="utf-8"?>
        <ObjectNodes>
          <Metadata Name="__embedded_metadata" Length="100" Hash="aa" />
          <MethodCode Name="NativeAotSample_WeatherForecast__ToString" Length="167" Hash="bb" />
          <GCInfo Name="NativeAotSample_WeatherForecast__ToString" Length="12" Hash="cc" />
          <MethodCode Name="System_Net_Primitives_System_Net_Cookie__SetDomainAndKey" Length="152" Hash="dd" />
          <ReadyToRunHelper Name="__RhpNewFast" Length="32" Hash="ee" />
          <UnboxingStub Name="unbox_NativeAotSample_WeatherForecast__Equals" Length="20" Hash="ff" />
        </ObjectNodes>
        """;

    private static NativeAotMethodMap LoadSample()
        => NativeAotMethodMap.Load(new MemoryStream(Encoding.UTF8.GetBytes(SampleMap)));

    [Fact]
    public void Load_IndexesOnlyManagedMethodCodeNodes()
    {
        var map = LoadSample();

        // Two distinct MethodCode names; GCInfo with the same Name does not double-count.
        map.Count.Should().Be(2);
        map.ContainsMethod("NativeAotSample_WeatherForecast__ToString").Should().BeTrue();
        map.ContainsMethod("System_Net_Primitives_System_Net_Cookie__SetDomainAndKey").Should().BeTrue();
    }

    [Fact]
    public void ContainsMethod_ExcludesHelpersAndStubs()
    {
        var map = LoadSample();

        map.ContainsMethod("__RhpNewFast").Should().BeFalse("ReadyToRunHelper nodes are not managed method bodies");
        map.ContainsMethod("unbox_NativeAotSample_WeatherForecast__Equals").Should().BeFalse("unboxing stubs are not real method bodies");
        map.ContainsMethod("__embedded_metadata").Should().BeFalse();
    }

    [Fact]
    public void ContainsMethod_IsOrdinalAndCaseSensitive()
    {
        var map = LoadSample();

        map.ContainsMethod("nativeaotsample_weatherforecast__tostring").Should().BeFalse();
        map.ContainsMethod(null).Should().BeFalse();
        map.ContainsMethod(string.Empty).Should().BeFalse();
        map.ContainsMethod("not_in_map").Should().BeFalse();
    }

    [Fact]
    public void TryLoad_MissingOrBlankPath_ReturnsNull()
    {
        NativeAotMethodMap.TryLoad(null).Should().BeNull();
        NativeAotMethodMap.TryLoad("   ").Should().BeNull();
        NativeAotMethodMap.TryLoad(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Path.GetRandomFileName())).Should().BeNull();
    }

    [Fact]
    public void TryLoad_ReadsFromDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), "aotmap-" + Path.GetRandomFileName() + ".map.xml");
        File.WriteAllText(path, SampleMap);
        try
        {
            var map = NativeAotMethodMap.TryLoad(path);
            map.Should().NotBeNull();
            map!.ContainsMethod("NativeAotSample_WeatherForecast__ToString").Should().BeTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
