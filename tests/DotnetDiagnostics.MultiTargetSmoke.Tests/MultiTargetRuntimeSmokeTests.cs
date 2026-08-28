using System.Diagnostics;
using FluentAssertions;

namespace DotnetDiagnostics.MultiTargetSmoke.Tests;

[Collection("LiveProcess")]
public class MultiTargetRuntimeSmokeTests
{
    [Theory]
    [InlineData("net8.0", 8)]
    [InlineData("net9.0", 9)]
    public async Task MultiTargetSmokeHost_ExercisesCoreAndBenchmarkDotNet(string targetFramework, int runtimeMajor)
    {
        if (!InstalledRuntimes.HasMajorVersion(runtimeMajor))
        {
            throw SkipException.ForReason($"Microsoft.NETCore.App {runtimeMajor}.x is not installed on this host; skipping {targetFramework} runtime smoke test.");
        }

        var smokeDll = SampleLocator.LocateMultiTargetSmokeDll(targetFramework)
            ?? throw SkipException.ForReason($"DotnetDiagnostics.MultiTargetSmoke.dll ({targetFramework}) not found. Build tests/DotnetDiagnostics.MultiTargetSmoke for that TFM before running this test.");

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(smokeDll)!,
        };
        psi.ArgumentList.Add(smokeDll);
        psi.Environment["DOTNET_NOLOGO"] = "1";

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start DotnetDiagnostics.MultiTargetSmoke ({targetFramework}).");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            throw new TimeoutException($"DotnetDiagnostics.MultiTargetSmoke ({targetFramework}) did not exit within the timeout.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        process.ExitCode.Should().Be(0, $"stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");
        stdout.Should().Contain($"HostRuntime: .NET {runtimeMajor}");
        stdout.Should().Contain($"CoreTargetRuntime: .NET {runtimeMajor}");
        stdout.Should().Contain("CORE_OK");
        stdout.Should().Contain("BENCHMARK_OK");
        stdout.Should().Contain("SMOKE_OK");
        stderr.Should().BeNullOrWhiteSpace();
    }
}
