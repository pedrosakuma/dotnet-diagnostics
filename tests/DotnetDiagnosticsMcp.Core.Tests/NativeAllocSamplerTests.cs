using DotnetDiagnosticsMcp.Core.NativeAlloc;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Core.Tests;

public sealed class ProcMapsLibcResolverTests
{
    private const int Pid = 4242;

    [Fact]
    public void Parse_PrefersExecutableGlibcMapping_AndRerootsThroughProcRoot()
    {
        var maps = string.Join('\n',
            "7f0000000000-7f0000021000 r--p 00000000 08:01 100 /usr/lib/x86_64-linux-gnu/libc.so.6",
            "7f0000021000-7f00001a0000 r-xp 00021000 08:01 100 /usr/lib/x86_64-linux-gnu/libc.so.6",
            "7f00001a0000-7f00001ef000 r--p 001a0000 08:01 100 /usr/lib/x86_64-linux-gnu/libc.so.6");

        var result = ProcMapsLibcResolver.Parse(maps, Pid);

        result.Should().NotBeNull();
        result!.InNamespacePath.Should().Be("/usr/lib/x86_64-linux-gnu/libc.so.6");
        result.HostPath.Should().Be($"/proc/{Pid}/root/usr/lib/x86_64-linux-gnu/libc.so.6");
    }

    [Fact]
    public void Parse_FallsBackToNonExecutableMapping_WhenNoExecutableLine()
    {
        var maps = "7f0000000000-7f0000021000 r--p 00000000 08:01 100 /lib/libc-2.31.so";

        var result = ProcMapsLibcResolver.Parse(maps, Pid);

        result.Should().NotBeNull();
        result!.InNamespacePath.Should().Be("/lib/libc-2.31.so");
    }

    [Fact]
    public void Parse_MatchesMuslLibc()
    {
        var maps = "7f0000000000-7f0000021000 r-xp 00000000 08:01 100 /lib/ld-musl-x86_64.so.1";

        var result = ProcMapsLibcResolver.Parse(maps, Pid);

        result.Should().NotBeNull();
        result!.InNamespacePath.Should().Be("/lib/ld-musl-x86_64.so.1");
    }

    [Fact]
    public void Parse_IgnoresUnrelatedLibrariesAndAnonymousMappings()
    {
        var maps = string.Join('\n',
            "55a000000000-55a000001000 r-xp 00000000 08:01 1 /usr/bin/dotnet",
            "7f0000000000-7f0000021000 r-xp 00000000 08:01 2 /usr/lib/x86_64-linux-gnu/libstdc++.so.6",
            "7ffd00000000-7ffd00021000 rw-p 00000000 00:00 0 [stack]",
            "7f1000000000-7f1000001000 r-xp 00000000 00:00 0 ");

        ProcMapsLibcResolver.Parse(maps, Pid).Should().BeNull();
    }

    [Fact]
    public void Parse_DoesNotMatchLibcppOrSimilarPrefixes()
    {
        // libc++ / libcrypto must not be mistaken for the C runtime.
        var maps = string.Join('\n',
            "7f0000000000-7f0000021000 r-xp 00000000 08:01 1 /usr/lib/libc++.so.1",
            "7f0000100000-7f0000121000 r-xp 00000000 08:01 2 /usr/lib/libcrypto.so.3");

        ProcMapsLibcResolver.Parse(maps, Pid).Should().BeNull();
    }

    [Fact]
    public void Parse_ReturnsNull_OnEmptyInput()
    {
        ProcMapsLibcResolver.Parse(string.Empty, Pid).Should().BeNull();
    }
}

public sealed class PerfNativeAllocSamplerUnitTests
{
    [Fact]
    public void BuildEventName_ProducesValidIdentifier_AndIsUniquePerToken()
    {
        var a = PerfNativeAllocSampler.BuildEventName("malloc", "4242_abcd");
        a.Should().Be("diagmcp_malloc_4242_abcd");
        a.Should().MatchRegex("^[A-Za-z_][A-Za-z0-9_]*$");

        // A token with shell/path-unsafe characters is sanitized to underscores.
        var b = PerfNativeAllocSampler.BuildEventName("re-alloc", "4242/../x");
        b.Should().Be("diagmcp_re_alloc_4242____x");
        b.Should().MatchRegex("^[A-Za-z_][A-Za-z0-9_]*$");
    }

    [Fact]
    public void ParseCreatedTracepoint_ExtractsGroupAndEvent()
    {
        const string output =
            "Added new event:\n" +
            "  probe_libc:diagmcp_malloc_4242_abcd (on malloc in /usr/lib/x86_64-linux-gnu/libc.so.6)\n" +
            "\n" +
            "You can now use it in all perf tools, such as:\n" +
            "\tperf record -e probe_libc:diagmcp_malloc_4242_abcd -aR sleep 1\n";

        PerfNativeAllocSampler.ParseCreatedTracepoint(output)
            .Should().Be("probe_libc:diagmcp_malloc_4242_abcd");
    }

    [Fact]
    public void ParseCreatedTracepoint_ReturnsNull_WhenNoAddedEventLine()
    {
        PerfNativeAllocSampler.ParseCreatedTracepoint("Failed to find symbol malloc").Should().BeNull();
        PerfNativeAllocSampler.ParseCreatedTracepoint(string.Empty).Should().BeNull();
    }

    [Fact]
    public void IsAvailable_ReturnsFalse_OnNonLinux()
    {
        if (OperatingSystem.IsLinux()) return; // Linux availability depends on a perf binary being present.
        new PerfNativeAllocSampler().IsAvailable().Should().BeFalse();
    }

    [Fact]
    public async Task SampleAsync_RejectsOutOfRangeArguments()
    {
        var sampler = new PerfNativeAllocSampler();
        var bad = async () => await sampler.SampleAsync(processId: 1, duration: TimeSpan.Zero);
        await bad.Should().ThrowAsync<ArgumentOutOfRangeException>();

        var badTopN = async () => await sampler.SampleAsync(processId: 1, duration: TimeSpan.FromSeconds(1), topN: 0);
        await badTopN.Should().ThrowAsync<ArgumentOutOfRangeException>();

        var badPeriod = async () => await sampler.SampleAsync(processId: 1, duration: TimeSpan.FromSeconds(1), samplePeriod: 0);
        await badPeriod.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task SampleAsync_OnNonLinux_Throws_PlatformNotSupported()
    {
        if (OperatingSystem.IsLinux()) return;
        var sampler = new PerfNativeAllocSampler();
        var act = async () => await sampler.SampleAsync(processId: 1, duration: TimeSpan.FromSeconds(1));
        var ex = await act.Should().ThrowAsync<PlatformNotSupportedException>();
        ex.Which.Message.Should().Contain("Linux");
    }
}
