using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;

namespace DotnetDiagnostics.TestSupport.OperatingEnvelope;

public sealed class OperatingEnvelopeArtifactWriter
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly List<OperatingEnvelopePairResult> _pairs = [];
    private readonly List<OperatingEnvelopeTrialResult> _trials = [];
    private readonly List<OperatingEnvelopeArtifactHash> _writtenArtifacts = [];
    private bool _quarantined;
    private bool _finalizationStarted;
    private bool _finalized;

    private OperatingEnvelopeArtifactWriter(string runDirectory, OperatingEnvelopeSchedule schedule,
        string configurationHash)
    {
        RunDirectory = runDirectory;
        Schedule = schedule;
        ConfigurationHash = configurationHash;
    }

    public string RunDirectory { get; }
    public OperatingEnvelopeSchedule Schedule { get; }
    public string ConfigurationHash { get; }

    public static async Task<OperatingEnvelopeArtifactWriter> CreateAsync(
        string outputDirectory,
        OperatingEnvelopeSchedule schedule,
        IReadOnlyDictionary<string, string>? inputHashes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(schedule);
        schedule.Configuration.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        var root = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(root);
        var runId = $"operating-envelope-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
        var runDirectory = Path.Combine(root, runId);
        Directory.CreateDirectory(runDirectory);

        var planBytes = JsonSerializer.SerializeToUtf8Bytes(schedule, JsonOptions);
        var configHash = Hash(JsonSerializer.SerializeToUtf8Bytes(schedule.Configuration, JsonOptions));
        var writer = new OperatingEnvelopeArtifactWriter(runDirectory, schedule, configHash);
        await writer.WriteImmutableAsync("plan.json", planBytes, cancellationToken).ConfigureAwait(false);
        var planHash = Hash(planBytes);
        var runManifest = new OperatingEnvelopeRunManifest(
            OperatingEnvelopeProtocol.Version,
            runId,
            DateTimeOffset.UtcNow,
            Environment.OSVersion.ToString(),
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount,
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            await GetDotnetSdkVersionAsync(cancellationToken).ConfigureAwait(false),
            await GetSourceRevisionAsync(cancellationToken).ConfigureAwait(false),
            schedule.Configuration,
            configHash,
            new("plan.json", planHash),
            (inputHashes ?? new Dictionary<string, string>()).OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new OperatingEnvelopeArtifactHash(pair.Key, pair.Value)).ToArray());
        await writer.WriteImmutableJsonAsync("run-manifest.json", runManifest, cancellationToken).ConfigureAwait(false);
        return writer;
    }

    public async Task WritePairAsync(
        OperatingEnvelopePairResult pair,
        IReadOnlyList<OperatingEnvelopeTrialResult> trials,
        CancellationToken cancellationToken = default)
    {
        ThrowIfNotWritable();
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(trials);
        if (_pairs.Any(existing => existing.PairId == pair.PairId))
        {
            throw new InvalidOperationException($"Pair '{pair.PairId}' already has an immutable report.");
        }

        var pairTrials = trials.Where(trial => trial.Plan.PairId == pair.PairId)
            .OrderBy(trial => trial.Plan.OrderInPair)
            .ToArray();
        if (pairTrials.Length != pair.TrialIds.Count)
        {
            throw new ArgumentException("Pair report trial identities do not match the supplied outcomes.", nameof(trials));
        }

        foreach (var trial in pairTrials)
        {
            await WriteImmutableJsonAsync($"trials/{trial.Plan.TrialId}.json", trial, cancellationToken)
                .ConfigureAwait(false);
        }

        await WriteImmutableJsonAsync($"pairs/{pair.PairId}.json", pair, cancellationToken).ConfigureAwait(false);
        await WriteImmutableAsync($"pairs/{pair.PairId}.csv", SerializeCsv(pairTrials, [pair]), cancellationToken)
            .ConfigureAwait(false);
        _pairs.Add(pair);
        _trials.AddRange(pairTrials);
    }

    public async Task WriteFinalManifestAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfNotWritable();
        cancellationToken.ThrowIfCancellationRequested();
        _finalizationStarted = true;
        await WriteImmutableAsync("results.csv", SerializeCsv(_trials, _pairs), cancellationToken, finalArtifact: true)
            .ConfigureAwait(false);
        var manifest = new OperatingEnvelopeResultsManifest(
            OperatingEnvelopeProtocol.Version,
            ConfigurationHash,
            _pairs.Count,
            _trials.Count,
            _pairs.Count(pair => pair.IsValid),
            _pairs.Count(pair => !pair.IsValid),
            _writtenArtifacts.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        await WriteImmutableAsync("results-manifest.json", bytes, cancellationToken, finalArtifact: true)
            .ConfigureAwait(false);
        await WriteImmutableAsync("results-manifest.sha256", Encoding.ASCII.GetBytes(Hash(bytes) + "\n"),
            cancellationToken, finalArtifact: true).ConfigureAwait(false);
        _finalized = true;
    }

    public async Task WriteQuarantineAsync(
        OperatingEnvelopeTrialResult trial,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ThrowIfNotWritable();
        ArgumentNullException.ThrowIfNull(trial);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (_quarantined)
        {
            throw new InvalidOperationException("The run is already quarantined.");
        }

        _quarantined = true;
        var quarantine = new OperatingEnvelopeQuarantineManifest(
            OperatingEnvelopeProtocol.Version,
            trial.Plan.TrialId,
            true,
            trial.Outcome,
            trial.StopOutcome,
            trial.CleanupSucceeded,
            reason,
            DateTimeOffset.UtcNow);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(quarantine, JsonOptions);
        await WriteImmutableAsync("quarantine.json", bytes, cancellationToken).ConfigureAwait(false);
        await WriteImmutableAsync("quarantine.sha256", Encoding.ASCII.GetBytes(Hash(bytes) + "\n"),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteImmutableJsonAsync<T>(string relativePath, T value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        await WriteImmutableAsync(relativePath, bytes, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteImmutableAsync(
        string relativePath, byte[] bytes, CancellationToken cancellationToken, bool finalArtifact = false)
    {
        if (_finalized || (_finalizationStarted && !finalArtifact))
        {
            throw new InvalidOperationException("Runs undergoing or past finalization cannot write additional evidence.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(RunDirectory, relativePath));
        if (!fullPath.StartsWith(RunDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Evidence paths must remain inside the run directory.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await using (var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        var relative = Path.GetRelativePath(RunDirectory, fullPath).Replace(Path.DirectorySeparatorChar, '/');
        _writtenArtifacts.Add(new(relative, Hash(bytes)));
    }

    private void ThrowIfNotWritable()
    {
        if (_finalizationStarted)
        {
            throw new InvalidOperationException("Runs undergoing or past finalization cannot write additional evidence.");
        }

        if (_quarantined)
        {
            throw new InvalidOperationException(
                "Quarantined runs cannot publish pair reports or a results manifest.");
        }
    }

    private static byte[] SerializeCsv(
        IReadOnlyList<OperatingEnvelopeTrialResult> trials,
        IReadOnlyList<OperatingEnvelopePairResult> pairs)
    {
        var pairById = pairs.ToDictionary(pair => pair.PairId, StringComparer.Ordinal);
        var builder = new StringBuilder();
        builder.AppendLine("trial_id,pair_id,pair_valid,pair_invalid_reasons,population,mode,pair_number,order,target_count,outcome,stop_outcome,started_utc,completed_utc,warmup_ms,warmup_requests_json,window_ms,requests_planned,requests_offered,requests_admitted,requests_rejected,requests_unknown,requests_not_offered,request_throughput_per_second,latency_min_ms,latency_median_ms,latency_p95_ms,latency_max_ms,target_requests_json,targets_json,diagnostic_resources_json,artifacts_json,cleanup_ms,cleanup_succeeded,cleanup_errors_json,notes_json,error");
        foreach (var trial in trials.OrderBy(item => item.Plan.TrialId, StringComparer.Ordinal))
        {
            pairById.TryGetValue(trial.Plan.PairId, out var pair);
            var fields = new[]
            {
                trial.Plan.TrialId,
                trial.Plan.PairId,
                pair?.IsValid.ToString() ?? string.Empty,
                JsonSerializer.Serialize(pair?.InvalidReasons ?? [], JsonOptions),
                trial.Plan.Population.ToString(),
                trial.Plan.StorageMode.ToString(),
                trial.Plan.PairNumber.ToString(CultureInfo.InvariantCulture),
                trial.Plan.OrderInPair.ToString(CultureInfo.InvariantCulture),
                trial.Plan.TargetCount.ToString(CultureInfo.InvariantCulture),
                trial.Outcome.ToString(),
                trial.StopOutcome.ToString(),
                trial.StartedUtc.ToString("O", CultureInfo.InvariantCulture),
                trial.CompletedUtc.ToString("O", CultureInfo.InvariantCulture),
                Number(trial.WarmupMilliseconds),
                JsonSerializer.Serialize(trial.WarmupRequests, JsonOptions),
                Number(trial.Requests.MeasurementWindowMilliseconds),
                trial.Requests.Planned.ToString(CultureInfo.InvariantCulture),
                trial.Requests.Offered.ToString(CultureInfo.InvariantCulture),
                trial.Requests.Admitted.ToString(CultureInfo.InvariantCulture),
                trial.Requests.Rejected.ToString(CultureInfo.InvariantCulture),
                trial.Requests.Unknown.ToString(CultureInfo.InvariantCulture),
                trial.Requests.NotOffered.ToString(CultureInfo.InvariantCulture),
                NullableNumber(trial.Requests.ThroughputPerSecond),
                NullableNumber(trial.Requests.LatencyMinimumMilliseconds),
                NullableNumber(trial.Requests.LatencyMedianMilliseconds),
                NullableNumber(trial.Requests.LatencyP95Milliseconds),
                NullableNumber(trial.Requests.LatencyMaximumMilliseconds),
                JsonSerializer.Serialize(trial.TargetRequests, JsonOptions),
                JsonSerializer.Serialize(trial.Targets, JsonOptions),
                JsonSerializer.Serialize(trial.DiagnosticProcess, JsonOptions),
                JsonSerializer.Serialize(trial.Artifacts, JsonOptions),
                Number(trial.CleanupMilliseconds),
                trial.CleanupSucceeded.ToString(),
                JsonSerializer.Serialize(trial.CleanupErrors, JsonOptions),
                JsonSerializer.Serialize(trial.Notes, JsonOptions),
                trial.Error ?? string.Empty,
            };
            builder.AppendLine(string.Join(",", fields.Select(EscapeCsv)));
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(builder.ToString());
    }

    private static string EscapeCsv(string value) =>
        "\"" + value.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string Number(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);
    private static string NullableNumber(double? value) => value is { } number ? Number(number) : string.Empty;

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task<string> GetDotnetSdkVersionAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = FindRepositoryRoot(),
        };
        startInfo.ArgumentList.Add("--version");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start dotnet --version for run provenance.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = (await standardOutput.ConfigureAwait(false)).Trim();
        var error = (await standardError.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            throw new InvalidOperationException(
                $"dotnet --version failed with exit code {process.ExitCode}: {error}");
        }

        return output;
    }

    private static async Task<string> GetSourceRevisionAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = FindRepositoryRoot(),
        };
        startInfo.ArgumentList.Add("rev-parse");
        startInfo.ArgumentList.Add("HEAD");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start git rev-parse for run provenance.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = (await standardOutput.ConfigureAwait(false)).Trim();
        var error = (await standardError.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            throw new InvalidOperationException(
                $"git rev-parse HEAD failed with exit code {process.ExitCode}: {error}");
        }

        return output;
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, ".git")) ||
                Directory.Exists(Path.Combine(current.FullName, ".git")))
            {
                return current.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Git checkout for operating-envelope provenance.");
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

public sealed record OperatingEnvelopeArtifactHash(string Path, string Sha256);

public sealed record OperatingEnvelopeRunManifest(
    string ProtocolVersion,
    string RunId,
    DateTimeOffset CreatedUtc,
    string OperatingSystem,
    string ProcessArchitecture,
    int ProcessorCount,
    string DotnetRuntime,
    string DotnetSdkVersion,
    string SourceRevision,
    OperatingEnvelopeConfiguration Configuration,
    string ConfigurationSha256,
    OperatingEnvelopeArtifactHash Plan,
    IReadOnlyList<OperatingEnvelopeArtifactHash> Inputs);

public sealed record OperatingEnvelopeResultsManifest(
    string ProtocolVersion,
    string ConfigurationSha256,
    int PairCount,
    int TrialCount,
    int ValidPairCount,
    int InvalidPairCount,
    IReadOnlyList<OperatingEnvelopeArtifactHash> Artifacts);

public sealed record OperatingEnvelopeQuarantineManifest(
    string ProtocolVersion,
    string TrialId,
    bool TrialInvalid,
    OperatingEnvelopeTrialOutcome Outcome,
    OperatingEnvelopeStopOutcome StopOutcome,
    bool CleanupSucceeded,
    string Reason,
    DateTimeOffset QuarantinedUtc);
