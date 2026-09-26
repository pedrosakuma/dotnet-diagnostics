using System.Diagnostics;
using System.Runtime.InteropServices;
using DotnetDiagnostics.Core.Captures;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace DotnetDiagnostics.Core.Tests;

[Collection("PortableExportResources")]
public sealed class IsolatedCaptureWorkerTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "worker-tests", Guid.NewGuid().ToString("N"));
    private static string Worker => Path.Combine(AppContext.BaseDirectory, "capture-worker");
    private static string Helper => Path.Combine(AppContext.BaseDirectory, "capture-worker-fixture");
    private static bool SupportedPlatform => OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealContainedChild_InitializesSqliteAndDeniesExternalCapabilities(bool writable)
    {
        if (!SupportedPlatform) return;
        var fixture = CreateFixture("fixture ?#% \u00e9.db");
        using var helper = StartHelper();
        var address = await helper.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var before = File.ReadAllBytes(fixture);
            var parentHeapLimit = SQLitePCL.raw.sqlite3_hard_heap_limit64(-1);
            var result = await IsolatedCaptureWorker.ProbeTrustedFixtureAsync(
                Request(fixture, helper.Id, address!) with { WritableProfile = writable });
            Assert.Equal(123, result.FixtureValue);
            Assert.Equal(21, result.DeniedProbes);
            Assert.Equal(parentHeapLimit, SQLitePCL.raw.sqlite3_hard_heap_limit64(-1));
            Assert.True(result.LandlockAbi >= 3);
            Assert.InRange(result.PeakObservedRss, 1, 256L * 1024 * 1024);
            Assert.InRange(result.MaximumObservationGap, TimeSpan.Zero, TimeSpan.FromMilliseconds(10));
            Assert.InRange(result.VmInstructions, 1000, 200000000);
            Assert.Equal(before, File.ReadAllBytes(fixture));
            Assert.Equal("benign unrelated marker", File.ReadAllText(Path.Combine(_root, "unrelated.txt")));
            Assert.False(helper.HasExited);
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(fixture)!));
            output.WriteLine($"writableProfile={writable}; LandlockAbi={result.LandlockAbi}; SQLite={result.SqliteVersion}; denied={result.DeniedProbes}; " +
                $"peakObservedRss={result.PeakObservedRss}; maxGapMs={result.MaximumObservationGap.TotalMilliseconds:F3}; " +
                $"vmInstructions={result.VmInstructions}; resultBytes={result.OutputBytes}");
        }
        finally { Stop(helper); }
    }

    [Theory]
    [InlineData("bad-handshake", CaptureErrorCode.UnsupportedFormat, "WorkerHandshakeInvalid")]
    [InlineData("unsupported", CaptureErrorCode.UnsupportedFormat, "PurposeCreatedMissingFacility")]
    [InlineData("flood", CaptureErrorCode.CapacityExceeded, "WorkerOutputBytes")]
    [InlineData("crash", CaptureErrorCode.StorageFailure, "Worker exited")]
    public async Task PurposeCreatedProtocolFailuresNeverProduceCapabilities(string mode, CaptureErrorCode code, string reason)
    {
        if (!SupportedPlatform) return;
        var fixture = CreateFixture(mode + ".db");
        using var helper = StartHelper();
        var address = await helper.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var error = await Assert.ThrowsAsync<CaptureStoreException>(() =>
                IsolatedCaptureWorker.ProbeTrustedFixtureAsync(Request(fixture, helper.Id, address!) with { Executable = Helper },
                    new() { WallTime = TimeSpan.FromMilliseconds(100) }));
            Assert.Equal(code, error.Code);
            Assert.Contains(reason, error.Message);
            Assert.False(helper.HasExited);
        }
        finally { Stop(helper); }
    }

    [Fact]
    public async Task StalledChildFailsClosedAtFirstWallOrObservationDeadline()
    {
        if (!SupportedPlatform) return;
        var fixture = CreateFixture("stall.db");
        using var helper = StartHelper();
        var address = await helper.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        CaptureWorkerCapabilities? capabilities = null;
        try
        {
            var error = await Assert.ThrowsAsync<CaptureStoreException>(async () =>
            {
                capabilities = await IsolatedCaptureWorker.ProbeTrustedFixtureAsync(
                    Request(fixture, helper.Id, address!) with { Executable = Helper },
                    new() { WallTime = TimeSpan.FromMilliseconds(100) });
            });
            Assert.Equal(CaptureErrorCode.CapacityExceeded, error.Code);
            var reason = error.Message.Split(':', 2)[0];
            Assert.True(reason is "WorkerWallTime" or "WorkerObservationGap", error.Message);
            Assert.Null(capabilities);
            Assert.False(helper.HasExited);
            output.WriteLine($"Stalled child rejected by first watchdog decision: {reason}");
        }
        finally { Stop(helper); }
    }

    [Theory]
    [InlineData(100, false)]
    [InlineData(100, true)]
    [InlineData(120000, false)]
    [InlineData(120000, true)]
    public void WallDeadlineIsEnforcedWithAndWithoutMandatoryObservations(int milliseconds, bool mandatory)
    {
        var limit = TimeSpan.FromMilliseconds(milliseconds);
        var observations = new CaptureWorkerObservation(new() { WallTime = limit });
        for (var elapsed = 0; elapsed <= milliseconds; elapsed += 10)
        {
            var now = TimeSpan.FromMilliseconds(elapsed);
            observations.CheckWallTime(now);
            if (mandatory) observations.Record(now, 1000, TimeSpan.Zero);
        }
        var expired = limit + TimeSpan.FromTicks(1);
        observations.CheckGap(expired);
        var error = Assert.Throws<CaptureStoreException>(() => observations.CheckWallTime(expired));
        Assert.Equal(CaptureErrorCode.CapacityExceeded, error.Code);
        Assert.StartsWith("WorkerWallTime:", error.Message);
        Assert.Equal(mandatory ? TimeSpan.FromMilliseconds(10) : TimeSpan.Zero, observations.MaximumGap);
    }

    [Fact]
    public async Task CancellationTerminatesPurposeCreatedWaitingChild()
    {
        if (!SupportedPlatform) return;
        var fixture = CreateFixture("stall.db");
        using var helper = StartHelper();
        var address = await helper.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                IsolatedCaptureWorker.ProbeTrustedFixtureAsync(Request(fixture, helper.Id, address!) with { Executable = Helper },
                    cancellationToken: cancellation.Token));
        }
        finally { Stop(helper); }
    }

    [Fact]
    public void ObservationGapOverTenMillisecondsInvalidatesInsteadOfRecordingSuccess()
    {
        var observations = new CaptureWorkerObservation(new());
        observations.Record(TimeSpan.Zero, 1000, TimeSpan.Zero);
        observations.Record(TimeSpan.FromMilliseconds(10), 1000, TimeSpan.Zero);
        var error = Assert.Throws<CaptureStoreException>(() =>
            observations.Record(TimeSpan.FromMilliseconds(20.001), 1000, TimeSpan.Zero));
        Assert.Equal(CaptureErrorCode.CapacityExceeded, error.Code);
        Assert.Contains("WorkerObservationGap", error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResourceThresholdsAreCheckedAtObservation(bool resident)
    {
        var observations = new CaptureWorkerObservation(new());
        observations.Record(TimeSpan.Zero, 256L * 1024 * 1024, TimeSpan.FromSeconds(60));
        var error = Assert.Throws<CaptureStoreException>(() => observations.Record(TimeSpan.FromMilliseconds(1),
            256L * 1024 * 1024 + (resident ? 1 : 0), TimeSpan.FromSeconds(60) + (resident ? TimeSpan.Zero : TimeSpan.FromTicks(1))));
        Assert.Contains(resident ? "WorkerResidentBytes" : "WorkerCpuTime", error.Message);
    }

    [Fact]
    public async Task MissingWorkerAssetIsExplicitlyUnsupported()
    {
        var request = SupportedPlatform
            ? Request(CreateFixture("fixture.db"), 0, "1") with { Executable = Path.Combine(_root, "missing") }
            : new CaptureWorkerProbe("", "", "", "", "", 0, "", "");
        var error = await Assert.ThrowsAsync<CaptureStoreException>(() =>
            IsolatedCaptureWorker.ProbeTrustedFixtureAsync(request));
        Assert.Equal(CaptureErrorCode.UnsupportedFormat, error.Code);
        Assert.Contains(SupportedPlatform ? "WorkerAssetUnavailable" : "WorkerPlatformUnavailable", error.Message);
    }

    [Fact]
    public async Task RealChildExceedingLowerResidentPolicyIsTerminatedBeforeSqliteQuery()
    {
        if (!SupportedPlatform) return;
        var fixture = CreateFixture("fixture.db");
        using var helper = StartHelper();
        var address = await helper.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var error = await Assert.ThrowsAsync<CaptureStoreException>(() =>
                IsolatedCaptureWorker.ProbeTrustedFixtureAsync(Request(fixture, helper.Id, address!), new() { ResidentBytes = 1 }));
            Assert.Equal(CaptureErrorCode.CapacityExceeded, error.Code);
            Assert.Contains("WorkerResidentBytes", error.Message);
            Assert.False(helper.HasExited);
        }
        finally { Stop(helper); }
    }

    [Theory]
    [InlineData("no-landlock", "LandlockUnavailable")]
    [InlineData("no-seccomp", "SeccompUnavailable")]
    public async Task ActualMissingKernelFacilityRejectsWithoutFallback(string mode, string reason)
    {
        if (!SupportedPlatform) return;
        var fixture = CreateFixture(mode + ".db");
        using var helper = StartHelper();
        var address = await helper.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var error = await Assert.ThrowsAsync<CaptureStoreException>(() => IsolatedCaptureWorker.ProbeTrustedFixtureAsync(
                Request(fixture, helper.Id, address!) with { Executable = Helper, HelperExecutable = Worker }));
            Assert.Equal(CaptureErrorCode.UnsupportedFormat, error.Code);
            Assert.Contains(reason, error.Message);
        }
        finally { Stop(helper); }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(1000, -1)]
    public void UnavailableObservationNeverBecomesZeroCostSuccess(long rss, int cpuTicks)
    {
        var error = Assert.Throws<CaptureStoreException>(() => new CaptureWorkerObservation(new())
            .Record(TimeSpan.Zero, rss, TimeSpan.FromTicks(cpuTicks)));
        Assert.Equal(CaptureErrorCode.UnsupportedFormat, error.Code);
        Assert.Contains("WorkerObservationUnavailable", error.Message);
    }

    private string CreateFixture(string name)
    {
        Directory.CreateDirectory(_root);
        var staging = Path.Combine(_root, "private");
        Directory.CreateDirectory(staging);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(staging,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(_root, "unrelated.txt"), "benign unrelated marker");
        var path = Path.Combine(staging, name);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE probe(value INTEGER NOT NULL); INSERT INTO probe VALUES (123);";
        command.ExecuteNonQuery();
        return path;
    }

    private CaptureWorkerProbe Request(string fixture, int pid, string address) => new(Worker,
        Path.Combine(AppContext.BaseDirectory, "runtimes", "linux-x64", "native", "libe_sqlite3.so"),
        Path.GetDirectoryName(fixture)!, fixture, Path.Combine(_root, "unrelated.txt"), pid, address, Helper);

    private static Process StartHelper()
    {
        var start = new ProcessStartInfo(Helper) { UseShellExecute = false, RedirectStandardOutput = true };
        start.ArgumentList.Add("helper");
        start.Environment.Clear();
        return Process.Start(start)!;
    }

    private static void Stop(Process process)
    {
        if (!process.HasExited) process.Kill();
        Assert.True(process.WaitForExit(5000));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
