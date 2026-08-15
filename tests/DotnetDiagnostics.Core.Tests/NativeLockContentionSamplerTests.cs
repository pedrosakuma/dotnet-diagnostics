using DotnetDiagnostics.Core.NativeAlloc;
using DotnetDiagnostics.Core.NativeLockContention;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class PerfNativeLockContentionSamplerUnitTests
{
    [Fact]
    public void IsAvailable_ReturnsFalse_OnNonLinux()
    {
        if (OperatingSystem.IsLinux()) return; // Linux availability depends on a perf binary being present.
        new PerfNativeLockContentionSampler().IsAvailable().Should().BeFalse();
    }

    [Fact]
    public async Task SampleAsync_Throws_WhenDurationOutOfRange()
    {
        var sampler = new PerfNativeLockContentionSampler();

        var tooShort = async () => await sampler.SampleAsync(4242, TimeSpan.Zero);
        await tooShort.Should().ThrowAsync<ArgumentOutOfRangeException>();

        var tooLong = async () => await sampler.SampleAsync(4242, TimeSpan.FromMinutes(6));
        await tooLong.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task SampleAsync_Throws_WhenTopNNotPositive()
    {
        var sampler = new PerfNativeLockContentionSampler();
        var act = async () => await sampler.SampleAsync(4242, TimeSpan.FromSeconds(1), topN: 0);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task SampleAsync_Throws_WhenSamplePeriodNotPositive()
    {
        var sampler = new PerfNativeLockContentionSampler();
        var act = async () => await sampler.SampleAsync(4242, TimeSpan.FromSeconds(1), samplePeriod: 0);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task SampleAsync_Throws_PlatformNotSupported_OnNonLinux()
    {
        if (OperatingSystem.IsLinux()) return;
        var sampler = new PerfNativeLockContentionSampler();
        var act = async () => await sampler.SampleAsync(4242, TimeSpan.FromSeconds(1));
        (await act.Should().ThrowAsync<PlatformNotSupportedException>()).Which.Message
            .Should().Contain("pthread_mutex_lock");
    }

    [Fact]
    public void BuildEventName_ReusesTheSharedNativeAllocHelper_ForMutexFunctionNames()
    {
        // The Linux sampler reuses PerfNativeAllocSampler.BuildEventName/ParseCreatedTracepoint
        // rather than duplicating the perf-probe plumbing (both live in the same assembly and the
        // helpers have no allocation-specific semantics — they just build/parse an identifier).
        var a = PerfNativeAllocSampler.BuildEventName("pthread_mutex_lock", "4242_abcd");
        a.Should().Be("diagmcp_pthread_mutex_lock_4242_abcd");
        a.Should().MatchRegex("^[A-Za-z_][A-Za-z0-9_]*$");
    }
}

public sealed class WindowsNativeLockContentionSamplerTests
{
    [Fact]
    public void IsAvailable_AlwaysReturnsFalse()
    {
        new WindowsNativeLockContentionSampler().IsAvailable().Should().BeFalse();
    }

    [Fact]
    public async Task SampleAsync_AlwaysThrows_PlatformNotSupported_WithInvestigationFindings()
    {
        var sampler = new WindowsNativeLockContentionSampler();
        var act = async () => await sampler.SampleAsync(4242, TimeSpan.FromSeconds(1));

        var exception = await act.Should().ThrowAsync<PlatformNotSupportedException>();
        if (OperatingSystem.IsWindows())
        {
            exception.Which.Message.Should().Contain("CritSecTraceProvider");
        }
    }
}

public sealed class RoutingNativeLockContentionSamplerTests
{
    [Fact]
    public void IsAvailable_ReturnsFalse_OnNonLinuxNonWindows()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows()) return;
        var routing = new RoutingNativeLockContentionSampler(
            new PerfNativeLockContentionSampler(),
            new WindowsNativeLockContentionSampler());
        routing.IsAvailable().Should().BeFalse();
    }

    [Fact]
    public async Task SampleAsync_Throws_OnWindows_WithNoBackendMessage()
    {
        if (!OperatingSystem.IsWindows()) return;
        var routing = new RoutingNativeLockContentionSampler(
            new PerfNativeLockContentionSampler(),
            new WindowsNativeLockContentionSampler());
        var act = async () => await routing.SampleAsync(4242, TimeSpan.FromSeconds(1));
        await act.Should().ThrowAsync<PlatformNotSupportedException>();
    }
}
