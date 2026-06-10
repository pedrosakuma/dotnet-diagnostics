using System;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// xunit <see cref="FactAttribute"/> variant that runtime-skips a test only when
/// running on Linux CI (<c>CI=true</c> + <see cref="OperatingSystem.IsLinux"/>).
/// Local Linux developers, Windows CI, and macOS CI still execute the test.
/// </summary>
/// <remarks>
/// Used to quarantine `LiveCoreClrProcessTests` cases that reliably segfault the
/// xunit test host on ubuntu-latest under full-suite load (native crash inside
/// libcoreclr's EventPipe SampleProfiler — see issues #145 and #147). The skip
/// is conditional so the regression tests stay runnable locally and on Windows,
/// preserving coverage of the closed-generic handoff contract from issue #21.
///
/// The skip can be forced off by setting
/// <see cref="RunQuarantinedEnvVar"/> (<c>DOTNET_DBG_MCP_RUN_QUARANTINED_LINUX_TESTS=1</c>).
/// The dedicated <c>linux-crash-repro</c> CI job (see <c>.github/workflows/ci.yml</c>)
/// sets it to deliberately reproduce the crash under <c>strace</c> so the runtime
/// team can correlate the unmapped fault address against the mmap/munmap log
/// (dotnet/runtime#128525).
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class SkipOnLinuxCiFactAttribute : FactAttribute
{
    /// <summary>
    /// Environment variable that, when set to <c>1</c>/<c>true</c>, overrides the
    /// Linux-CI quarantine so the test executes anyway. Used by the strace-based
    /// crash-repro CI job (dotnet/runtime#128525).
    /// </summary>
    public const string RunQuarantinedEnvVar = "DOTNET_DBG_MCP_RUN_QUARANTINED_LINUX_TESTS";

    public SkipOnLinuxCiFactAttribute(string skipReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skipReason);

        if (IsLinuxCi() && !RunQuarantinedOptIn())
        {
            Skip = skipReason;
        }
    }

    private static bool IsLinuxCi()
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        var ci = Environment.GetEnvironmentVariable("CI");
        return string.Equals(ci, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ci, "1", StringComparison.Ordinal);
    }

    private static bool RunQuarantinedOptIn()
    {
        var optIn = Environment.GetEnvironmentVariable(RunQuarantinedEnvVar);
        return string.Equals(optIn, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(optIn, "1", StringComparison.Ordinal);
    }
}
