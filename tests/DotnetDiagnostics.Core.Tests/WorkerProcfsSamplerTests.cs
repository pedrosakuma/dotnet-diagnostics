using System.Diagnostics;
using System.Text;
using DotnetDiagnostics.Core.Captures;

namespace DotnetDiagnostics.Core.Tests;

public sealed class WorkerProcfsSamplerTests
{
    private static string Stat(string comm, string rss = "3", string utime = "7", string stime = "5") =>
        $"4242 ({comm}) S 1 4242 4242 0 -1 4194560 100 0 0 0 {utime} {stime} 0 0 20 0 1 0 12345 999424 {rss} " +
        "18446744073709551615 1 1 0 0 0 0 0 0 0 0 0 17 3 0 0 0 0 0\n";

    [Theory]
    [InlineData("capture-worker")]
    [InlineData("a) (b c) S 9")]
    [InlineData("")]
    public void ParsesRssAndCpuAfterTheLastCommandParenthesis(string comm)
    {
        Assert.True(WorkerProcfsSampler.TryParse(Encoding.ASCII.GetBytes(Stat(comm)), 4096,
            TimeSpan.TicksPerSecond / 100, out var rss, out var cpu));
        Assert.Equal(3L * 4096, rss);
        Assert.Equal(TimeSpan.FromMilliseconds(120), cpu);
    }

    [Theory]
    [InlineData("4242 capture-worker S 1")]
    [InlineData("4242 (w) S 1 4242")]
    [InlineData("4242 (w) S 1 4242 4242 0 -1 4194560 100 0 0 0 x 5 0 0 20 0 1 0 12345 999424 3\n")]
    [InlineData("4242 (w) S 1 4242 4242 0 -1 4194560 100 0 0 0 7 5 0 0 20 0 1 0 12345 999424 -3\n")]
    [InlineData("4242 (w) S 1 4242 4242 0 -1 4194560 100 0 0 0 7 5 0 0 20 0 1 0 12345 999424  3\n")]
    [InlineData("4242 (w) S 1 4242 4242 0 -1 4194560 100 0 0 0 99999999999999999999 5 0 0 20 0 1 0 12345 999424 3\n")]
    public void MalformedTruncatedNegativeOrOverflowingRecordsAreRejected(string stat)
    {
        Assert.False(WorkerProcfsSampler.TryParse(Encoding.ASCII.GetBytes(stat), 4096,
            TimeSpan.TicksPerSecond / 100, out _, out _));
    }

    [Fact]
    public void ZeroRssIsReportedForTheCallerToConfirmExit()
    {
        Assert.True(WorkerProcfsSampler.TryParse(Encoding.ASCII.GetBytes(Stat("w", rss: "0")), 4096,
            TimeSpan.TicksPerSecond / 100, out var rss, out _));
        Assert.Equal(0, rss);
    }

    [Fact]
    public void RepeatedLiveReadsAndRuntimeSnapshotsDoNotAllocate()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var sampler = WorkerProcfsSampler.Open(Environment.ProcessId);
        sampler.Read(out _, out _);
        _ = SupervisorRuntimeSnapshot.Capture();
        var before = GC.GetAllocatedBytesForCurrentThread();
        long rss = 0;
        for (var i = 0; i < 1000; i++)
        {
            sampler.Read(out rss, out _);
            _ = SupervisorRuntimeSnapshot.Capture();
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(rss > 0);
        Assert.NotNull(SupervisorRuntimeSnapshot.Capture().ThreadCpu);
    }

    [Fact]
    public void HeldHandleFailsAfterTheChildIsReapedInsteadOfFollowingPidReuse()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var child = Process.Start(new ProcessStartInfo("/bin/sleep", "30") { UseShellExecute = false })!;
        try
        {
            using var sampler = WorkerProcfsSampler.Open(child.Id);
            // A freshly exec'd process can briefly report zero resident pages.
            var deadline = Stopwatch.StartNew();
            long rss;
            do { sampler.Read(out rss, out _); if (rss == 0) Thread.Sleep(1); }
            while (rss == 0 && deadline.Elapsed < TimeSpan.FromSeconds(10));
            Assert.True(rss > 0);
            child.Kill();
            Assert.True(child.WaitForExit(10000));
            Assert.Throws<IOException>(() => sampler.Read(out _, out _));
        }
        finally
        {
            if (!child.HasExited) { child.Kill(); child.WaitForExit(10000); }
        }
    }
}
