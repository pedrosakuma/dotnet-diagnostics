using System.Diagnostics;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Threads;
using FluentAssertions;
using Microsoft.Diagnostics.Tracing.Session;

namespace DotnetDiagnostics.Core.Tests;

[Collection("LiveProcess")]
public sealed class LiveWindowsNativeAotThreadSnapshotTests : IAsyncLifetime
{
    private Process? _sampleProcess;
    private string? _publishDir;
    private readonly TaskCompletionSource<Uri> _listeningUrl = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task InitializeAsync()
    {
        if (!OperatingSystem.IsWindows() || TraceEventSession.IsElevated() != true)
        {
            return;
        }

        var inspector = new EtwNativeThreadSnapshotInspector();
        if (!inspector.IsAvailable())
        {
            return;
        }

        var publishDir = Path.Combine(AppContext.BaseDirectory, "test-artifacts", nameof(LiveWindowsNativeAotThreadSnapshotTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(publishDir);

        try
        {
            await PrePublishedNativeAotSample.PrepareAsync(publishDir, TimeSpan.FromMinutes(2));
            _publishDir = publishDir;
        }
        catch
        {
            try { Directory.Delete(publishDir, recursive: true); } catch { /* best effort */ }
            throw;
        }
    }

    public Task DisposeAsync()
    {
        if (_sampleProcess is { HasExited: false })
        {
            try
            {
                _sampleProcess.Kill(entireProcessTree: true);
                _sampleProcess.WaitForExit(5_000);
            }
            catch (Exception)
            {
                // best effort
            }
        }
        _sampleProcess?.Dispose();

        if (!string.IsNullOrWhiteSpace(_publishDir))
        {
            try { Directory.Delete(_publishDir, recursive: true); } catch { /* best effort */ }
        }

        return Task.CompletedTask;
    }

    // NativeAOT publish on Windows CI can take significantly longer than non-AOT live tests.
    [Fact(Timeout = 180_000)]
    public async Task RoutingInspector_OnWindowsNativeAot_ReturnsEtwNativeThreadSnapshot()
    {
        if (!OperatingSystem.IsWindows() || TraceEventSession.IsElevated() != true)
        {
            return;
        }
        _publishDir.Should().NotBeNullOrWhiteSpace("the NativeAOT sample must be published before this test runs");

        var exePath = Path.Combine(_publishDir!, "NativeAotSample.exe");
        File.Exists(exePath).Should().BeTrue("the published NativeAOT executable is required");

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _publishDir,
        };
        psi.ArgumentList.Add("--urls");
        psi.ArgumentList.Add("http://127.0.0.1:0");
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";

        _sampleProcess = Process.Start(psi);
        _sampleProcess.Should().NotBeNull();

        _ = Task.Run(async () =>
        {
            try { while (await _sampleProcess!.StandardError.ReadLineAsync() is not null) { } }
            catch { /* best effort */ }
        });
        _ = Task.Run(async () =>
        {
            try
            {
                while (await _sampleProcess!.StandardOutput.ReadLineAsync() is { } line)
                {
                    const string marker = "Now listening on:";
                    var index = line.IndexOf(marker, StringComparison.Ordinal);
                    if (index >= 0) _listeningUrl.TrySetResult(new Uri(line[(index + marker.Length)..].Trim()));
                }
                _listeningUrl.TrySetException(new InvalidOperationException("The sample exited before reporting its listening URL."));
            }
            catch (Exception exception) { _listeningUrl.TrySetException(exception); }
        });

        await WaitForDiagnosticEndpointAsync(_sampleProcess!.Id, TimeSpan.FromSeconds(45));

        var detector = new CapabilityDetector(etwSampler: new DotnetDiagnostics.Core.CpuSampling.EtwNativeAotCpuSampler());
        var caps = await detector.DetectAsync(_sampleProcess.Id, CancellationToken.None);
        caps.Runtime.Should().Be(RuntimeFlavor.NativeAot);
        caps.CanCollectThreadSnapshot.Should().BeTrue();
        caps.ThreadSnapshotSource.Should().Be("etw-native-stack");

        var inspector = new RoutingThreadSnapshotInspector(
            detector,
            new IThreadSnapshotBackend[]
            {
                new ClrMdThreadSnapshotBackend(new ClrMdThreadSnapshotInspector()),
                new LinuxNativeThreadSnapshotBackend(new LinuxNativeThreadSnapshotInspector()),
                new EtwNativeThreadSnapshotBackend(new EtwNativeThreadSnapshotInspector()),
            });

        using var client = new HttpClient
        {
            BaseAddress = await _listeningUrl.Task.WaitAsync(TimeSpan.FromSeconds(45)),
            Timeout = TimeSpan.FromSeconds(10),
        };
        using var workloadCancellation = new CancellationTokenSource();
        var workload = Task.WhenAll(
            RunWorkloadAsync(client, workloadCancellation.Token),
            RunWorkloadAsync(client, workloadCancellation.Token));
        ThreadSnapshotArtifact snapshot;
        try
        {
            using var ready = await client.GetAsync("cpu");
            ready.EnsureSuccessStatusCode();
            snapshot = await inspector.InspectLiveAsync(
                _sampleProcess.Id, new ThreadSnapshotOptions(MaxFramesPerThread: 32), CancellationToken.None);
        }
        finally
        {
            workloadCancellation.Cancel();
            await workload;
        }

        snapshot.Origin.Should().Be(ThreadSnapshotOrigin.Live);
        snapshot.Source.Should().Be("etw-native-stack");
        snapshot.RuntimeName.Should().Be("NativeAot");
        snapshot.Threads.Should().NotBeEmpty();
        snapshot.Threads.Select(t => t.OSThreadId).Should().OnlyHaveUniqueItems();
        snapshot.Threads.Count.Should().BeGreaterThan(0);
        snapshot.Threads.SelectMany(t => t.Frames).Should().NotBeEmpty();
    }

    private static async Task RunWorkloadAsync(HttpClient client, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var response = await client.GetAsync("cpu", cancellationToken);
                response.EnsureSuccessStatusCode();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task WaitForDiagnosticEndpointAsync(int pid, TimeSpan timeout)
    {
        var discovery = new LocalProcessDiscovery();
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var info = discovery.TryGetProcess(pid);
            if (info is not null)
            {
                return;
            }
            await Task.Delay(250);
        }
        throw new TimeoutException($"Timed out waiting for native sample diagnostic endpoint for pid {pid}.");
    }
}
