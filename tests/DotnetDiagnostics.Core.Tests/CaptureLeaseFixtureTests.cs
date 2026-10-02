using System.Diagnostics;

namespace DotnetDiagnostics.Core.Tests;

public sealed class CaptureLeaseFixtureTests
{
    [Theory]
    [InlineData("release", 0)]
    [InlineData("invalid", 2)]
    [InlineData(null, 2)]
    public async Task LeaseIsHeldUntilACommandAndReleasedEvenWhenTheCommandIsInvalid(string? command, int exitCode)
    {
        var path = Path.GetTempFileName();
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(typeof(CaptureLeaseFixture).Assembly.Location);
        start.ArgumentList.Add("--capture-lease-fixture");
        start.ArgumentList.Add(path);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Lease fixture did not start.");
        var stderr = child.StandardError.ReadToEndAsync();
        try
        {
            Assert.Equal("ready", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(child.HasExited);
            Assert.Throws<IOException>(() => File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            if (command is not null)
            {
                await child.StandardInput.WriteLineAsync(command);
                await child.StandardInput.FlushAsync();
            }
            child.StandardInput.Close();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var errors = await stderr;
            Assert.Equal(exitCode, child.ExitCode);
            if (exitCode == 0) Assert.Empty(errors);
            else Assert.Contains("Expected release command", errors);
            using var released = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            File.Delete(path);
        }
    }
}
