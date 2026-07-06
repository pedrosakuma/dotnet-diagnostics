using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.MethodParameters;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Security;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class MethodParameterCaptureCollectorTests
{
    [Fact]
    public async Task CollectAsync_RejectsPreDotNet8Targets()
    {
        var collector = new MethodParameterCaptureCollector(new MvidReader(), new SensitiveDataRedactor(new SecurityOptions()));

        var result = await collector.CollectAsync(
            4242,
            new MethodParameterCaptureRequest(
                [new MethodFilter("CoreClrSample.dll", "Program", "BurnCpu")],
                TimeSpan.FromSeconds(2),
                10,
                5,
                "7.0.15",
                new ProcessContext(4242, RuntimeFlavor.CoreClr, true, true, false, "7.0.15")),
            CancellationToken.None);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("NotSupported");
        result.Error.Message.Should().Contain(".NET 8+");
    }

    [Fact]
    public async Task CollectAsync_RejectsNativeAotTargets()
    {
        var collector = new MethodParameterCaptureCollector(new MvidReader(), new SensitiveDataRedactor(new SecurityOptions()));

        var result = await collector.CollectAsync(
            4242,
            new MethodParameterCaptureRequest(
                [new MethodFilter("NativeAotSample.dll", "Program", "Main")],
                TimeSpan.FromSeconds(2),
                10,
                5,
                "10.0.0",
                new ProcessContext(4242, RuntimeFlavor.NativeAot, true, false, false, "10.0.0")),
            CancellationToken.None);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("NotSupported");
        result.Error.Message.Should().Contain("NativeAOT");
    }
}
