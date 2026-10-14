using System.Diagnostics;

namespace DotnetDiagnostics.Core.Tests;

internal static class PrePublishedNativeAotSample
{
    public const string EnvironmentVariable = "DIAG_NATIVEAOT_PUBLISH_DIR";

    private static readonly string[] RequiredFiles = ["NativeAotSample.exe", "NativeAotSample.pdb"];

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DotnetDiagnostics.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not locate the repository root from " + AppContext.BaseDirectory);
    }

    public static async Task PrepareAsync(string destination, TimeSpan publishDeadline)
    {
        if (!TryCopyTo(Environment.GetEnvironmentVariable(EnvironmentVariable), destination))
        {
            await PublishAsync(destination, publishDeadline);
        }
    }

    public static bool TryCopyTo(string? configured, string destination)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return false;
        }

        var source = Path.IsPathRooted(configured) ? configured : Path.GetFullPath(configured, RepositoryRoot());
        foreach (var name in RequiredFiles)
        {
            if (!File.Exists(Path.Combine(source, name)))
            {
                throw new InvalidOperationException(
                    $"{EnvironmentVariable} points at '{source}', which does not contain the required '{name}'.");
            }
        }

        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
        }

        return true;
    }

    private static async Task PublishAsync(string destination, TimeSpan deadline)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
        {
            "publish", Path.Combine(RepositoryRoot(), "samples", "NativeAotSample", "NativeAotSample.csproj"),
            "-c", "Release", "-r", "win-x64", "-p:PublishAot=true", "-o", destination, "--self-contained", "true",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start NativeAOT publish.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(deadline);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            var reaped = true;
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                reaped = false;
            }

            throw new TimeoutException(
                $"NativeAOT sample publish exceeded {deadline}; process reaped: {reaped}."
                + $"{Environment.NewLine}stdout:{Environment.NewLine}{await ReadBoundedAsync(stdout)}"
                + $"{Environment.NewLine}stderr:{Environment.NewLine}{await ReadBoundedAsync(stderr)}");
        }

        var output = await ReadBoundedAsync(stdout);
        var error = await ReadBoundedAsync(stderr);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"NativeAOT sample publish failed with exit code {process.ExitCode}."
                + $"{Environment.NewLine}{output}{Environment.NewLine}{error}");
        }
    }

    private static async Task<string> ReadBoundedAsync(Task<string> read)
    {
        try
        {
            return await read.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            return "<unavailable: output pipe still held open>";
        }
    }
}
