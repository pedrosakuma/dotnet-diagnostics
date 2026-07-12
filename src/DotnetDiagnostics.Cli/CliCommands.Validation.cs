using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Comparison;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Threads;

namespace DotnetDiagnostics.Cli;

internal static partial class CliCommands
{
    public static bool TryValidateCommand(CliOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        error = null;
        if (!TryValidateWatch(options, out error))
        {
            return false;
        }

        return options.Command switch
        {
            "collect" => TryValidateCollect(options, out error),
            "inspect" => TryValidateInspect(options, out error),
            "inspect-heap" => TryValidateInspectHeap(options, out error),
            "dump" => TryValidateDump(options, out error),
            "get-bytes" => TryValidateGetBytes(options, out error),
            "compare" => TryValidateCompare(options, out error),
            "investigate" => TryValidateInvestigate(options, out error),
            "export-summary" => TryValidateExportSummary(options, out error),
            "completion" => TryValidateCompletion(options, out error),
            _ => true,
        };
    }

    public static bool TryValidateWatch(CliOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        error = null;
        if (options.WatchIntervalSeconds is not { } interval)
        {
            return true;
        }

        if (interval <= 0)
        {
            error = "--watch expects a positive interval in seconds.";
            return false;
        }

        // In threshold-gated capture mode --watch is the metric sample interval (a single bounded
        // run), not the human redraw loop — so the redraw-specific restrictions don't apply.
        if (options.CaptureWhen is not null)
        {
            return true;
        }

        if (options.Json)
        {
            error = "--watch cannot be combined with --json because watch redraws human output.";
            return false;
        }

        if (string.Equals(options.Command, "session", StringComparison.Ordinal)
            || string.Equals(options.Command, "completion", StringComparison.Ordinal))
        {
            error = $"--watch is not supported by '{options.Command}'.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates the <c>collect</c>-specific options before the host is built so usage problems
    /// surface as exit code 2 (not a thrown exception or a runtime error envelope). Returns
    /// <c>true</c> when the options are well-formed; otherwise sets <paramref name="error"/>.
    /// </summary>
    public static bool TryValidateCollect(CliOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        error = null;

        if (string.IsNullOrWhiteSpace(options.Kind))
        {
            error = "The 'collect' command requires --kind <kind>.";
            return false;
        }

        if (!CollectKindSet.Contains(options.Kind))
        {
            error = $"Unknown collect kind '{options.Kind}'. Valid kinds: {string.Join(", ", CollectKinds)}.";
            return false;
        }

        if (options.Kind == "event_source" && options.Providers.Count == 0)
        {
            error = "kind=event_source requires --provider <EventSource name>.";
            return false;
        }

        if (options.Depth is not null && !TryParseDepth(options.Depth, out _))
        {
            error = $"Unknown --depth '{options.Depth}'. Valid values: summary, detail, raw.";
            return false;
        }

        if (options.DurationSeconds is < 1)
        {
            error = "--duration must be >= 1.";
            return false;
        }

        // Threshold-gated capture (#419): --capture-when / --capture / --window form one bounded
        // watch and must be supplied together with kind=counters. Deep validation (predicate parse,
        // ranges) happens in the use case so the error surfaces with recovery hints.
        var gated = options.CaptureWhen is not null || options.CaptureKind is not null || options.WindowSeconds is not null;
        if (gated)
        {
            if (options.Kind != "counters")
            {
                error = "Threshold-gated capture (--capture-when) requires --kind counters.";
                return false;
            }

            if (options.CaptureWhen is null)
            {
                error = "--capture requires --capture-when <predicate> (e.g. --capture-when 'cpu>85').";
                return false;
            }

            if (options.CaptureKind is null)
            {
                error = "--capture-when requires --capture <dump|cpu-sample|heap|thread-snapshot>.";
                return false;
            }

            if (options.WindowSeconds is null)
            {
                error = "Threshold-gated capture requires --window <seconds> (the watch is bounded).";
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(options.NativeAotMapFile))
        {
            var validForCpuCollect = string.Equals(options.Kind, "cpu", StringComparison.Ordinal);
            var validForGatedCpuCapture = string.Equals(options.CaptureKind, "cpu-sample", StringComparison.Ordinal);
            if (!validForCpuCollect && !validForGatedCpuCapture)
            {
                error = options.CaptureKind is null
                    ? "--native-aot-map requires 'collect --kind cpu' or '--capture cpu-sample'."
                    : $"--native-aot-map is not supported by '--capture {options.CaptureKind}'. It is only valid with 'collect --kind cpu' or '--capture cpu-sample'.";
                return false;
            }

            if (!File.Exists(options.NativeAotMapFile))
            {
                error = $"--native-aot-map: file '{options.NativeAotMapFile}' does not exist.";
                return false;
            }
        }

        var isCpu = string.Equals(options.Kind, "cpu", StringComparison.Ordinal);
        var isOffCpu = string.Equals(options.Kind, "off_cpu", StringComparison.Ordinal)
            || string.Equals(options.Kind, "off-cpu", StringComparison.Ordinal);
        var isAllocation = string.Equals(options.Kind, "allocation", StringComparison.Ordinal);
        var isNativeAlloc = string.Equals(options.Kind, "native-alloc", StringComparison.Ordinal);
        var isThreadSnapshot = string.Equals(options.Kind, "thread-snapshot", StringComparison.Ordinal);

        if ((isCpu || isOffCpu || isAllocation || isNativeAlloc) && options.Top is < 1)
        {
            error = "--top must be >= 1.";
            return false;
        }

        if (isNativeAlloc && options.NativeAllocSamplePeriod is < 1)
        {
            error = "--native-alloc-sample-period must be >= 1.";
            return false;
        }

        if (isThreadSnapshot && options.MaxFramesPerThread is < 1)
        {
            error = "--max-frames-per-thread must be >= 1.";
            return false;
        }

        if (isThreadSnapshot && options.MaxFramesPerThread > ClrMdThreadSnapshotInspector.MaxFramesPerThreadHardCap)
        {
            error = $"--max-frames-per-thread must be <= {ClrMdThreadSnapshotInspector.MaxFramesPerThreadHardCap}.";
            return false;
        }

        if (isThreadSnapshot && !string.IsNullOrWhiteSpace(options.DumpFile) && options.HasPid)
        {
            error = "collect --kind thread-snapshot accepts either --pid or --dump-file, not both.";
            return false;
        }

        if (isThreadSnapshot && !string.IsNullOrWhiteSpace(options.DumpFile) && !File.Exists(options.DumpFile))
        {
            error = $"--dump-file: file '{options.DumpFile}' does not exist.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Resolves the effective <c>inspect-heap</c> source: an explicit <c>--source live|dump</c> wins;
    /// otherwise it is inferred (presence of <c>--dump-file</c> ⇒ dump, else live). Also enforces the
    /// live/dump mutual-exclusion rules. Returns <c>true</c> with <paramref name="source"/> set, or
    /// <c>false</c> with <paramref name="error"/>.
    /// </summary>
    public static bool TryResolveHeapSource(CliOptions options, out string source, out string? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        source = string.Empty;
        error = null;

        if (options.Sources.Count > 1)
        {
            error = "inspect-heap accepts a single --source (live, dump or gcdump).";
            return false;
        }

        if (options.Sources.Count == 1)
        {
            source = options.Sources[0];
            if (!HeapSourceSet.Contains(source))
            {
                error = $"Unknown --source '{source}'. Valid values: live, dump, gcdump.";
                return false;
            }
        }
        else
        {
            // Infer from the presence of a dump file so the common cases need no --source.
            source = options.DumpFile is not null ? "dump" : "live";
        }

        if (source == "dump")
        {
            if (string.IsNullOrWhiteSpace(options.DumpFile))
            {
                error = "inspect-heap --source dump requires --dump-file <path>.";
                return false;
            }

            if (options.HasPid)
            {
                error = "inspect-heap --source dump does not accept --pid (the dump is offline).";
                return false;
            }
        }
        else if (options.DumpFile is not null)
        {
            error = "inspect-heap --source live does not accept --dump-file (use --source dump).";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates the opt-in <c>--launch</c> dev mode (issue #365), independent of the per-command
    /// option checks. Enforces that the <c>--</c> launch argv and the <c>--launch</c> flag are used
    /// together, that <c>--launch</c> is mutually exclusive with <c>--pid</c> (the CLI supplies the
    /// child's pid), and that the command actually targets a live process the child relationship can
    /// unblock. Commands that pass no <c>--launch</c> and no <c>--</c> argv are accepted unchanged.
    /// </summary>
    public static bool TryValidateLaunch(CliOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        error = null;

        if (!options.Launch)
        {
            if (options.LaunchArgs.Count > 0)
            {
                error = "Launch arguments after '--' require --launch (e.g. --launch -- dotnet App.dll).";
                return false;
            }

            if (options.SuspendStartup)
            {
                error = "--suspend-startup requires --launch (cold-start capture launches the target suspended).";
                return false;
            }

            return true;
        }

        if (options.LaunchArgs.Count == 0)
        {
            error = "--launch requires a program after '--', e.g. --launch -- dotnet App.dll.";
            return false;
        }

        if (options.HasPid)
        {
            error = "--launch cannot be combined with --pid: the CLI launches the target and binds its pid.";
            return false;
        }

        if (!LaunchableCommandSet.Contains(options.Command!))
        {
            error = $"--launch is not supported by '{options.Command}'. Supported: {string.Join(", ", LaunchableCommands)}.";
            return false;
        }

        if (options.Command == "inspect-heap"
            && (options.DumpFile is not null || options.Sources.Contains("dump", StringComparer.Ordinal)))
        {
            error = "--launch applies to a live target; it cannot be combined with inspect-heap --source dump.";
            return false;
        }

        if (options.Command == "get-bytes"
            && (string.Equals(options.Kind, "dump", StringComparison.Ordinal)
                || string.Equals(options.Kind, "trace", StringComparison.Ordinal)))
        {
            error = $"--launch applies to a live target; it cannot be combined with get-bytes --kind {options.Kind}.";
            return false;
        }

        if (options.SuspendStartup
            && !(options.Command == "collect" && options.Kind == "startup"))
        {
            error = "--suspend-startup applies only to 'collect --kind startup'.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates the <c>inspect</c>-specific options before the host is built. Returns
    /// <c>true</c> when well-formed; otherwise sets <paramref name="error"/>.
    /// </summary>
    public static bool TryValidateInspect(CliOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        error = null;

        if (string.IsNullOrWhiteSpace(options.View))
        {
            error = $"The 'inspect' command requires --view <view>. Valid views: {string.Join(", ", InspectViews)}.";
            return false;
        }

        if (!InspectViewSet.Contains(options.View))
        {
            error = $"Unknown --view '{options.View}'. Valid views: {string.Join(", ", InspectViews)}.";
            return false;
        }

        if (options.View == "triage" && options.DurationSeconds is < 1)
        {
            error = "--duration must be >= 1 for 'inspect --view triage'.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates the <c>inspect-heap</c>-specific options before the host is built. Returns
    /// <c>true</c> when well-formed; otherwise sets <paramref name="error"/>.
    /// </summary>
    public static bool TryValidateInspectHeap(CliOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        return TryResolveHeapSource(options, out _, out error);
    }

    /// <summary>
    /// Validates the <c>dump</c>-specific options before the host is built. Returns <c>true</c> when
    /// well-formed; otherwise sets <paramref name="error"/>.
    /// </summary>
    public static bool TryValidateDump(CliOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        error = null;

        if (options.DumpType is not null && !TryParseDumpType(options.DumpType, out _))
        {
            error = $"Unknown --dump-type '{options.DumpType}'. Valid values: {string.Join(", ", DumpTypes)}.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates the <c>get-bytes</c>-specific options before the host is built. Returns <c>true</c>
    /// when well-formed; otherwise sets <paramref name="error"/>. <c>get-bytes</c> always materialises
    /// the artifact to a file, so <c>--out &lt;file&gt;</c> is mandatory.
    /// </summary>
    public static bool TryValidateGetBytes(CliOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        error = null;

        if (string.IsNullOrWhiteSpace(options.Kind))
        {
            error = $"The 'get-bytes' command requires --kind <{string.Join("|", ByteKinds)}>.";
            return false;
        }

        if (!ByteKindSet.Contains(options.Kind))
        {
            error = $"Unknown get-bytes kind '{options.Kind}'. Valid kinds: {string.Join(", ", ByteKinds)}.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(options.OutDir))
        {
            error = "The 'get-bytes' command requires --out <file> (the destination the artifact is written to).";
            return false;
        }

        if (options.Kind == "module")
        {
            if (string.IsNullOrWhiteSpace(options.Mvid))
            {
                error = "get-bytes --kind module requires --mvid <module-version-id>.";
                return false;
            }

            if (!Guid.TryParse(options.Mvid, out _))
            {
                error = $"--mvid '{options.Mvid}' is not a valid GUID.";
                return false;
            }

            if (options.Asset is not null && !ByteAssetSet.Contains(options.Asset))
            {
                error = $"Unknown --asset '{options.Asset}'. Valid values: {string.Join(", ", ByteAssets)}.";
                return false;
            }

            if (options.DumpFile is not null)
            {
                error = "get-bytes --kind module does not accept --dump-file (that is for --kind dump).";
                return false;
            }
        }
        else if (options.Kind == "trace")
        {
            if (string.IsNullOrWhiteSpace(options.DumpFile))
            {
                error = "get-bytes --kind trace requires --dump-file <path> (the exported .nettrace).";
                return false;
            }

            if (options.HasPid)
            {
                error = "get-bytes --kind trace does not accept --pid (the trace is offline).";
                return false;
            }
        }
        else
        {
            // kind == dump
            if (string.IsNullOrWhiteSpace(options.DumpFile))
            {
                error = "get-bytes --kind dump requires --dump-file <path>.";
                return false;
            }

            if (options.HasPid)
            {
                error = "get-bytes --kind dump does not accept --pid (the dump is offline).";
                return false;
            }
        }

        return true;
    }

    public static bool TryValidateCompare(CliOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        error = null;

        if (options.ComparePaths.Count < 2)
        {
            error = "The 'compare' command requires at least two snapshot JSON paths.";
            return false;
        }

        if (!JourneyModeParser.TryParse(options.Mode, out _))
        {
            error = $"Unknown --mode '{options.Mode}'. Valid values: trend, dispersion.";
            return false;
        }

        return true;
    }

    public static bool TryValidateInvestigate(CliOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        error = null;

        if (options.MaxToolCalls is < 1)
        {
            error = "--max-tool-calls must be >= 1.";
            return false;
        }

        // Cold mode (no --hypothesis) has nothing to anchor the plan on without a stated symptom;
        // the planner would silently default to a generic route. Require one so the plan is meaningful.
        if (string.IsNullOrWhiteSpace(options.Hypothesis) && string.IsNullOrWhiteSpace(options.Symptom))
        {
            error = "The 'investigate' command requires --symptom <text> (or --hypothesis <text>) so the plan can be anchored to what you observed.";
            return false;
        }

        return true;
    }

    public static bool TryValidateExportSummary(CliOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        error = null;

        if (string.IsNullOrWhiteSpace(options.Handle))
        {
            error = "The 'export-summary' command requires --handle <id> (a CPU-sample handle from 'collect --kind cpu').";
            return false;
        }

        if (options.TopHotspots is < 1)
        {
            error = "--top-hotspots must be >= 1.";
            return false;
        }

        return true;
    }

    public static bool TryValidateCompletion(CliOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        error = null;

        if (string.IsNullOrWhiteSpace(options.CompletionShell))
        {
            error = $"The 'completion' command requires a shell argument. Valid shells: {string.Join(", ", CliCompletionScripts.Shells)}.";
            return false;
        }

        if (!CliCompletionScripts.ShellSet.Contains(options.CompletionShell))
        {
            error = $"Unknown completion shell '{options.CompletionShell}'. Valid shells: {string.Join(", ", CliCompletionScripts.Shells)}.";
            return false;
        }

        return true;
    }

}
