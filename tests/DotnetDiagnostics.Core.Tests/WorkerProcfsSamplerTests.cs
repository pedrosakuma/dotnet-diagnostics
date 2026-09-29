using System.Text;
using DotnetDiagnostics.Core.Captures;

namespace DotnetDiagnostics.Core.Tests;

public sealed class WorkerProcfsSamplerTests
{
    private static string Stat(string comm, string startTime = "12345") =>
        $"4242 ({comm}) S 1 4242 4242 0 -1 4194560 100 0 0 0 7 5 0 0 20 0 1 0 {startTime} 999424 3\n";

    [Theory]
    [InlineData("capture-worker")]
    [InlineData("a) (b c) S 9")]
    [InlineData("")]
    public void ParsesStartTimeAfterTheLastCommandParenthesis(string comm)
    {
        Assert.True(WorkerProcfsSampler.TryParseStartTime(Encoding.ASCII.GetBytes(Stat(comm)), out var startTime));
        Assert.Equal(12345, startTime);
    }

    [Theory]
    [InlineData("4242 capture-worker S 1")]
    [InlineData("4242 (w) S 1 4242")]
    [InlineData("4242 (w) S 1 4242 4242 0 -1 4194560 100 0 0 0 7 5 0 0 20 0 1 0 0 999424 3\n")]
    [InlineData("4242 (w) S 1 4242 4242 0 -1 4194560 100 0 0 0 7 5 0 0 20 0 1 0 x 999424 3\n")]
    [InlineData("4242 (w) S 1 4242 4242 0 -1 4194560 100 0 0 0 7 5 0 0 20 0 1 0 99999999999999999999 999424 3\n")]
    public void MalformedTruncatedZeroOrOverflowingStartTimeIsRejected(string stat)
    {
        Assert.False(WorkerProcfsSampler.TryParseStartTime(Encoding.ASCII.GetBytes(stat), out _));
    }

    [Fact]
    public void ReadsTheCurrentProcessStartTime()
    {
        if (!OperatingSystem.IsLinux()) return;
        Assert.True(WorkerProcfsSampler.ReadStartTime(Environment.ProcessId) > 0);
    }
}
