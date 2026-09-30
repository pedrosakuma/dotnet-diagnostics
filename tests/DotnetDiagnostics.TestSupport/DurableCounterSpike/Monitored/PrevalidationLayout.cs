using System.Diagnostics;
using System.Text.Json;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.Monitored;

internal static class PrevalidationLayout
{
    // The outcome is in the parent outputs directory; monitoring has one owner.
    internal static readonly string[] WorkerFiles =
    [
        "attempt-start.json", "worker-descriptor.json", "worker-stdout.jsonl",
        "worker-stderr.log", "worker-result.json", "../outcome.json",
    ];
    internal static readonly string[] RecoveryFiles =
    [
        "recovery-worker-descriptor.json", "recovery-worker-stdout.jsonl",
        "recovery-worker-stderr.log",
    ];
    internal static readonly string[] ContextFiles =
    [
        "harness-request.json", "harness-stdout.log", "harness-stderr.log",
        "harness-result.json", "coordinator-monitor.jsonl", "ownership.jsonl", "coverage.json", "cleanup.json",
    ];
    internal static readonly string[] SuiteFiles =
    [
        "suite-start.json", "report.json", "seal.json", "partial.json",
        "admission-monitor.jsonl", "final-monitor.jsonl",
    ];
    internal static readonly string[] InputControlFiles =
    [
        "resolved-manifest.json", "authorization.json", "adoption.json", "implementation-acceptance.json",
        "attribution.json", "encoding.json", "component-evidence.json", "historical-readiness.md", "fixture-manifest.json",
    ];

    internal static int DeriveSuiteIdentityBound()
    {
        var actual = PrevalidationProtocol.Plan().Where(static probe => probe.Ordinal < 8);
        var rooted = actual.Sum(probe => checked(probe.FixtureSlots * 4
            + WorkerFiles.Length + ContextFiles.Length
            + (probe.Workload == "F3" ? RecoveryFiles.Length : 0)
            + (probe.Candidate == "E" ? 0 : MonitoredRunnerGeometry.MaximumActivePackageAndRecoveryFiles)));
        // Geometry's 539 includes its own controls. Suite controls are conservatively
        // counted again here, never subtracted from actual observations.
        return checked(rooted + 539 + 32 + SuiteFiles.Length + InputControlFiles.Length);
    }

    internal static void CopyControlInputs(PrevalidationValidated validated, CancellationToken cancellationToken)
    {
        var manifest = validated.Manifest;
        var sources = new[]
        {
            new MonitoredBinaryIdentity(validated.ManifestPath, validated.ManifestSha256),
            new MonitoredBinaryIdentity(manifest.AuthorizationReceipt, validated.AuthorizationSha256),
            manifest.Adoption, manifest.ImplementationAcceptance, manifest.Attribution, manifest.Encoding,
            manifest.ComponentEvidence, manifest.HistoricalReport, manifest.FixtureManifest,
        };
        var root = Path.Combine(manifest.PrivateRoot, "controls");
        Directory.CreateDirectory(root);
        MonitoredFile.MakePrivateDirectory(root);
        for (var index = 0; index < InputControlFiles.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = MonitoredFile.ReadBounded(sources[index].Path, 1_048_576);
            PrevalidationProtocol.Require(MonitoredFile.HashBytes(bytes) == sources[index].Sha256,
                "PrevalidationControlInputChanged");
            var path = Path.Combine(root, InputControlFiles[index]);
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            MonitoredFile.MakeReadOnly(path);
        }
    }

    internal static string ContextRoot(PrevalidationManifest manifest, PrevalidationProbe probe)
        => Path.Combine(manifest.PrivateRoot, "contexts", $"{probe.Ordinal:D2}-{probe.Id}");

    internal static string ExecutionName(PrevalidationProbe probe)
        => $"{probe.Ordinal:D2}-{probe.Candidate.ToLowerInvariant()}-{probe.Workload.ToLowerInvariant()}";

    internal static string OwnedIdentity(string suiteId, MonitoredExecutionSpec execution,
        MonitoredWorkerMode mode, string kind)
        => $"{suiteId}-{kind}-{execution.Ordinal:D2}-{execution.Candidate.ToLowerInvariant()}-"
            + (mode == MonitoredWorkerMode.Execute ? "source" : "recovered");

    internal static PrevalidationExecutionSettings Settings(PrevalidationManifest manifest, PrevalidationProbe probe)
    {
        var root = ContextRoot(manifest, probe);
        return new(manifest.RuntimeBinary, manifest.ToolBinary, manifest.SampleBinary,
            manifest.SourceCommits, manifest.FixtureManifest.Sha256,
            Path.Combine(root, "history"), Path.Combine(root, "workspace"), Path.Combine(root, "outputs"))
            { SampledLoss = manifest.SampledLoss };
    }

    internal static string ExecutionRoot(PrevalidationManifest manifest, PrevalidationProbe probe)
        => Path.Combine(Settings(manifest, probe).OutputRoot, ExecutionName(probe));

    internal static string HistoryRoot(PrevalidationManifest manifest, PrevalidationProbe probe)
        => Path.Combine(Settings(manifest, probe).HistoryRoot, ExecutionName(probe));

    internal static MonitoredExecutionContext Context(PrevalidationValidated validated, PrevalidationProbe probe,
        MonitoredProcessIdentity coordinator)
    {
        var settings = Settings(validated.Manifest, probe);
        var previous = PrevalidationProtocol.Plan().Where(item => item.Ordinal < probe.Ordinal)
            .Select(item => ContextRoot(validated.Manifest, item)).ToArray();
        var roots = validated.Attribution.Roots.Concat(new[]
        {
            new MonitoredAttributionRoot("history", settings.HistoryRoot, true),
            new MonitoredAttributionRoot("workspace", settings.WorkspaceRoot, true),
            new MonitoredAttributionRoot("outputs", settings.OutputRoot, true),
        }).ToArray();
        return new(settings, validated.RepositoryRoot, validated.ManifestPath, validated.ManifestSha256,
            validated.Manifest.PrivateRoot, validated.Attribution with { Roots = roots },
            validated.Encoding, validated.ComponentEvidence,
            new(previous, coordinator, Path.Combine(ContextRoot(validated.Manifest, probe), "ownership.jsonl"),
                CurrentHistoryRoot: HistoryRoot(validated.Manifest, probe), GeometryFixtures: probe.Ordinal == 8));
    }

    internal static void CreateDirectories(PrevalidationManifest manifest, PrevalidationProbe probe)
    {
        var settings = Settings(manifest, probe);
        foreach (var path in new[] { ContextRoot(manifest, probe), settings.HistoryRoot,
            settings.WorkspaceRoot, settings.OutputRoot, ExecutionRoot(manifest, probe), HistoryRoot(manifest, probe) })
        {
            Directory.CreateDirectory(path);
            MonitoredFile.MakePrivateDirectory(path);
        }
    }

    internal static void CreateHistoryFixtures(string root, int slots, CancellationToken cancellationToken)
        => CreateHistoryFixturesCore(root, slots, null, null, cancellationToken);

    internal static void CreateHistoryFixturesForComponent(string root, int slots,
        Action fixtureWritten, CancellationToken cancellationToken, Action? fixtureClosed = null)
        => CreateHistoryFixturesCore(root, slots, fixtureWritten, fixtureClosed, cancellationToken);

    private static void CreateHistoryFixturesCore(string root, int slots, Action? fixtureWritten,
        Action? fixtureClosed, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw PrevalidationProtocol.Error("PrevalidationLinuxOnly", "Prevalidation inventory fixtures require Linux.");
        }
        PrevalidationProtocol.Require(slots is >= 0 and <= 64, "PrevalidationFixtureSlotLimit");
        var content = new byte[512];
        "synthetic inventory fixture; not a capture package"u8.CopyTo(content);
        for (var slot = 0; slot < slots; slot++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.Combine(root, $"inventory-{slot:D2}");
            PrevalidationProtocol.Require(!Directory.Exists(directory), "PrevalidationFixtureSlotReuse");
            Directory.CreateDirectory(directory);
            MonitoredFile.MakePrivateDirectory(directory);
            for (var file = 0; file < 4; file++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(directory, $"fixture-{file}.bin");
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    stream.Write(content);
                    stream.Flush(flushToDisk: true);
                    fixtureWritten?.Invoke();
                }
                fixtureClosed?.Invoke();
                MonitoredFile.MakeReadOnly(path);
            }
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
    }
}

internal static class PrevalidationOwnership
{
    internal const int MaximumCleanupErrors = 16;
    internal const int MaximumCleanupErrorBytes = 128;
    internal static void RequireRegisteredSelf(string path, MonitoredProcessRole role)
    {
        using var self = Process.GetCurrentProcess();
        var identity = MonitoredProcessIdentity.Capture(self, role);
        PrevalidationProtocol.Require(Read(path).Contains(identity), "PrevalidationUnregisteredInvocation");
    }

    internal static void Append(string path, MonitoredProcessIdentity identity)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(identity);
        PrevalidationProtocol.Require(bytes.Length < 512, "PrevalidationOwnershipRecordLimit");
        using var output = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        PrevalidationProtocol.Require(output.Length <= 4 * 513, "PrevalidationOwnershipCountLimit");
        output.Write([.. bytes, (byte)'\n']);
        output.Flush(flushToDisk: true);
    }

    internal static IReadOnlyList<MonitoredProcessIdentity> Read(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }
        var bytes = MonitoredFile.ReadBounded(path, 5 * 513);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        PrevalidationProtocol.Require(text.EndsWith('\n'), "PrevalidationPartialOwnershipRecord");
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<MonitoredProcessIdentity>(line)
                ?? throw PrevalidationProtocol.Error("PrevalidationOwnershipInvalid", "Invalid process receipt."))
            .ToArray();
    }

    internal static Task<PrevalidationCleanupResult> StopAsync(string path, CancellationToken cancellationToken)
        => StopAsync(Read(path), LinuxPrevalidationProcessOperations.Instance, cancellationToken);

    internal static async Task<PrevalidationCleanupResult> StopAsync(
        IReadOnlyList<MonitoredProcessIdentity> identities,
        IPrevalidationProcessOperations operations, CancellationToken cancellationToken)
    {
        PrevalidationProtocol.Require(identities.Count <= 5, "PrevalidationOwnershipCountLimit");
        var errors = new List<string>();
        var additionalErrors = 0;
        void RecordError(string error)
        {
            PrevalidationProtocol.Require(MonitoredSweepSummaryEncoding.IsBoundedToken(error, MaximumCleanupErrorBytes),
                "PrevalidationCleanupErrorEncodingLimit");
            if (errors.Contains(error, StringComparer.Ordinal)) return;
            if (errors.Count < MaximumCleanupErrors) errors.Add(error);
            else if (additionalErrors < int.MaxValue) additionalErrors++;
        }
        var pending = identities.Distinct().Reverse().ToList();
        // Cancellation limits confirmation, never the signal-all pass.
        foreach (var identity in pending)
        {
            try
            {
                if (operations.IsOriginalAlive(identity))
                {
                    operations.Signal(identity);
                }
            }
            catch (Exception exception) when (IsProcessError(exception))
            {
                RecordError(FormattableString.Invariant($"{identity.ProcessId}:signal:{ErrorCode(exception)}"));
            }
        }
        do
        {
            foreach (var identity in pending.ToArray())
            {
                try
                {
                    if (!operations.IsOriginalAlive(identity))
                    {
                        pending.Remove(identity);
                    }
                }
                catch (Exception exception) when (IsProcessError(exception))
                {
                    RecordError(FormattableString.Invariant($"{identity.ProcessId}:confirm:{ErrorCode(exception)}"));
                }
            }
            if (pending.Count == 0 || cancellationToken.IsCancellationRequested)
            {
                break;
            }
            try
            {
                await operations.DelayAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (IsProcessError(exception))
            {
                RecordError($"confirmation-wait:{ErrorCode(exception)}");
                break;
            }
        } while (true);
        return new("durable-prevalidation-cleanup/2", pending.Count == 0, pending, errors, additionalErrors);
    }

    private static bool IsProcessError(Exception exception) => exception is IOException
        or UnauthorizedAccessException or InvalidOperationException or ArgumentException
        or System.ComponentModel.Win32Exception or DurableStorageExperimentException;

    private static string ErrorCode(Exception exception)
    {
        var code = PrevalidationFailureCodes.Normalize(exception is DurableStorageExperimentException storage
            ? storage.Code : exception.GetType().Name);
        if (exception is IOException)
        {
            return FormattableString.Invariant($"{code}:hr-{exception.HResult:X8}");
        }
        if (exception is UnauthorizedAccessException)
        {
            return exception.InnerException is IOException inner
                ? FormattableString.Invariant($"{code}:hr-{exception.HResult:X8}:iohr-{inner.HResult:X8}")
                : FormattableString.Invariant($"{code}:hr-{exception.HResult:X8}");
        }
        return code;
    }
}

internal sealed class PrevalidationOwnedProcessLedger
{
    private readonly List<MonitoredProcessIdentity> _identities = new(5);
    internal IReadOnlyList<MonitoredProcessIdentity> Identities => _identities;

    internal void Register(string path, MonitoredProcessIdentity identity)
    {
        PrevalidationProtocol.Require(_identities.Count < 5 && !_identities.Contains(identity),
            "PrevalidationOwnershipCountOrDuplicate");
        // Retain ownership even when persistence or monitor admission subsequently fails.
        _identities.Add(identity);
        PrevalidationOwnership.Append(path, identity);
    }
}

internal sealed record PrevalidationCleanupResult(string Schema, bool Quiescent,
    IReadOnlyList<MonitoredProcessIdentity> Unconfirmed, IReadOnlyList<string> Errors, int AdditionalErrorCount = 0);

internal interface IPrevalidationProcessOperations
{
    bool IsOriginalAlive(MonitoredProcessIdentity identity);
    void Signal(MonitoredProcessIdentity identity);
    Task DelayAsync(CancellationToken cancellationToken);
}

internal sealed class LinuxPrevalidationProcessOperations : IPrevalidationProcessOperations
{
    internal static readonly LinuxPrevalidationProcessOperations Instance = new();

    public bool IsOriginalAlive(MonitoredProcessIdentity identity)
        => IsOriginalAliveForComponent(identity,
            static path => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 1), ReadPinnedStat);

    internal static bool IsOriginalAliveForComponent(MonitoredProcessIdentity identity,
        Func<string, Stream> openStat, Func<Stream, string> readStat)
    {
        PrevalidationProtocol.Require(identity.ProcessId > 0, "InvalidProcessIdentity");
        Stream pinned;
        try
        {
            pinned = openStat(FormattableString.Invariant($"/proc/{identity.ProcessId}/stat"));
        }
        catch (Exception exception) when (IsProcStatAbsent(exception))
        {
            return false;
        }
        using var owned = pinned;
        string stat;
        try
        {
            stat = readStat(pinned);
        }
        // Linux proc_single_show returns ESRCH after the pinned inode's task is reaped.
        // .NET Unix IO preserves this raw errno (3) in IOException.HResult, not HRESULT_FROM_WIN32(3).
        catch (IOException exception) when (IsProcStatAbsent(exception))
        {
            return false;
        }
        var close = stat.LastIndexOf(')');
        PrevalidationProtocol.Require(close >= 0 && close + 2 < stat.Length, "InvalidProcessIdentity");
        var fields = stat[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        PrevalidationProtocol.Require(fields.Length > 19
            && ulong.TryParse(fields[19], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out _), "InvalidProcessIdentity");
        var start = ulong.Parse(fields[19], System.Globalization.CultureInfo.InvariantCulture);
        // A reused PID is not ours; a zombie cannot execute or hold writable descriptors.
        return start == identity.LinuxStartTimeTicks && fields[0] is not ("Z" or "X");
    }

    private static bool IsProcStatAbsent(Exception exception)
        => exception is FileNotFoundException or DirectoryNotFoundException
            || exception.GetType() == typeof(IOException) && exception.HResult == 3;

    internal static string ReadPinnedStat(Stream stream)
    {
        Span<byte> bytes = stackalloc byte[4_097];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = stream.Read(bytes[count..]);
            if (read == 0) break;
            count += read;
        }
        PrevalidationProtocol.Require(count <= 4_096, "ProcessIdentityStatWidth");
        return System.Text.Encoding.UTF8.GetString(bytes[..count]);
    }

    public void Signal(MonitoredProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            if (IsOriginalAlive(identity))
            {
                OwnedProcessTerminator.KillExact(process, identity);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            || exception is DurableStorageExperimentException { Code: "OwnedProcessIdentityMismatch" })
        {
            if (IsOriginalAlive(identity))
            {
                throw;
            }
        }
    }

    public Task DelayAsync(CancellationToken cancellationToken)
        => Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
}
