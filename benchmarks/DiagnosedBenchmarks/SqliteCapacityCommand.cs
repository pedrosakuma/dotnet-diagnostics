using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DotnetDiagnostics.TestSupport.SqliteCapacity;

namespace DiagnosedBenchmarks;

internal static class SqliteCapacityCommand
{
    // No campaign runner: each invocation owns exactly one child at a time and one fresh case directory.
    internal static int Run(string[] args)
    {
        if (args is ["describe"])
        {
            Console.WriteLine(CapacityProtocol.Configuration);
            Console.WriteLine("sha256:" + CapacityProtocol.Hash);
            return 0;
        }
        if (args is ["worker", var workerRoot, var workerProfile, var workerRate, var workerMode, var records, var workerDeadline])
        {
            var result = CapacityRunner.RunWorker(workerRoot, ParseProfile(workerProfile),
                int.Parse(workerRate, CultureInfo.InvariantCulture), ParseMode(workerMode),
                long.Parse(workerDeadline, CultureInfo.InvariantCulture),
                int.Parse(records, CultureInfo.InvariantCulture));
            return result.Outcome == "failed" ? 1 : 0;
        }
        if (args is ["verify", var verifyRoot, var verifyDeadline])
        {
            var verification = CapacityRunner.VerifyWorker(verifyRoot,
                long.Parse(verifyDeadline, CultureInfo.InvariantCulture));
            CapacityProtocol.WriteReport(Path.Combine(verifyRoot, "verification.json"), verification);
            return 0;
        }
        var component = args.Length > 0 && args[0] == "component";
        if (args.Length != 6 || (!component && args[0] != "cell") || args[5] != CapacityProtocol.Hash)
        {
            Console.Error.WriteLine("EXPERIMENTAL only: describe | component|cell <fresh-case-directory> <profile> <rate> <sqlite|producer-only> <reviewed-protocol-sha256>");
            return 2;
        }
        var profile = ParseProfile(args[2]);
        var rate = int.Parse(args[3], CultureInfo.InvariantCulture);
        var producerOnly = ParseMode(args[4]);
        if (!CapacityProtocol.Rates.Contains(rate) || (component && rate != 1_000))
            throw new ArgumentOutOfRangeException(nameof(args));
        return Supervise(Path.GetFullPath(args[1]), profile, rate, producerOnly, component);
    }

    private static int Supervise(string root, CapacityProfile profile, int rate, bool producerOnly, bool component)
    {
        if (Directory.Exists(root) || File.Exists(root))
            throw new InvalidOperationException("case-directory-must-be-new");
        var workspace = Path.GetDirectoryName(root)!;
        Directory.CreateDirectory(workspace);
        var priorBytes = DirectoryBytes(workspace);
        if (priorBytes > CapacityProtocol.WorkspaceBytes - CapacityProtocol.PackageBytes - 4 * CapacityProtocol.OutputBytes)
            throw new InvalidOperationException("workspace-reservation-cap");
        Directory.CreateDirectory(root);
        var started = Stopwatch.GetTimestamp();
        var deadline = started + CapacityProtocol.CaseSeconds * Stopwatch.Frequency;
        var evidence = new SupervisorEvidence
        {
            ProtocolHash = CapacityProtocol.Hash, PriorWorkspaceBytes = priorBytes,
            Profile = profile.ToString(), Rate = rate, ProducerOnly = producerOnly, ComponentOnly = component,
            DeadlineTimestamp = deadline
        };
        try
        {
            RunChild(["worker", root, profile.ToString(), rate.ToString(CultureInfo.InvariantCulture),
                producerOnly ? "producer-only" : "sqlite", component ? "32" : "0",
                deadline.ToString(CultureInfo.InvariantCulture)], root, deadline, evidence);
            if (!producerOnly)
                RunChild(["verify", root, deadline.ToString(CultureInfo.InvariantCulture)], root, deadline, evidence);
            var result = JsonSerializer.Deserialize<CapacityResult>(File.ReadAllBytes(Path.Combine(root, "worker.json")))!;
            evidence.Outcome = result.Outcome;
            return result.Outcome is "complete-not-production-approval" or "control-complete" ? 0 : 3;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            evidence.Outcome = "stopped-or-failed";
            evidence.Failure = exception.GetType().Name + ":" + exception.Message[..Math.Min(exception.Message.Length, 512)];
            return 1;
        }
        finally
        {
            evidence.ElapsedTicks = Stopwatch.GetTimestamp() - started;
            CapacityProtocol.WriteReport(Path.Combine(root, "supervisor.json"), evidence);
        }
    }

    private static void RunChild(string[] args, string root, long deadline, SupervisorEvidence evidence)
    {
        var start = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath!, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(start.FileName), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("experimental-sqlite-capacity");
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("child-start-failed");
        evidence.OwnedPids.Add(child.Id);
        var output = new OutputBudget();
        var stdout = DrainOutput(child.StandardOutput, output);
        var stderr = DrainOutput(child.StandardError, output);
        using var supervisor = Process.GetCurrentProcess();
        try
        {
            while (!child.HasExited)
            {
                if (Stopwatch.GetTimestamp() >= deadline)
                    throw new TimeoutException("whole-case-deadline");
                if (Volatile.Read(ref output.Characters) > CapacityProtocol.OutputBytes / 4)
                    throw new InvalidOperationException("child-output-cap");
                supervisor.Refresh();
                child.Refresh();
                var childRss = child.WorkingSet64;
                evidence.PeakSampledChildRssBytes = Math.Max(evidence.PeakSampledChildRssBytes, childRss);
                if (args[0] == "worker")
                    evidence.PeakSampledWriterWorkerRssBytes = Math.Max(evidence.PeakSampledWriterWorkerRssBytes, childRss);
                else
                    evidence.PeakSampledVerifierRssBytes = Math.Max(evidence.PeakSampledVerifierRssBytes, childRss);
                evidence.PeakSampledDiagnosticRssBytes = Math.Max(evidence.PeakSampledDiagnosticRssBytes,
                    childRss + supervisor.WorkingSet64);
                if (childRss + supervisor.WorkingSet64 > CapacityProtocol.DiagnosticRssBytes)
                    throw new InvalidOperationException("sampled-diagnostic-rss-cap");
                var bytes = DirectoryBytes(root, evidence);
                evidence.PeakSampledCaseBytes = Math.Max(evidence.PeakSampledCaseBytes, bytes);
                if (bytes > CapacityProtocol.PackageBytes + 4 * CapacityProtocol.OutputBytes)
                    throw new InvalidOperationException("sampled-case-bytes-cap");
                Thread.Sleep(20);
            }
            Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
            evidence.ChildOutputCharacters += output.Characters;
            if (output.Characters > CapacityProtocol.OutputBytes / 4)
                throw new InvalidOperationException("child-output-cap");
            if (child.ExitCode != 0)
                throw new InvalidOperationException("child-exit-" + child.ExitCode.ToString(CultureInfo.InvariantCulture));
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: false);
                evidence.OwnedStopConfirmed = child.WaitForExit(1_000);
            }
            lock (output.Prefix)
                evidence.ChildOutputPrefixes[args[0]] = output.Prefix.ToString();
        }
    }

    private static async Task DrainOutput(StreamReader reader, OutputBudget budget)
    {
        var buffer = new char[1_024];
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        {
            Interlocked.Add(ref budget.Characters, read);
            lock (budget.Prefix)
                budget.Prefix.Append(buffer, 0, Math.Min(read, 2_048 - budget.Prefix.Length));
        }
    }

    private static long DirectoryBytes(string root, SupervisorEvidence? evidence = null)
    {
        long bytes = 0;
        var count = 0;
        // Dedicated experiment workspace only. Never recurse through symlinks into other workspaces.
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var path in Directory.EnumerateFiles(root, "*", options))
        {
            if (++count > 10_000) throw new InvalidOperationException("workspace-file-count-cap");
            try { bytes = checked(bytes + new FileInfo(path).Length); }
            catch (FileNotFoundException) when (evidence is not null)
            {
                evidence.TransientPathMissObservations++;
            }
        }
        return bytes;
    }

    private static CapacityProfile ParseProfile(string value)
        => Enum.TryParse<CapacityProfile>(value, ignoreCase: false, out var profile) && Enum.IsDefined(profile)
            ? profile : throw new ArgumentException("unknown-profile");

    private static bool ParseMode(string value) => value switch
    {
        "producer-only" => true, "sqlite" => false, _ => throw new ArgumentException("unknown-mode")
    };

    private sealed class OutputBudget
    {
        internal long Characters;
        internal StringBuilder Prefix { get; } = new(2_048);
    }

    private sealed class SupervisorEvidence
    {
        public string ProtocolHash { get; set; } = "";
        public string Profile { get; set; } = "";
        public int Rate { get; set; }
        public bool ProducerOnly { get; set; }
        public bool ComponentOnly { get; set; }
        public string Outcome { get; set; } = "incomplete";
        public string? Failure { get; set; }
        public List<int> OwnedPids { get; set; } = [];
        public bool? OwnedStopConfirmed { get; set; }
        public long PriorWorkspaceBytes { get; set; }
        public long PeakSampledCaseBytes { get; set; }
        public long TransientPathMissObservations { get; set; }
        public long PeakSampledChildRssBytes { get; set; }
        public long PeakSampledWriterWorkerRssBytes { get; set; }
        public long PeakSampledVerifierRssBytes { get; set; }
        public long PeakSampledDiagnosticRssBytes { get; set; }
        public long ElapsedTicks { get; set; }
        public long DeadlineTimestamp { get; set; }
        public long ChildOutputCharacters { get; set; }
        public Dictionary<string, string> ChildOutputPrefixes { get; set; } = [];
        public string NativeTransientPeak { get; set; } = "unknown";
        public string MissingWorkerReportPopulations { get; set; } = "unknown, not zero";
    }
}
