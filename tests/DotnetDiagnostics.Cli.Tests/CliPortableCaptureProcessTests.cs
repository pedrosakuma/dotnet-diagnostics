using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using DotnetDiagnostics.Cli;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

[CollectionDefinition(nameof(CliPortableCaptureProcessTests), DisableParallelization = true)]
public sealed class CliPortableCaptureProcessCollection;

[Collection(nameof(CliPortableCaptureProcessTests))]
public sealed class CliPortableCaptureProcessTests
{
    [Fact]
    public async Task MissingWorkerFailsExplicitlyWithoutReadingInput()
    {
        var root = NewRoot();
        try
        {
            var result = await InvokeAsync(root, "missing-worker", false, "captures", "import",
                "--capture-root", Path.Combine(root, "store"), "--file", Path.Combine(root, "nonexistent.ddcapture"),
                "--acknowledge-risk", "high");
            result.Exit.Should().Be(1, result.Stderr);
            result.Json.GetProperty("data").GetProperty("failure").GetProperty("reason").GetString().Should().Be("ImportWorkerUnavailable");
            result.Json.GetProperty("error").GetProperty("message").GetString().Should().Contain("DOTNET_DIAGNOSTICS_IMPORT_WORKER");
            Directory.Exists(Path.Combine(root, "store")).Should().BeFalse();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [PortableImportFact]
    [Trait("Category", "PortableImportNative")]
    public async Task FreshProcessesImportExportImportThenQueryWithoutSourceAndReplayReceipt()
    {
        // Opt-in discovery skips unconfigured runs; configured runs must execute or fail.
        OperatingSystem.IsLinux().Should().BeTrue("the currently configured confined import worker supports Linux only");
        Environment.GetEnvironmentVariable(CliCommands.ImportWorkerEnvironment).Should().NotBeNullOrWhiteSpace();
        Environment.GetEnvironmentVariable(CliCommands.ImportLibraryEnvironment).Should().NotBeNullOrWhiteSpace();
        var root = NewRoot();
        var success = false;
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "known-counters-v1-v2.ddcapture");
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture))).ToLowerInvariant()
                .Should().Be("bbdc6584acb14da9b21fc2d5170252aab2cf307fbcda1f554a05b6f1938c9ea1");
            var a = Path.Combine(root, "source A");
            var b = Path.Combine(root, "destination B");
            var first = await InvokeAsync(root, "import-a", true, "captures", "import", "--capture-root", a,
                "--file", fixture, "--acknowledge-risk", "high");
            first.Exit.Should().Be(0, first.Json.ToString() + first.Stderr);
            var aEntries = first.Json.GetProperty("data").GetProperty("import").GetProperty("entries").EnumerateArray().ToArray();
            aEntries.Should().HaveCount(2);
            var bundle = Path.Combine(root, "portable bundle.ddcapture");
            var exported = await InvokeAsync(root, "export-a", false, "captures", "export", "--capture-root", a,
                "--entry", aEntries[0].GetProperty("mapping").GetProperty("localCaptureId").GetString() + "=../same label",
                "--entry", aEntries[1].GetProperty("mapping").GetProperty("localCaptureId").GetString() + "=../same label",
                "--file", bundle, "--acknowledge-risk", "high");
            exported.Exit.Should().Be(0, exported.Json.ToString() + exported.Stderr);
            exported.Json.GetProperty("data").GetProperty("outputPublished").GetBoolean().Should().BeTrue();
            var imported = await InvokeAsync(root, "import-b", true, "captures", "import", "--capture-root", b,
                "--file", bundle, "--acknowledge-risk", "high");
            imported.Exit.Should().Be(0, imported.Json.ToString() + imported.Stderr);
            var data = imported.Json.GetProperty("data");
            data.GetProperty("import").GetProperty("complete").GetBoolean().Should().BeTrue();
            var operation = data.GetProperty("operation");
            var replay = await InvokeAsync(root, "retry-b", true, "captures", "import", "--capture-root", b,
                "--file", bundle, "--operation-id", operation.GetProperty("id").GetString()!,
                "--requested-utc", operation.GetProperty("requestedUtc").GetString()!, "--acknowledge-risk", "high");
            replay.Exit.Should().Be(0, replay.Json.ToString() + replay.Stderr);
            replay.Json.GetProperty("data").GetProperty("import").ToString().Should().Be(data.GetProperty("import").ToString());
            Directory.Delete(a, recursive: true);
            File.Delete(bundle);
            var status = await InvokeAsync(root, "result-b", false, "captures", "import-result", "--capture-root", b,
                "--operation-id", operation.GetProperty("id").GetString()!,
                "--requested-utc", operation.GetProperty("requestedUtc").GetString()!);
            status.Exit.Should().Be(0, status.Json.ToString() + status.Stderr);
            status.Json.GetProperty("data").GetProperty("import").ToString().Should().Be(data.GetProperty("import").ToString());
            var entries = data.GetProperty("import").GetProperty("entries").EnumerateArray().ToArray();
            entries.Should().HaveCount(2);
            entries.Select(entry => entry.GetProperty("mapping").GetProperty("localCaptureId").GetString()).Should().OnlyHaveUniqueItems();
            var catalog = await InvokeAsync(root, "catalog-b", false, "captures", "list", "--capture-root", b);
            catalog.Exit.Should().Be(0, catalog.Json.ToString() + catalog.Stderr);
            catalog.Json.GetProperty("data").GetProperty("captures").GetArrayLength().Should().Be(2);
            for (var i = 0; i < entries.Length; i++)
            {
                var mapping = entries[i].GetProperty("mapping");
                mapping.GetProperty("label").GetString().Should().Be("../same label");
                var capture = data.GetProperty("captures")[i];
                capture.GetProperty("ownerId").GetString().Should().Be(CliCaptureRootProvider.CurrentAccess().OwnerId);
                capture.GetProperty("portableSource").GetProperty("origin").GetProperty("ownerId").GetString().Should().Be("original-owner");
                capture.GetProperty("portableSource").GetProperty("origin").GetProperty("captureId").GetString()
                    .Should().Be("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                mapping.GetProperty("localCaptureId").GetString().Should().NotBe(aEntries[i].GetProperty("mapping").GetProperty("localCaptureId").GetString());
                capture.GetProperty("quality").GetProperty("unknownTail").GetBoolean().Should().BeTrue();
                var query = await InvokeAsync(root, "query-b-" + i, false, "query", "--capture-root", b,
                    "--capture-id", mapping.GetProperty("localCaptureId").GetString()!,
                    "--artifact-id", mapping.GetProperty("artifacts")[0].GetProperty("localArtifactId").GetString()!,
                    "--view", "records");
                query.Exit.Should().Be(0, query.Json.ToString() + query.Stderr);
                query.Json.GetProperty("data").GetProperty("records")[0].GetProperty("record").GetProperty("name")
                    .GetString().Should().Be("working-set");
                query.Json.GetProperty("data").GetProperty("records")[0].GetProperty("recordId").GetInt64().Should().Be(41);
            }
            success = true;
        }
        finally
        {
            if (success && Environment.GetEnvironmentVariable("DOTNET_DIAGNOSTICS_PORTABLE_TEST_EVIDENCE") is null)
                Directory.Delete(root, recursive: true);
        }
    }

    private static string NewRoot()
    {
        var parent = Environment.GetEnvironmentVariable("DOTNET_DIAGNOSTICS_PORTABLE_TEST_EVIDENCE") ?? Path.GetTempPath();
        var root = Path.Combine(parent, "cli-portable-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<(int Exit, JsonElement Json, string Stderr)> InvokeAsync(
        string root, string name, bool configuredWorker, params string[] arguments)
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ??
            Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..",
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var start = new ProcessStartInfo(host) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(CliHost).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--json");
        if (!configuredWorker)
        {
            start.Environment.Remove(CliCommands.ImportWorkerEnvironment);
            start.Environment.Remove(CliCommands.ImportLibraryEnvironment);
        }
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            await File.WriteAllTextAsync(Path.Combine(root, name + ".stdout.json"), await stdout);
            await File.WriteAllTextAsync(Path.Combine(root, name + ".stderr.log"), await stderr);
            await File.WriteAllTextAsync(Path.Combine(root, name + ".exit"), process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        using var json = JsonDocument.Parse(await stdout);
        return (process.ExitCode, json.RootElement.Clone(), await stderr);
    }
}

public sealed class PortableImportFactAttribute : FactAttribute
{
    public PortableImportFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_DIAGNOSTICS_PORTABLE_IMPORT_TEST") != "1")
            Skip = "Set DOTNET_DIAGNOSTICS_PORTABLE_IMPORT_TEST=1 and trusted worker/library paths to run the real confined importer.";
    }
}
