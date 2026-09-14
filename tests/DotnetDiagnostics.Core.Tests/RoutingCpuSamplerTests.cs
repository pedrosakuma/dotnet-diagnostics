using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.CpuSampling;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class RoutingCpuSamplerTests
{
    [Theory]
    [InlineData(RuntimeFlavor.CoreClr, CpuSamplingMode.Automatic, "EventPipe")]
    [InlineData(RuntimeFlavor.Unknown, CpuSamplingMode.Automatic, "EventPipe")]
    [InlineData(RuntimeFlavor.NativeAot, CpuSamplingMode.Automatic, "Os")]
    [InlineData(RuntimeFlavor.CoreClr, CpuSamplingMode.EventPipe, "EventPipe")]
    [InlineData(RuntimeFlavor.CoreClr, CpuSamplingMode.Os, "Os")]
    [InlineData(RuntimeFlavor.NativeAot, CpuSamplingMode.Os, "Os")]
    public void SelectRoute_IsDeterministic(
        RuntimeFlavor runtime,
        CpuSamplingMode mode,
        string expected)
    {
        RoutingCpuSampler.SelectRoute(runtime, mode).ToString().Should().Be(expected);
    }

    [Fact]
    public void SelectRoute_EventPipeForNativeAot_PreservesUnsupportedRuntimeFailure()
    {
        var act = () => RoutingCpuSampler.SelectRoute(
            RuntimeFlavor.NativeAot,
            CpuSamplingMode.EventPipe);

        var exception = act.Should().Throw<CpuSamplingUnavailableException>().Which;
        exception.ErrorKind.Should().Be("UnsupportedRuntime");
        exception.Message.Should().Contain("NativeAOT");
    }
}
