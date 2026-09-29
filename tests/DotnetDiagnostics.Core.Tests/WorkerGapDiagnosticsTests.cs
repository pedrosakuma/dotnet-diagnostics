using System.Diagnostics;
using System.Runtime.InteropServices;
using DotnetDiagnostics.Core.Captures;

namespace DotnetDiagnostics.Core.Tests;

[Collection("PortableExportResources")]
public sealed class WorkerGapDiagnosticsTests
{
    private static string Helper => Path.Combine(AppContext.BaseDirectory, "capture-worker-fixture");
    private static bool SupportedPlatform => OperatingSystem.IsLinux() &&
        RuntimeInformation.ProcessArchitecture == Architecture.X64;

    [Fact]
    public void StrictReportParserAcceptsTheFixedSchema()
    {
        const string nonce = "0123456789abcdef0123456789abcdef";
        var report = IsolatedCaptureWorker.ParseMonitorReport(Report(nonce,
            outcome: "Exited", exit: 0, peakRss: 4096, maxGapNs: 2_000_000, samples: 4), nonce);

        Assert.Equal("Exited", report.Outcome);
        Assert.Equal(4096, report.PeakRss);
        Assert.Equal(4, report.Samples);
        Assert.Equal("-", report.WorkerStderrHex);
    }

    [Theory]
    [InlineData("MONITOR_RESULT")]
    [InlineData("MONITOR-RESULT 2")]
    [InlineData("outcome=Unknown")]
    [InlineData("exit=0 exit=0")]
    [InlineData("workerStderrHex=0g")]
    public void StrictReportParserRejectsMalformedUnknownDuplicateOrOutOfOrderFields(string mutation)
    {
        const string nonce = "0123456789abcdef0123456789abcdef";
        var valid = Report(nonce, outcome: "Exited", exit: 0);
        var malformed = mutation switch
        {
            "MONITOR_RESULT" => valid.Replace("MONITOR-RESULT", mutation, StringComparison.Ordinal),
            "MONITOR-RESULT 2" => valid.Replace("MONITOR-RESULT 1", mutation, StringComparison.Ordinal),
            "outcome=Unknown" => valid.Replace("outcome=Exited", mutation, StringComparison.Ordinal),
            "exit=0 exit=0" => valid.Replace("exit=0 signal=0", mutation, StringComparison.Ordinal),
            _ => valid.Replace("workerStderrHex=-", "workerStderrRetained=1 workerStderrHex=0g",
                StringComparison.Ordinal)
        };

        var error = Assert.Throws<CaptureStoreException>(() =>
            IsolatedCaptureWorker.ParseMonitorReport(malformed, nonce));
        Assert.Equal(CaptureErrorCode.UnsupportedFormat, error.Code);
        Assert.Contains("WorkerMonitorReportInvalid", error.Message);
    }

    [Fact]
    public void OversizedOrMultiLineReportIsRejected()
    {
        const string nonce = "0123456789abcdef0123456789abcdef";
        Assert.Throws<CaptureStoreException>(() =>
            IsolatedCaptureWorker.ParseMonitorReport(new string('x', 1025), nonce));
        Assert.Throws<CaptureStoreException>(() =>
            IsolatedCaptureWorker.ParseMonitorReport(Report(nonce, "Exited", 0) + "\n", nonce));
    }

    [Fact]
    public void ObservationGapIsTelemetryOnSuccessfulExit()
    {
        const string nonce = "0123456789abcdef0123456789abcdef";
        var text = Report(nonce, "Exited", 0)
            .Replace("maxGapNs=0", "maxGapNs=12000000", StringComparison.Ordinal)
            .Replace("gapLastValidNs=0", "gapLastValidNs=3000000", StringComparison.Ordinal)
            .Replace("gapNowNs=0", "gapNowNs=15000000", StringComparison.Ordinal)
            .Replace("gapNs=0", "gapNs=12000000", StringComparison.Ordinal);
        var report = IsolatedCaptureWorker.ParseMonitorReport(text, nonce);

        Assert.Equal("Exited", report.Outcome);
        Assert.Equal(12_000_000, report.MaxGapNs);
        Assert.Equal(12_000_000, report.GapNs);
    }

    [Fact]
    public async Task KillingAnUnacknowledgedMonitorNeverExecutesOrLeavesTheWorker()
    {
        if (!SupportedPlatform) return;
        using var monitor = StartMonitor("stall", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));
        var line = await monitor.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var parts = line!.Split(' ');
        var workerPid = int.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture);

        monitor.Kill();
        Assert.True(monitor.WaitForExit(5000));
        await AssertProcessGoneAsync(workerPid);
    }

    private static Process StartMonitor(string mode, TimeSpan wall, TimeSpan cpu)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(Helper)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.Environment.Clear();
        foreach (var argument in new[]
        {
            "--monitor", checked(wall.Ticks * 100).ToString(System.Globalization.CultureInfo.InvariantCulture),
            checked(cpu.Ticks * 100).ToString(System.Globalization.CultureInfo.InvariantCulture),
            (256L * 1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture), "--",
            nonce, "sqlite", "/tmp", mode, "marker", "1", "1", Helper
        })
            start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }

    private static async Task AssertProcessGoneAsync(int processId)
    {
        var deadline = Stopwatch.StartNew();
        while (Directory.Exists($"/proc/{processId}") && deadline.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(10);
        Assert.False(Directory.Exists($"/proc/{processId}"));
    }

    private static string Report(string nonce, string outcome, int exit, int signal = 0, long peakRss = 0,
        long maxGapNs = 0, long samples = 0) =>
        $"MONITOR-RESULT 1 {nonce} outcome={outcome} exit={exit} signal={signal} peakRss={peakRss} " +
        $"maxGapNs={maxGapNs} samples={samples} gapLastValidNs=0 gapNowNs=0 gapNs=0 wallNs=1 cpuNs=0 " +
        "locked=0 workerStderrRetained=0 workerStderrHex=- monitorThreadCpuDeltaNs=0 monitorInvCtxSwDelta=0\n";

}
