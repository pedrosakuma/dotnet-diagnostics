using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DotnetDiagnostics.Core.Captures;

namespace DotnetDiagnostics.Core.Tests;

[Collection("PortableExportResources")]
public sealed class SqliteRebuildTests
{
    [Fact]
    public void UnobservableExitDoesNotConvertAMetricFailureIntoSuccess()
    {
        var observations = new CaptureWorkerObservation(new());
        observations.Record(TimeSpan.Zero, 100, TimeSpan.Zero);
        var failure = Assert.Throws<CaptureStoreException>(() => observations.MetricUnavailable(
            new IOException("Purpose-created metric fault"),
            () => throw new System.ComponentModel.Win32Exception("Purpose-created exit fault"), () => TimeSpan.Zero));
        Assert.Contains("WorkerObservationUnavailable", failure.Message);
        Assert.False(observations.Completed);
    }

    [Theory]
    [InlineData(false, 50000, "WorkerObservationUnavailable")]
    [InlineData(true, 99999, null)]
    [InlineData(true, 100000, null)]
    [InlineData(true, 100001, "WorkerObservationGap")]
    [InlineData(true, 1200000001, "WorkerWallTime")]
    public void MetricReadFailureRequiresConfirmedExitWithinTheOriginalDeadline(bool exited, long ticks, string? reason)
    {
        var observations = new CaptureWorkerObservation(new());
        observations.Record(TimeSpan.Zero, 100, TimeSpan.Zero);
        var error = new System.ComponentModel.Win32Exception("Purpose-created process-information failure");
        if (reason is null)
        {
            observations.MetricUnavailable(error, () => exited, () => TimeSpan.FromTicks(ticks));
            Assert.True(observations.Completed);
        }
        else
        {
            var failure = Assert.Throws<CaptureStoreException>(() =>
                observations.MetricUnavailable(error, () => exited, () => TimeSpan.FromTicks(ticks)));
            Assert.Contains(reason, failure.Message);
            Assert.False(observations.Completed);
        }
    }

    [Fact]
    public async Task CancellationDrainsEvidenceWritesBeforeExitCallbackAndReturn()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        var directory = Path.Combine(AppContext.BaseDirectory, "rebuild-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var database = Path.Combine(directory, "capture.sqlite");
        File.Copy(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../Fixtures/SqliteAdmission/v2.sqlite")), database);
        try
        {
            using var cancellation = new CancellationTokenSource();
            using var evidence = new CancellationWrite(cancellation);
            var exited = false;
            var request = new SqliteAdmissionRequest(Path.Combine(AppContext.BaseDirectory, "capture-worker"),
                Path.Combine(AppContext.BaseDirectory, "runtimes/linux-x64/native/libe_sqlite3.so"), directory, database,
                new(2, 1, 1, 1, 2, 2), [new("11111111111111111111111111111111", "synthetic", "Independent v2 scalar fixture")], 1)
            {
                AfterExit = () =>
                {
                    Assert.True(evidence.Settled);
                    exited = true;
                }
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                IsolatedCaptureWorker.AdmitSqliteAsync(request, evidence, cancellationToken: cancellation.Token));
            Assert.True(exited);
            Assert.True(evidence.Settled);
        }
        finally
        {
            File.Delete(database);
            Directory.Delete(directory);
        }
    }

    private sealed class CancellationWrite(CancellationTokenSource cancellation) : MemoryStream
    {
        internal bool Settled { get; private set; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            finally { Settled = true; }
        }
    }

    [Theory]
    [InlineData(99999, true)]
    [InlineData(100000, true)]
    [InlineData(100001, false)]
    public void ExitTransitionPreservesLastValidDeadlineAndEndsOnlyTheRssWindow(long ticks, bool allowed)
    {
        var observations = new CaptureWorkerObservation(new());
        observations.Record(TimeSpan.Zero, 100, TimeSpan.Zero);
        if (!allowed)
        {
            var error = Assert.Throws<CaptureStoreException>(() => observations.ConfirmExit(TimeSpan.FromTicks(ticks)));
            Assert.Contains("WorkerObservationGap", error.Message);
            Assert.False(observations.Completed);
            return;
        }
        observations.ConfirmExit(TimeSpan.FromTicks(ticks));
        observations.CheckGap(TimeSpan.FromSeconds(1));
        Assert.True(observations.Completed);
        Assert.Equal(TimeSpan.FromTicks(ticks), observations.MaximumGap);
        var wall = Assert.Throws<CaptureStoreException>(() => observations.CheckWallTime(TimeSpan.FromSeconds(121)));
        Assert.Contains("WorkerWallTime", wall.Message);
        Assert.Throws<CaptureStoreException>(() => observations.Record(TimeSpan.FromSeconds(1), 100, TimeSpan.Zero));
    }

    [Fact]
    public async Task ConfirmedNativeExitEndsRssWindowBeforeBoundedParentEvidenceFlush()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        var directory = Path.Combine(AppContext.BaseDirectory, "rebuild-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var database = Path.Combine(directory, "capture.sqlite");
        File.Copy(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../Fixtures/SqliteAdmission/v2.sqlite")), database);
        Process? child = null;
        try
        {
            using var evidence = new DelayedFlush(() => child!);
            var request = new SqliteAdmissionRequest(Path.Combine(AppContext.BaseDirectory, "capture-worker"),
                Path.Combine(AppContext.BaseDirectory, "runtimes/linux-x64/native/libe_sqlite3.so"), directory, database,
                new(2, 1, 1, 1, 2, 2), [new("11111111111111111111111111111111", "synthetic", "Independent v2 scalar fixture")], 1)
                { BeforeInput = pid => child = Process.GetProcessById(pid) };
            var result = await IsolatedCaptureWorker.AdmitSqliteAsync(request, evidence);
            Assert.Equal(1, result.TableRows[3]);
            Assert.True(evidence.ConfirmedExit);
        }
        finally
        {
            child?.Dispose();
            File.Delete(database);
            Directory.Delete(directory);
        }
    }

    private sealed class DelayedFlush(Func<Process> child) : MemoryStream
    {
        internal bool ConfirmedExit { get; private set; }
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await child().WaitForExitAsync(cancellationToken);
            ConfirmedExit = child().HasExited;
            await Task.Delay(25, cancellationToken);
            await base.FlushAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task TrustedGeneratedFramesBuildFreshSchemaInSeparateConfinedChild()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        var directory = Path.Combine(AppContext.BaseDirectory, "rebuild-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            using var frames = new MemoryStream();
            WriteRow(frames, 0, 3L, 1L, 1L, 1L, 3L, 3L);
            const string artifact = "11111111111111111111111111111111";
            WriteRow(frames, 2, artifact, "counters", "Trusted generated rebuild");
            var completion = new byte[65];
            completion[0] = 5;
            BinaryPrimitives.WriteInt64LittleEndian(completion.AsSpan(1), 1);
            BinaryPrimitives.WriteInt64LittleEndian(completion.AsSpan(17), 1);
            Frame(frames, completion);
            frames.Position = 0;
            var executable = Path.Combine(AppContext.BaseDirectory, "capture-worker");
            var library = Path.Combine(AppContext.BaseDirectory, "runtimes/linux-x64/native/libe_sqlite3.so");
            var work = await IsolatedCaptureWorker.RebuildValidatedAsync(
                new(executable, library, directory, 256L * 1024 * 1024, 200_000_000, new()), frames, CancellationToken.None);
            Assert.InRange(work, 1, 200_000_000);
            var result = await IsolatedCaptureWorker.AdmitSqliteAsync(
                new(executable, library, directory, Path.Combine(directory, CapturePackage.Database),
                    CapturePackage.PortableFormat, [new(artifact, "counters", "Trusted generated rebuild")], 0),
                Stream.Null, cancellationToken: CancellationToken.None);
            Assert.Equal(new long[] { 1, 0, 1, 0, 0, 0 }, result.TableRows);
        }
        finally
        {
            foreach (var path in Directory.EnumerateFiles(directory)) File.Delete(path);
            Directory.Delete(directory);
        }
    }

    private static void WriteRow(Stream stream, byte table, params object[] values)
    {
        Frame(stream, [1, table, (byte)values.Length]);
        foreach (var value in values)
        {
            var bytes = value is string text ? CapturePackage.Utf8.GetBytes(text) : BitConverter.GetBytes((long)value);
            var header = new byte[6];
            header[0] = 2; header[1] = value is string ? (byte)3 : (byte)1;
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(2), bytes.Length);
            Frame(stream, header);
            Frame(stream, [3, .. bytes]);
        }
        Frame(stream, [4]);
    }

    private static void Frame(Stream stream, byte[] bytes)
    {
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(size, bytes.Length);
        stream.Write(size);
        stream.Write(bytes);
    }
}
