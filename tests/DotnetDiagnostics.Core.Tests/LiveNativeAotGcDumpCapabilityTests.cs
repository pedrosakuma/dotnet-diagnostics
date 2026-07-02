using System.Diagnostics;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.ProcessDiscovery;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Live coverage for the NativeAOT gcdump capability gate (issue #471). Publishes the
/// <c>NativeAotSample</c> with <c>PublishAot=true</c>, attaches over the diagnostic IPC socket, and
/// asserts <see cref="DiagnosticCapabilities.CanCollectGcDump"/> is reported <c>false</c> for a live
/// NativeAOT process.
/// <para>
/// gcdump is deliberately withheld on NativeAOT: empirically, on .NET 10 (SDK 10.0.201) requesting the
/// <c>GCHeapSnapshot</c> EventPipe keyword (0x1980001) <b>crashes the NativeAOT runtime</b> — the target
/// segfaults (<c>Segmentation fault (core dumped)</c>), the IPC stream closes mid-handshake
/// (<c>EndOfStreamException</c>), and the process exits, while the benign GC keyword (0x1) is safe but
/// emits zero <c>GCBulkNode</c> events. This test therefore stops at capability detection and does
/// <b>not</b> drive a gcdump against the live target (doing so would kill it). Detection itself is safe
/// (counters/SampleProfiler probe only) and leaves the target running.
/// </para>
/// <para>
/// Gated like the other live AOT suites: Linux-only and a no-op when the AOT toolchain is unavailable
/// (publish failures leave the publish dir unset and the test early-returns), so it never blocks the PR
/// on a missing-toolchain run.
/// </para>
/// </summary>
[Collection("LiveProcess")]
public sealed class LiveNativeAotGcDumpCapabilityTests : IAsyncLifetime
{
    private Process? _sampleProcess;
    private string? _publishDir;

    public async Task InitializeAsync()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var publishDir = Path.Combine(Path.GetTempPath(), $"diagnosticsmcp-nativeaot-gcdumpcap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(publishDir);

        try
        {
            var sampleProject = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "samples", "NativeAotSample", "NativeAotSample.csproj"));
            await PublishAsync(sampleProject, publishDir, CancellationToken.None);
            _publishDir = publishDir;
        }
        catch
        {
            // AOT toolchain not available (e.g. clang/dev libs missing) — leave _publishDir null so the
            // test no-ops instead of failing. CI provides the toolchain and exercises the path.
            try { Directory.Delete(publishDir, recursive: true); } catch { /* best effort */ }
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

    [Fact(Timeout = 180_000)]
    public async Task DetectAsync_OnLinuxNativeAot_ReportsCanCollectGcDumpFalse()
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(_publishDir))
        {
            return;
        }

        var exePath = Path.Combine(_publishDir, "NativeAotSample");
        if (!File.Exists(exePath))
        {
            return;
        }

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
            try { while (await _sampleProcess!.StandardOutput.ReadLineAsync() is not null) { } }
            catch { /* best effort */ }
        });

        await WaitForDiagnosticEndpointAsync(_sampleProcess!.Id, TimeSpan.FromSeconds(45));

        var caps = await new CapabilityDetector().DetectAsync(_sampleProcess.Id, CancellationToken.None);

        caps.Runtime.Should().Be(RuntimeFlavor.NativeAot);
        caps.CanCollectGcDump.Should().BeFalse(
            "gcdump is withheld on NativeAOT — requesting the GCHeapSnapshot keyword crashes the .NET 10 target (issue #471)");

        // Detection must not perturb the target — the gcdump dump path itself is intentionally not driven here.
        _sampleProcess.HasExited.Should().BeFalse("capability detection must leave the target process running");
    }

    private static async Task PublishAsync(string sampleProject, string publishDir, CancellationToken cancellationToken)
    {
        var args = $"publish \"{sampleProject}\" -c Release -r linux-x64 -p:PublishAot=true -o \"{publishDir}\" --self-contained true";
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stderr = await stderrTask.ConfigureAwait(false);
        _ = await stdoutTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"dotnet publish failed for NativeAotSample: {stderr.Trim()}");
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
