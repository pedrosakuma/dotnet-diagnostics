using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.Monitored;

internal static class PrevalidationAdmission
{
    // A blocked synchronous filesystem observation must not strand the suite coordinator.
    // This launches our current entry point, never an unvalidated manifest binary.
    internal static async Task<PrevalidationValidated> ValidateAsync(string repositoryRoot, string manifestPath,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(118));
        var executable = Environment.ProcessPath
            ?? throw PrevalidationProtocol.Error("PrevalidationEntryPointMissing", "No current process executable.");
        var entryPoint = Assembly.GetEntryAssembly()?.Location
            ?? throw PrevalidationProtocol.Error("PrevalidationEntryPointMissing", "No current managed entry point.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        if (Path.GetFileNameWithoutExtension(executable) == "dotnet")
        {
            start.ArgumentList.Add(entryPoint);
        }
        foreach (var argument in new[] { "durable-capture-spike", "prevalidation-admission",
            "--repository-root", repositoryRoot, "--manifest", manifestPath })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)
            ?? throw PrevalidationProtocol.Error("PrevalidationAdmissionLaunch", "Admission process launch failed.");
        var identity = MonitoredProcessIdentity.Capture(process, MonitoredProcessRole.Harness);
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();
        var output = CopyBoundedAsync(process.StandardOutput.BaseStream, stdout, 1_048_576, deadline.Token);
        var error = CopyBoundedAsync(process.StandardError.BaseStream, stderr, 65_536, deadline.Token);
        try
        {
            using var self = Process.GetCurrentProcess();
            while (!process.HasExited)
            {
                deadline.Token.ThrowIfCancellationRequested();
                self.Refresh();
                process.Refresh();
                PrevalidationProtocol.Require(self.WorkingSet64 + process.WorkingSet64 <= 268_435_456,
                    "PrevalidationAdmissionHarnessRss");
                if (output.IsFaulted || error.IsFaulted)
                {
                    await Task.WhenAll(output, error).ConfigureAwait(false);
                }
                await Task.Delay(100, deadline.Token).ConfigureAwait(false);
            }
            await Task.WhenAll(output, error).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw PrevalidationProtocol.Error("PrevalidationAdmissionRejected",
                    System.Text.Encoding.UTF8.GetString(stderr.ToArray()));
            }
            return JsonSerializer.Deserialize<PrevalidationValidated>(stdout.ToArray(), PrevalidationProtocol.Json)
                ?? throw PrevalidationProtocol.Error("PrevalidationAdmissionEmpty", "Admission produced no validated context.");
        }
        finally
        {
            if (LinuxProcessIdentity.Matches(identity))
            {
                OwnedProcessTerminator.KillExact(process, identity);
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            deadline.Cancel();
            try
            {
                await Task.WhenAll(output, error).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
            }
        }
    }

    private static async Task CopyBoundedAsync(Stream source, Stream output, int limit, CancellationToken token)
    {
        var buffer = new byte[4_096];
        var total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            PrevalidationProtocol.Require(read <= limit - total, "PrevalidationAdmissionOutputLimit");
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            total += read;
        }
    }
}
