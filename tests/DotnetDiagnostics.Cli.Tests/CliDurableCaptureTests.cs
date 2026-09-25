using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetDiagnostics.Cli;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuEfficiency;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.ProcessDiscovery;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetDiagnostics.Cli.Tests;

public sealed class CliDurableCaptureTests : IDisposable
{
    private readonly string _root = Path.Combine(Environment.CurrentDirectory, ".validation", "cli-captures-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task CpuEfficiencyPersistsTypedAggregateWithoutInventingSnapshotViews()
    {
        using var services = Services();
        var (exit, captured) = await ExecuteAsync(services,
            ["collect", "--kind", "cpu-efficiency", "--persist", "--capture-root", _root, "--json"]);
        exit.Should().Be(0, captured.ToString());
        captured.GetProperty("data").GetProperty("instructionsPerCycle").GetDouble().Should().Be(2);
        var capture = captured.GetProperty("capture");
        var captureId = capture.GetProperty("captureId").GetString()!;
        var artifact = capture.GetProperty("artifacts").EnumerateArray().Single();
        artifact.GetProperty("supportedViews").EnumerateArray().Select(view => view.GetString())
            .Should().NotContain("summary");
        var artifactId = artifact.GetProperty("artifactId").GetString()!;
        var store = new SqliteCaptureStore(new CliCaptureRootProvider(_root));
        using var reader = await store.OpenAsync(captureId, CliCaptureRootProvider.CurrentAccess());
        var snapshot = reader.ReadSnapshot(artifactId);
        snapshot.Should().NotBeNull();
        using var snapshotJson = JsonDocument.Parse(snapshot!.Utf8Json);
        var restored = snapshotJson.RootElement.GetProperty("snapshot").Deserialize<CpuEfficiencySample>(JsonOptions);
        restored!.InstructionsPerCycle.Should().Be(2);
        var (queryExit, query, _) = await HostAsync("query", "--capture-id", captureId,
            "--artifact-id", artifactId, "--view", "summary");
        queryExit.Should().Be(1);
        query.GetProperty("error").GetProperty("kind").GetString().Should().Be("Forbidden");
    }

    [Fact]
    public async Task ExplicitChildScopesPreserveSameKindArtifactIdentityAndExposeReferences()
    {
        using var services = Services();
        var coordinator = CliDurableCaptures.For(services).Get(_root);
        var result = await CliCommands.PersistAsync(services,
            new CliOptions { Command = "collect", Kind = "sweep", Persist = true, CaptureRoot = _root },
            async ct =>
            {
                foreach (var name in new[] { "first-window", "second-window" })
                {
                    var child = await coordinator.RunChildAsync<object>("counters", name, async childToken =>
                    {
                        var captured = await CliCommands.RunAsync(services,
                            new CliOptions { Command = "collect", Kind = "counters" }, childToken);
                        return captured.CaptureProjection!();
                    }, ct);
                    child.IsError.Should().BeFalse();
                }
                return CliCommands.BuildResult(DiagnosticResult.Ok(new { ChildCount = 2 }, "two windows"), static (_, _) => { });
            }, CancellationToken.None);
        result.IsError.Should().BeFalse(result.Human);
        var capture = result.Capture!;
        capture.State.Should().Be(CaptureState.Sealed);
        var root = capture.Artifacts.Single(artifact => artifact.Kind == "sweep");
        var composition = result.CaptureCompositions![root.ArtifactId];
        composition.Children.Should().HaveCount(2);
        composition.Children.Select(child => child.ArtifactId).Should().OnlyHaveUniqueItems();
        composition.Children.Select(child => child.Name).Should().BeEquivalentTo("first-window", "second-window");
        result.CaptureViews![root.ArtifactId].Should().BeEmpty();
        var (showExit, show, _) = await HostAsync("captures", "show", "--capture-id", capture.CaptureId);
        showExit.Should().Be(0, show.ToString());
        var shownRoot = show.GetProperty("capture").GetProperty("artifacts").EnumerateArray()
            .Single(artifact => artifact.GetProperty("artifactId").GetString() == root.ArtifactId);
        shownRoot.GetProperty("composition").GetProperty("children").GetArrayLength().Should().Be(2);
        using var fresh = Services();
        foreach (var child in composition.Children)
        {
            child.ParentArtifactId.Should().Be(root.ArtifactId);
            var (queryExit, query) = await ExecuteAsync(fresh,
                ["query", "--capture-id", capture.CaptureId, "--artifact-id", child.ArtifactId,
                    "--capture-root", _root, "--view", "summary", "--json"]);
            queryExit.Should().Be(0, query.ToString());
        }
    }

    [Fact]
    public async Task OriginalPersistedProducerHandleIsReauthorizedAfterDeletion()
    {
        using var services = Services();
        var (exit, captured) = await ExecuteAsync(services,
            ["collect", "--kind", "counters", "--persist", "--capture-root", _root, "--json"]);
        exit.Should().Be(0, captured.ToString());
        var captureId = captured.GetProperty("capture").GetProperty("captureId").GetString()!;
        var handle = captured.GetProperty("handle").GetString()!;
        var (beforeExit, before) = await ExecuteAsync(services,
            ["query", "--handle", handle, "--view", "summary", "--json"], session: true);
        beforeExit.Should().Be(0, before.ToString());
        var (deleteExit, deleted) = await ExecuteAsync(services,
            ["captures", "delete", "--capture-id", captureId, "--capture-root", _root, "--json"], session: true);
        deleteExit.Should().Be(0, deleted.ToString());
        var (afterExit, after) = await ExecuteAsync(services,
            ["query", "--handle", handle, "--view", "summary", "--json"], session: true);
        afterExit.Should().Be(1);
        after.GetProperty("error").GetProperty("kind").GetString().Should().BeOneOf("Deleted", "NotFound");
    }

    [Fact]
    public async Task SeparateCliProcessQueriesPersistedSnapshotAfterProducerHostDisposal()
    {
        string captureId;
        string artifactId;
        using (var services = Services())
        {
            var (exit, result) = await ExecuteAsync(services,
                ["collect", "--kind", "counters", "--persist", "--capture-root", _root, "--json"]);
            exit.Should().Be(0, result.ToString());
            captureId = result.GetProperty("capture").GetProperty("captureId").GetString()!;
            artifactId = result.GetProperty("capture").GetProperty("artifacts")[0].GetProperty("artifactId").GetString()!;
        }
        var runtimeHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
            ?? Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..",
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var start = new ProcessStartInfo(runtimeHost)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
        {
            typeof(CliHost).Assembly.Location, "query", "--capture-root", _root, "--capture-id", captureId,
            "--artifact-id", artifactId, "--view", "summary", "--json",
        })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        process.ExitCode.Should().Be(0, stderr + stdout);
        using var json = JsonDocument.Parse(stdout);
        json.RootElement.GetProperty("capture").GetProperty("captureId").GetString().Should().Be(captureId);
        json.RootElement.GetProperty("data").ToString().Should().Contain("42");
    }

    [Theory]
    [InlineData(HeapSnapshotOrigin.Live)]
    [InlineData(HeapSnapshotOrigin.Dump)]
    public async Task HistoricalHeapNeverReattachesOnInitialOrReusedHandle(HeapSnapshotOrigin origin)
    {
        using var services = Services();
        var snapshot = new HeapSnapshotArtifact(origin, Environment.ProcessId, DateTimeOffset.UnixEpoch,
            TimeSpan.Zero, new("CoreCLR", "10.0", "x64", false, 1),
            new(0, 0, 0, 0, 0, 0, 0), [], [])
        {
            DumpFilePath = origin == HeapSnapshotOrigin.Dump ? Path.Combine(_root, "never-open-this.dmp") : null,
        };
        var options = new CliOptions { Command = "inspect-heap", Persist = true, CaptureRoot = _root };
        var result = await CliCommands.PersistAsync(services, options, _ =>
        {
            var handle = services.GetRequiredService<IDiagnosticHandleStore>().Register(
                Environment.ProcessId, "heap-snapshot", snapshot, TimeSpan.FromMinutes(10));
            return Task.FromResult(CliCommands.BuildResult(
                DiagnosticResult.OkWithHandle(snapshot, "fixture", handle.Id, handle.ExpiresAt), static (_, _) => { }));
        }, CancellationToken.None);
        result.IsError.Should().BeFalse(result.Human);
        var info = result.Capture!;
        var artifactId = info.Artifacts.Single().ArtifactId;
        var (originalExit, original) = await ExecuteAsync(services,
            ["query", "--handle", result.Handle!, "--view", "object", "--address", "123", "--json"], session: true);
        originalExit.Should().Be(1);
        original.GetProperty("error").GetProperty("kind").GetString().Should().Be("Forbidden");
        var (badExit, bad) = await ExecuteAsync(services,
            ["query", "--capture-id", info.CaptureId, "--artifact-id", artifactId,
                "--capture-root", _root, "--view", "object", "--address", "123", "--json"]);
        badExit.Should().Be(1);
        bad.GetProperty("error").GetProperty("kind").GetString().Should().Be("Forbidden");
        var (goodExit, good) = await ExecuteAsync(services,
            ["query", "--capture-id", info.CaptureId, "--artifact-id", artifactId,
                "--capture-root", _root, "--view", "top-types", "--json"]);
        goodExit.Should().Be(0, good.ToString());
        var latest = services.GetRequiredService<IDiagnosticHandleStore>().TryGetLatestByKind("heap-snapshot")!;
        var (reusedExit, reused) = await ExecuteAsync(services,
            ["query", "--handle", latest.Id, "--view", "gcroot", "--address", "123", "--json"], session: true);
        reusedExit.Should().Be(1);
        reused.GetProperty("error").GetProperty("kind").GetString().Should().Be("Forbidden");
    }

    [Fact]
    public async Task GenericArtifactReadsCannotBypassMarkedCaptureDirectory()
    {
        var (capture, _) = await SeedRecordsAsync();
        var path = Path.Combine(_root, "captures", capture.CaptureId, "manifest.json");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await CliHost.RunAsync(["get-bytes", "--kind", "dump", "--dump-file", path,
                "--out", Path.Combine(_root, "exported.dmp"), "--acknowledge-risk", "critical", "--json"],
            stdout, stderr, CancellationToken.None);
        exit.Should().Be(1, stdout + stderr.ToString());
        stdout.ToString().Should().Contain("managed capture packages").And.Contain("InvalidArtifactPath");
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task DefaultCollectionDoesNotCreateStoreOrChangeEnvelope()
    {
        var collector = new CounterReplay();
        using var services = Services(collector);
        var (exit, json) = await ExecuteAsync(services,
            ["collect", "--kind", "counters", "--capture-root", _root, "--json"]);
        exit.Should().Be(0);
        collector.Calls.Should().Be(1);
        json.TryGetProperty("capture", out _).Should().BeFalse();
        Directory.Exists(_root).Should().BeFalse();
        json.GetProperty("data").GetProperty("counters")[0].GetProperty("value").GetDouble().Should().Be(42);
    }

    [Fact]
    public async Task PersistedCollectionSurvivesNewHostAndTemporaryHandleInvalidation()
    {
        var collector = new CounterReplay();
        string captureId;
        string artifactId;
        string oldHandle;
        using (var services = Services(collector))
        {
            var (exit, json) = await ExecuteAsync(services,
                ["collect", "--kind", "counters", "--persist", "--capture-root", _root, "--json"]);
            exit.Should().Be(0, json.ToString());
            collector.Calls.Should().Be(1);
            json.GetProperty("data").GetProperty("counters")[0].GetProperty("value").GetDouble().Should().Be(42);
            captureId = json.GetProperty("capture").GetProperty("captureId").GetString()!;
            artifactId = json.GetProperty("capture").GetProperty("artifacts")[0].GetProperty("artifactId").GetString()!;
            oldHandle = json.GetProperty("handle").GetString()!;
            json.GetProperty("capture").GetProperty("artifacts")[0].GetProperty("supportedViews")
                .EnumerateArray().Select(view => view.GetString()).Should().BeEquivalentTo("records", "summary", "byProvider");
            json.GetProperty("handleNotice").GetString().Should().Contain("--capture-id");
            json.GetProperty("capture").GetProperty("ownerId").GetString().Should().Be(CliCaptureRootProvider.CurrentAccess().OwnerId);
        }
        using var fresh = Services();
        fresh.GetRequiredService<IDiagnosticHandleStore>().TryGetWithKind(oldHandle).Should().BeNull();
        var (queryExit, queried) = await ExecuteAsync(fresh,
            ["query", "--capture-id", captureId, "--artifact-id", artifactId,
                "--capture-root", _root, "--view", "summary", "--json"]);
        queryExit.Should().Be(0, queried.ToString());
        queried.ToString().Should().Contain("42");
        var latest = fresh.GetRequiredService<IDiagnosticHandleStore>().TryGetLatestByKind("counters")!;
        latest.Origin.Should().Be(HandleOrigin.Imported, "historical provenance must not authorize a live attach");
        var (reusedExit, reused) = await ExecuteAsync(fresh,
            ["query", "--handle", latest.Id, "--view", "byProvider", "--json"], session: true);
        reusedExit.Should().Be(0, reused.ToString());
        await new SqliteCaptureStore(new CliCaptureRootProvider(_root)).DeleteAsync(captureId, CliCaptureRootProvider.CurrentAccess());
        var (deletedExit, deleted) = await ExecuteAsync(fresh,
            ["query", "--handle", latest.Id, "--view", "summary", "--json"], session: true);
        deletedExit.Should().Be(1);
        deleted.GetProperty("error").GetProperty("kind").GetString().Should().BeOneOf("Deleted", "NotFound");
    }

    [Fact]
    public async Task SessionInheritedPersistenceSurvivesExitWithoutUsingScratchRoot()
    {
        using var services = Services(new CounterReplay());
        var scratch = Path.Combine(_root, "scratch");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await SessionRepl.RunAsync(services, new MutableArtifactRootProvider(scratch),
            new StringReader("collect --kind counters --json\nexit\n"), stdout, stderr, null,
            CancellationToken.None, sessionOptions: new CliOptions { Persist = true, CaptureRoot = _root });
        exit.Should().Be(0);
        stderr.ToString().Should().NotContain("ERROR");
        var store = new SqliteCaptureStore(new CliCaptureRootProvider(_root));
        var captures = await store.ListAsync(CliCaptureRootProvider.CurrentAccess());
        captures.Captures.Should().ContainSingle();
        Directory.Exists(Path.Combine(scratch, "captures")).Should().BeFalse();
        var (listExit, json, _) = await HostAsync("captures", "list");
        listExit.Should().Be(0);
        json.GetProperty("data").GetProperty("captures").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task OneShotLifecycleAndTypedRecordPaginationUseStableRootAndCurrentOwner()
    {
        var (capture, artifactId) = await SeedRecordsAsync();
        var (listExit, list, _) = await HostAsync("captures", "list", "--page-size", "1");
        listExit.Should().Be(0, list.ToString());
        list.GetProperty("data").GetProperty("captures")[0].GetProperty("captureId").GetString().Should().Be(capture.CaptureId);
        var (showExit, show, _) = await HostAsync("captures", "show", "--capture-id", capture.CaptureId);
        showExit.Should().Be(0);
        show.GetProperty("data").GetProperty("quality").GetProperty("isIncomplete").GetBoolean().Should().BeTrue();
        var args = new[] { "query", "--capture-id", capture.CaptureId, "--artifact-id", artifactId,
            "--view", "records", "--category", "test", "--name", "value", "--thread-id", "7", "--page-size", "1" };
        var (firstExit, first, _) = await HostAsync(args);
        firstExit.Should().Be(0, first.ToString());
        first.GetProperty("data").GetProperty("records").GetArrayLength().Should().Be(1);
        var after = first.GetProperty("data").GetProperty("nextAfterRecordId").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var (nextExit, next, _) = await HostAsync([.. args, "--after-record-id", after]);
        nextExit.Should().Be(0);
        next.GetProperty("data").GetProperty("records")[0].GetProperty("record").GetProperty("numericValue").GetDouble().Should().Be(2);
        var (deleteExit, _, _) = await HostAsync("captures", "delete", "--capture-id", capture.CaptureId);
        deleteExit.Should().Be(0);
        var (missingExit, missing, _) = await HostAsync(args);
        missingExit.Should().Be(1);
        missing.GetProperty("error").GetProperty("kind").GetString().Should().BeOneOf("Deleted", "NotFound");
    }

    [Fact]
    public async Task DifferentOwnerCannotReadDeleteOrRecover()
    {
        var (capture, artifactId) = await SeedRecordsAsync(new CaptureAccess("another-local-owner"));
        var (listExit, list, _) = await HostAsync("captures", "list");
        listExit.Should().Be(0);
        list.GetProperty("data").GetProperty("captures").GetArrayLength().Should().Be(0);
        foreach (var command in new[]
        {
            new[] { "query", "--capture-id", capture.CaptureId, "--artifact-id", artifactId, "--view", "records" },
            new[] { "captures", "delete", "--capture-id", capture.CaptureId },
            new[] { "captures", "recover", "--capture-id", capture.CaptureId },
        })
        {
            var (exit, json, _) = await HostAsync(command);
            exit.Should().Be(1);
            json.GetProperty("error").GetProperty("kind").GetString().Should().Be("Forbidden");
        }
    }

    [Fact]
    public async Task InterruptedCaptureRequiresExplicitRecoveryIntoNewPackage()
    {
        var (capture, artifactId) = await SeedRecordsAsync(interrupted: true);
        var (queryExit, query, _) = await HostAsync("query", "--capture-id", capture.CaptureId,
            "--artifact-id", artifactId, "--view", "records");
        queryExit.Should().Be(1);
        query.GetProperty("error").GetProperty("kind").GetString().Should().Be("Incomplete");
        var (recoverExit, recovered, _) = await HostAsync("captures", "recover", "--capture-id", capture.CaptureId);
        recoverExit.Should().Be(0, recovered.ToString());
        recovered.GetProperty("data").GetProperty("captureId").GetString().Should().NotBe(capture.CaptureId);
        recovered.GetProperty("data").GetProperty("derivedFrom").GetString().Should().Be(capture.CaptureId);
        Directory.Exists(Path.Combine(_root, "captures", capture.CaptureId)).Should().BeTrue();
    }

    [Theory]
    [InlineData(false, "CorruptPackage")]
    [InlineData(true, "UnsupportedFormat")]
    public async Task CorruptionAndFutureVersionAreErrorsNotEmptyData(bool futureVersion, string expected)
    {
        var (capture, artifactId) = await SeedRecordsAsync();
        var manifest = Path.Combine(_root, "captures", capture.CaptureId, "manifest.json");
        if (futureVersion)
        {
            var node = JsonNode.Parse(await File.ReadAllTextAsync(manifest))!;
            node["PackageVersion"] = 999;
            await File.WriteAllTextAsync(manifest, node.ToJsonString());
        }
        else
        {
            await File.WriteAllTextAsync(manifest, "{invalid");
        }
        var (exit, json, _) = await HostAsync("query", "--capture-id", capture.CaptureId,
            "--artifact-id", artifactId, "--view", "records");
        exit.Should().Be(1);
        json.GetProperty("error").GetProperty("kind").GetString().Should().Be(expected);
    }

    private async Task<(CaptureInfo Capture, string ArtifactId)> SeedRecordsAsync(CaptureAccess? access = null, bool interrupted = false)
    {
        access ??= CliCaptureRootProvider.CurrentAccess();
        var store = new SqliteCaptureStore(new CliCaptureRootProvider(_root));
        var writer = await store.CreateAsync(new("fixture"), access);
        var artifactId = writer.AddArtifact("test-records", "fixture records");
        writer.TryAppend(artifactId, new(DateTimeOffset.UnixEpoch, 7, "test", "value", 1)).Should().BeTrue();
        writer.TryAppend(artifactId, new(DateTimeOffset.UnixEpoch.AddSeconds(1), 7, "test", "value", 2)).Should().BeTrue();
        writer.TryAppend(artifactId, new(NumericValue: double.NaN)).Should().BeFalse();
        if (interrupted)
        {
            await writer.DisposeAsync();
            var page = await store.ListAsync(access);
            return (page.Captures.Single(), artifactId);
        }
        var info = await writer.CompleteAsync();
        await writer.DisposeAsync();
        return (info, artifactId);
    }

    private async Task<(int Exit, JsonElement Json, string Stderr)> HostAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        string[] acknowledgement = args is ["captures", "delete", ..] ? ["--acknowledge-risk", "high"] : [];
        var exit = await CliHost.RunAsync([.. args, .. acknowledgement, "--capture-root", _root, "--json"], stdout, stderr, CancellationToken.None);
        using var json = JsonDocument.Parse(stdout.ToString());
        return (exit, json.RootElement.Clone(), stderr.ToString());
    }

    private static async Task<(int Exit, JsonElement Json)> ExecuteAsync(ServiceProvider services, string[] args, bool session = false)
    {
        var prepared = session
            ? CliCommandExecution.TryPrepareSession(args, null, out var command, out var response)
            : CliCommandExecution.TryPrepareOneShot(args, out command, out response);
        prepared.Should().BeTrue(response?.Text);
        var stdout = new StringWriter();
        var outcome = await CliCommandExecution.ExecuteAsync(services, command!, stdout, new StringWriter(),
            new CliExecutionOptions(session ? CliExecutionContext.Session : CliExecutionContext.OneShot, false, false),
            CancellationToken.None);
        using var json = JsonDocument.Parse(stdout.ToString());
        return (outcome.ExitCode, json.RootElement.Clone());
    }

    private static ServiceProvider Services(CounterReplay? counters = null)
        => new ServiceCollection()
            .AddSingleton<IDiagnosticHandleStore>(new MemoryDiagnosticHandleStore())
            .AddSingleton<IProcessContextResolver>(new Resolver())
            .AddSingleton<ICounterCollector>(counters ?? new CounterReplay())
            .AddSingleton<ICpuEfficiencySampler>(new CpuEfficiencyReplay())
            .BuildServiceProvider();

    private sealed class CpuEfficiencyReplay : ICpuEfficiencySampler
    {
        public bool IsAvailable() => true;

        public Task<CpuEfficiencySample> SampleAsync(int processId, TimeSpan duration, CancellationToken cancellationToken = default)
            => Task.FromResult(new CpuEfficiencySample(processId, DateTimeOffset.UnixEpoch, duration,
                "fixture", Instructions: 200, Cycles: 100, InstructionsPerCycle: 2));
    }

    private sealed class Resolver : IProcessContextResolver
    {
        public Task<ProcessContextResolution> ResolveAsync(int? requestedProcessId, CancellationToken cancellationToken)
            => Task.FromResult(new ProcessContextResolution(
                new ProcessContext(requestedProcessId ?? Environment.ProcessId, RuntimeFlavor.CoreClr, true, true, requestedProcessId is null), null));
    }

    private sealed class CounterReplay : ICounterCollector
    {
        internal int Calls { get; private set; }
        public Task<CounterSnapshot> CollectAsync(int processId, TimeSpan duration,
            IReadOnlyList<string>? providers = null, IReadOnlyList<string>? meters = null,
            int intervalSeconds = 1, int maxInstrumentTimeSeries = 1000, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new CounterSnapshot(processId, DateTimeOffset.UnixEpoch, duration,
                [new("System.Runtime", "cpu-usage", "CPU", 42, CounterKind.Mean, "%")], [], []));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
