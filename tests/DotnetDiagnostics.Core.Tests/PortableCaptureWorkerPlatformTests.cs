using System.Runtime.InteropServices;
using DotnetDiagnostics.Core.Captures;

namespace DotnetDiagnostics.Core.Tests;

public sealed class PortableCaptureWorkerPlatformTests
{
    [Theory]
    [InlineData(true, Architecture.X64, "linux-x64", "linux-x64")]
    [InlineData(true, Architecture.Arm64, "linux-arm64", "linux-arm64")]
    [InlineData(false, Architecture.X64, "linux-x64", null)]
    [InlineData(false, Architecture.Arm64, "linux-arm64", null)]
    [InlineData(true, Architecture.X64, "linux-musl-x64", null)]
    [InlineData(true, Architecture.Arm64, "linux-musl-arm64", null)]
    [InlineData(true, Architecture.Arm, "linux-arm", null)]
    public void SupportedRuntimeIdentifierIsAnExplicitLinuxGlibcMatrix(bool isLinux,
        Architecture architecture, string runtimeIdentifier, string? expected)
    {
        Assert.Equal(expected, PortableCaptureImportWorker.GetSupportedRuntimeIdentifier(
            isLinux, architecture, runtimeIdentifier));
    }
}
