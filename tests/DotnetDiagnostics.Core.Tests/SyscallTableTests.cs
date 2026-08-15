using DotnetDiagnostics.Core.OffCpu;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>Sanity checks for <see cref="SyscallTable"/>'s x86_64 lookup table used on this CI's architecture (issue #829).</summary>
public sealed class SyscallTableTests
{
    [Theory]
    [InlineData(0, "read")]
    [InlineData(1, "write")]
    [InlineData(232, "epoll_wait")]
    public void Resolve_KnownX64Ids_ReturnsExpectedName(long id, string expected)
    {
        // These tests run on x86_64 CI runners (ubuntu-latest/windows-latest); guard so the
        // assertion is meaningful only where RuntimeInformation.ProcessArchitecture is X64.
        if (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            != System.Runtime.InteropServices.Architecture.X64)
        {
            return;
        }

        SyscallTable.Resolve(id).Should().Be(expected);
    }

    [Fact]
    public void Resolve_UnknownId_FallsBackToSyscallPrefixedName()
    {
        SyscallTable.Resolve(999_999).Should().Be("syscall_999999");
    }

    [Fact]
    public void X64_Futex_ResolvesToSyscallNumber202()
    {
        // futex is the single most important syscall for off-CPU attribution (lock/monitor
        // waits) — pin its exact x86_64 number as a regression guard independent of architecture.
        if (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            != System.Runtime.InteropServices.Architecture.X64)
        {
            return;
        }

        SyscallTable.Resolve(202).Should().Be("futex");
    }
}
