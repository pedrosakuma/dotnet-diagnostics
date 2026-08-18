using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.NativeAlloc;
using DotnetDiagnostics.Core.NativeLockContention;
using FluentAssertions;
using System.IO;
using Xunit;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Non-privileged smoke coverage for the perf command-line compatibility surface (issue #851):
/// perf binary discovery for the WSL wrapper-with-no-binary case, portable
/// <c>--max-size</c> argument formatting, full command-line construction for every perf-backed
/// collector (CPU, off-CPU is covered separately in <see cref="PerfSchedOffCpuCommandBuilderTests"/>,
/// native-allocation, native-lock-contention), and structured differentiation between perf failure
/// modes. All tests are pure — they never spawn perf or require elevated privileges. See
/// <c>docs/perf-compat-matrix.md</c> for the environment matrix these tests are meant to guard.
/// </summary>
public sealed class PerfCompatSmokeTests
{
    // ---- Perf binary discovery: WSL wrapper-with-no-binary ---------------------------------

    [Fact]
    public void Resolve_TreatsWslWrapperAsUnusable_AndFallsBackToKernelMatchedLinuxTools()
    {
        // Reproduces the #830-discovered WSL topology: /usr/bin/perf is a kernel-matching wrapper
        // script that prints a warning and exits non-zero because the matching linux-tools-<ver>
        // package was never installed for the running (virtualized) kernel. The wrapper itself is
        // NOT a working perf binary, but /usr/lib/linux-tools-<other-ver>/perf (installed for a
        // different kernel than uname -r reports, which can happen after a WSL kernel upgrade) is.
        var probedPaths = new List<string>();
        var resolved = PerfBinaryResolver.Resolve(
            configuredPath: "/usr/bin/perf",
            enumerateCandidates: () => new[]
            {
                "/usr/lib/linux-tools-5.15.167.4-microsoft-standard-WSL2/perf", // kernel-matched, still missing
                "/usr/lib/linux-tools-5.15.90.1-microsoft-standard-WSL2/perf", // older install, but usable
            },
            probe: path =>
            {
                probedPaths.Add(path);
                // The wrapper and the kernel-exact-matched candidate both fail; only the older
                // installed linux-tools package actually has a working binary.
                return path == "/usr/lib/linux-tools-5.15.90.1-microsoft-standard-WSL2/perf";
            });

        resolved.Should().Be("/usr/lib/linux-tools-5.15.90.1-microsoft-standard-WSL2/perf");
        probedPaths.Should().Equal(
            "/usr/bin/perf",
            "/usr/lib/linux-tools-5.15.167.4-microsoft-standard-WSL2/perf",
            "/usr/lib/linux-tools-5.15.90.1-microsoft-standard-WSL2/perf");
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenWrapperAndAllInstalledLinuxToolsAreUnusable()
    {
        // The fully-broken WSL topology from #830: no linux-tools package was ever installed, so
        // every candidate (including the wrapper) fails the version probe.
        var resolved = PerfBinaryResolver.Resolve(
            configuredPath: "/usr/bin/perf",
            enumerateCandidates: () => Array.Empty<string>(),
            probe: static _ => false);

        resolved.Should().BeNull();
    }

    [Fact]
    public void ProbePerfVersion_TreatsWslWarningBannerAsUnusable_EvenWithZeroExitCode()
    {
        // Some perf wrapper builds print the "WARNING: perf not found for kernel" banner to
        // stdout but still exit 0 (rather than non-zero) when they fall back to a stub. The
        // probe must not trust the exit code alone — only Linux shells can execute the fake
        // wrapper script, so this test is a no-op on other platforms.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), $"fake-wsl-perf-wrapper-{Guid.NewGuid():N}.sh");
        File.WriteAllText(scriptPath,
            "#!/bin/sh\n" +
            "echo 'WARNING: perf not found for kernel 5.15.167.4-microsoft-standard-WSL2'\n" +
            "echo 'You may need to install linux-tools-5.15.167.4-microsoft-standard-WSL2'\n" +
            "exit 0\n");
        try
        {
            File.SetUnixFileMode(scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            PerfBinaryResolver.ProbePerfVersion(scriptPath).Should().BeFalse();
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    // ---- Portable --max-size formatting -----------------------------------------------------

    [Theory]
    [InlineData(512L * 1024 * 1024, "512M")]
    [InlineData(128L * 1024 * 1024, "128M")]
    [InlineData(64L * 1024 * 1024, "64M")]
    [InlineData(1024L * 1024, "1M")]
    [InlineData(0L, "0M")]
    public void FormatPerfFileSize_ProducesHumanReadableMebibyteSuffix_ForExactMiBCounts(long bytes, string expected)
    {
        // Some perf versions reject raw byte counts for --max-size and require a human-readable
        // suffix such as "512M" (issue #851 context). Every collector's configured cap must
        // round-trip through this formatter to stay compatible with those perf builds.
        PerfNativeAotCpuSampler.FormatPerfFileSize(bytes).Should().Be(expected);
    }

    [Fact]
    public void FormatPerfFileSize_ThrowsForNegativeSize()
    {
        var act = () => PerfNativeAotCpuSampler.FormatPerfFileSize(-1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ---- Full command-line construction: CPU sampler ----------------------------------------

    [Fact]
    public void CpuSampler_BuildRecordArguments_UsesFrequencyDwarfPortableMaxSizeAndQuotedOutput()
    {
        var args = PerfNativeAotCpuSampler.BuildRecordArguments(
            pid: 4242, outputPath: "/tmp/cpu.data", duration: TimeSpan.FromSeconds(3.4), samplingFrequencyHz: 99);

        args.Should().ContainInOrder("record", "-F", "99");
        args.Should().ContainInOrder("--call-graph", "dwarf");
        args.Should().ContainInOrder("--max-size", "512M");
        args.Should().ContainInOrder("-p", "4242");
        args.Should().ContainInOrder("-o", "\"/tmp/cpu.data\"");
        args.Should().ContainInOrder("--", "sleep", "4");
    }

    [Fact]
    public void CpuSampler_BuildRecordArguments_RoundsSubSecondDurationUpToWholeSeconds()
    {
        var args = PerfNativeAotCpuSampler.BuildRecordArguments(
            pid: 1, outputPath: "/tmp/cpu.data", duration: TimeSpan.FromMilliseconds(200), samplingFrequencyHz: 99);

        args.Should().ContainInOrder("--", "sleep", "1");
    }

    // ---- Full command-line construction: native allocation ----------------------------------

    [Fact]
    public void NativeAlloc_BuildRecordArguments_IncludesEveryTracepointCallGraphPeriodAndMaxSize()
    {
        var args = PerfNativeAllocSampler.BuildRecordArguments(
            pid: 777,
            outputPath: "/tmp/alloc.data",
            duration: TimeSpan.FromSeconds(5),
            samplePeriod: 10,
            tracepoints: new[] { "probe_libc:malloc", "probe_libc:calloc" });

        args.Should().ContainInOrder("record", "-e", "probe_libc:malloc", "-e", "probe_libc:calloc");
        args.Should().ContainInOrder("--call-graph", "dwarf");
        args.Should().ContainInOrder("-c", "10");
        args.Should().ContainInOrder("-p", "777");
        args.Should().ContainInOrder("--max-size", "512M");
        args.Should().ContainInOrder("-o", "/tmp/alloc.data", "--", "sleep", "5");
    }

    // ---- Full command-line construction: native lock contention -----------------------------

    [Fact]
    public void NativeLockContention_BuildRecordArguments_IncludesEveryTracepointCallGraphPeriodAndMaxSize()
    {
        var args = PerfNativeLockContentionSampler.BuildRecordArguments(
            pid: 888,
            outputPath: "/tmp/lock.data",
            duration: TimeSpan.FromSeconds(2),
            samplePeriod: 5,
            tracepoints: new[] { "probe_libc:pthread_mutex_lock" });

        args.Should().ContainInOrder("record", "-e", "probe_libc:pthread_mutex_lock");
        args.Should().ContainInOrder("--call-graph", "dwarf");
        args.Should().ContainInOrder("-c", "5");
        args.Should().ContainInOrder("-p", "888");
        args.Should().ContainInOrder("--max-size", "512M");
        args.Should().ContainInOrder("-o", "/tmp/lock.data", "--", "sleep", "2");
    }

    // ---- Structured perf failure classification ----------------------------------------------

    [Theory]
    [InlineData("WARNING: perf not found for kernel 6.8.0-60-generic\nYou may need to install linux-tools-6.8.0-60-generic", PerfFailureKind.UnusableWrapper)]
    [InlineData("perf record: Permission denied", PerfFailureKind.PermissionDenied)]
    [InlineData("Error: Access to performance monitoring and observability operations is limited.\nConsider adjusting /proc/sys/kernel/perf_event_paranoid setting", PerfFailureKind.PermissionDenied)]
    [InlineData("event syntax error: 'sched:sched_switch'\nerror: sched:sched_switch event not found", PerfFailureKind.MissingTracepoint)]
    [InlineData("Error: File probe_libc:malloc not found", PerfFailureKind.MissingTracepoint)]
    [InlineData("failed to set thread specific data (dwarf callchain not supported)", PerfFailureKind.UnsupportedCallGraph)]
    [InlineData("dwarf: not supported on this platform", PerfFailureKind.UnsupportedCallGraph)]
    [InlineData("bash: perf: command not found", PerfFailureKind.MissingPerf)]
    [InlineData("something unrelated went wrong", PerfFailureKind.Unknown)]
    [InlineData("", PerfFailureKind.Unknown)]
    [InlineData(null, PerfFailureKind.Unknown)]
    public void Classify_DistinguishesFailureModes(string? perfOutput, PerfFailureKind expected)
    {
        PerfFailureClassifier.Classify(perfOutput).Should().Be(expected);
    }

    [Fact]
    public void ClassifyMissingBinary_AlwaysReturnsMissingPerf()
    {
        // The resolver returning null (every candidate failed the version probe) never produces
        // perf-authored text to classify from, so this is a distinct entry point.
        PerfFailureClassifier.ClassifyMissingBinary().Should().Be(PerfFailureKind.MissingPerf);
    }

    [Fact]
    public void Classify_PrefersUnusableWrapper_OverPermissionDenied_WhenBothPhrasesArePresent()
    {
        // A wrapper banner sometimes chains into a downstream "Permission denied" once bash tries
        // the fallback stub. The wrapper diagnosis is more specific/actionable (install
        // linux-tools) than a generic permission failure, so it must win when both are present.
        var combined = "WARNING: perf not found for kernel 6.8.0-60-generic\n" +
            "bash: /usr/lib/linux-tools-6.8.0-60-generic/perf: Permission denied";

        PerfFailureClassifier.Classify(combined).Should().Be(PerfFailureKind.UnusableWrapper);
    }
}
