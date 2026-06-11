using DotnetDiagnostics.Core.Capabilities;
using Xunit;

namespace DotnetDiagnostics.Core.Tests;

public sealed class PtraceProbeTests
{
    private const string StatusWithCap = "Name:\tdotnet\nCapEff:\t00000000a80c25fb\n";
    private const string StatusWithoutCap = "Name:\tdotnet\nCapEff:\t0000000000000000\n";
    private const string StatusMalformed = "Name:\tdotnet\nCapEff:\tnot-a-hex\n";

    private static Func<string, string> ReadAllText(IDictionary<string, string> files)
        => path => files.TryGetValue(path, out var v) ? v : throw new FileNotFoundException(path);

    private static Func<string, bool> FileExists(IDictionary<string, string> files)
        => path => files.ContainsKey(path);

    [Fact]
    public void CapSysPtrace_held_overrides_any_scope()
    {
        var files = new Dictionary<string, string>
        {
            [PtraceProbe.ProcSelfStatusPath] = StatusWithCap,
            [PtraceProbe.YamaPtraceScopePath] = "1\n",
        };

        var result = PtraceProbe.DetectLinux(ReadAllText(files), FileExists(files));

        Assert.True(result.CanAttach);
        Assert.True(result.HasCapSysPtrace);
        Assert.Equal(1, result.PtraceScope);
        Assert.Contains("CAP_SYS_PTRACE held", result.Reason);
    }

    [Fact]
    public void Scope0_alone_allows_attach()
    {
        var files = new Dictionary<string, string>
        {
            [PtraceProbe.ProcSelfStatusPath] = StatusWithoutCap,
            [PtraceProbe.YamaPtraceScopePath] = "0\n",
        };

        var result = PtraceProbe.DetectLinux(ReadAllText(files), FileExists(files));

        Assert.True(result.CanAttach);
        Assert.False(result.HasCapSysPtrace);
        Assert.Equal(0, result.PtraceScope);
        Assert.Contains("ptrace_scope=0", result.Reason);
    }

    [Fact]
    public void Scope1_without_cap_blocks_attach_and_lists_mitigations()
    {
        var files = new Dictionary<string, string>
        {
            [PtraceProbe.ProcSelfStatusPath] = StatusWithoutCap,
            [PtraceProbe.YamaPtraceScopePath] = "1\n",
        };

        var result = PtraceProbe.DetectLinux(ReadAllText(files), FileExists(files));

        Assert.False(result.CanAttach);
        Assert.False(result.HasCapSysPtrace);
        Assert.Equal(1, result.PtraceScope);
        Assert.Contains("ptrace_scope=1", result.Reason);
        Assert.Contains("--cap-add SYS_PTRACE", result.Reason);
        Assert.Contains("ptrace_scope=0", result.Reason);
    }

    [Fact]
    public void Scope2_without_cap_blocks_attach()
    {
        var files = new Dictionary<string, string>
        {
            [PtraceProbe.ProcSelfStatusPath] = StatusWithoutCap,
            [PtraceProbe.YamaPtraceScopePath] = "2",
        };

        var result = PtraceProbe.DetectLinux(ReadAllText(files), FileExists(files));

        Assert.False(result.CanAttach);
        Assert.False(result.HasCapSysPtrace);
        Assert.Equal(2, result.PtraceScope);
        Assert.Contains("ptrace_scope=2", result.Reason);
    }

    [Fact]
    public void Scope3_blocks_even_when_cap_is_documented_as_irrelevant()
    {
        var files = new Dictionary<string, string>
        {
            [PtraceProbe.ProcSelfStatusPath] = StatusWithoutCap,
            [PtraceProbe.YamaPtraceScopePath] = "3",
        };

        var result = PtraceProbe.DetectLinux(ReadAllText(files), FileExists(files));

        Assert.False(result.CanAttach);
        Assert.False(result.HasCapSysPtrace);
        Assert.Equal(3, result.PtraceScope);
        Assert.Contains("ptrace_scope=3", result.Reason);
        Assert.Contains("cannot override", result.Reason);
    }

    [Fact]
    public void Scope3_blocks_even_with_cap_sys_ptrace()
    {
        var files = new Dictionary<string, string>
        {
            [PtraceProbe.ProcSelfStatusPath] = StatusWithCap,
            [PtraceProbe.YamaPtraceScopePath] = "3",
        };

        var result = PtraceProbe.DetectLinux(ReadAllText(files), FileExists(files));

        Assert.False(result.CanAttach);
        Assert.True(result.HasCapSysPtrace);
        Assert.Equal(3, result.PtraceScope);
        Assert.Contains("cannot override", result.Reason);
    }

    [Fact]
    public void Yama_missing_means_classic_same_uid_attach_allowed()
    {
        var files = new Dictionary<string, string>
        {
            [PtraceProbe.ProcSelfStatusPath] = StatusWithoutCap,
        };

        var result = PtraceProbe.DetectLinux(ReadAllText(files), FileExists(files));

        Assert.True(result.CanAttach);
        Assert.False(result.HasCapSysPtrace);
        Assert.Null(result.PtraceScope);
        Assert.Contains("Yama LSM not enabled", result.Reason);
    }

    [Fact]
    public void Malformed_CapEff_does_not_throw_and_falls_through_to_scope()
    {
        var files = new Dictionary<string, string>
        {
            [PtraceProbe.ProcSelfStatusPath] = StatusMalformed,
            [PtraceProbe.YamaPtraceScopePath] = "0",
        };

        var result = PtraceProbe.DetectLinux(ReadAllText(files), FileExists(files));

        Assert.True(result.CanAttach);
        Assert.False(result.HasCapSysPtrace);
        Assert.Equal(0, result.PtraceScope);
        Assert.Contains("ptrace_scope=0", result.Reason);
    }

    [Fact]
    public void Unknown_scope_value_is_treated_as_blocking()
    {
        var files = new Dictionary<string, string>
        {
            [PtraceProbe.ProcSelfStatusPath] = StatusWithoutCap,
            [PtraceProbe.YamaPtraceScopePath] = "42",
        };

        var result = PtraceProbe.DetectLinux(ReadAllText(files), FileExists(files));

        Assert.False(result.CanAttach);
        Assert.False(result.HasCapSysPtrace);
        Assert.Equal(42, result.PtraceScope);
        Assert.Contains("unknown value", result.Reason);
    }

    [Fact]
    public void ChildLaunch_unblocks_only_scope1_without_cap()
    {
        // The one environment child-launch actually helps: scope=1, no CAP_SYS_PTRACE, attach blocked.
        var blocked = new PtraceProbeResult(CanAttach: false, Reason: "blocked")
        {
            HasCapSysPtrace = false,
            PtraceScope = 1,
        };

        // The predicate is gated on the real host OS — only Linux permits descendant attach.
        Assert.Equal(OperatingSystem.IsLinux(), PtraceProbe.ChildLaunchWouldUnblockAttach(blocked));
    }

    [Theory]
    // (canAttach, hasCap, scope) — none of these benefit from child-launch on any OS.
    [InlineData(true, false, 1)]  // already attachable
    [InlineData(false, true, 1)]  // cap held → attach already works elsewhere; nothing to unblock
    [InlineData(false, false, 0)] // scope 0 already allows attach
    [InlineData(false, false, 2)] // scope 2 still needs CAP regardless of ancestry
    [InlineData(false, false, 3)] // scope 3 forbids attach entirely
    [InlineData(false, false, null)] // Yama absent → classic attach already allowed
    public void ChildLaunch_does_not_advertise_outside_scope1(bool canAttach, bool hasCap, int? scope)
    {
        var result = new PtraceProbeResult(canAttach, Reason: "x")
        {
            HasCapSysPtrace = hasCap,
            PtraceScope = scope,
        };

        Assert.False(PtraceProbe.ChildLaunchWouldUnblockAttach(result));
    }
}
