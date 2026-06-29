using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Core.ProcessDiscovery;

/// <summary>
/// Best-effort runtime configuration reader for <c>inspect_process(view="runtime-config")</c>.
/// ClrMD-backed GC / ThreadPool details are optional: attach failures turn into notes so the
/// caller can still see filtered environment variables and startup overrides.
/// </summary>
public sealed class RuntimeConfigInspector : IRuntimeConfigInspector
{
    private static readonly string[] AllowlistedEnvironmentPrefixes =
    [
        "DOTNET_",       // Matches DOTNET_*, including DOTNET_SYSTEM_* (intentionally redundant for clarity)
        "COMPlus_",
        "ASPNETCORE_",
        "DOTNET_SYSTEM_", // Redundant with DOTNET_ but kept for documentation clarity
    ];

    private static readonly string[] TieredCompilationVariableNames = ["DOTNET_TieredCompilation", "COMPlus_TieredCompilation"];
    private static readonly string[] QuickJitVariableNames = ["DOTNET_TC_QuickJit", "COMPlus_TC_QuickJit"];
    private static readonly string[] TieredPgoVariableNames = ["DOTNET_TieredPGO", "COMPlus_TieredPGO"];
    private static readonly string[] ConcurrentGcVariableNames = ["DOTNET_gcConcurrent", "COMPlus_gcConcurrent"];
    private static readonly string[] LohCompactionModeVariableNames = ["DOTNET_GCLargeObjectHeapCompactionMode", "COMPlus_GCLargeObjectHeapCompactionMode", "COMPlus_LOHCompactionMode"];

    private readonly ILogger<RuntimeConfigInspector> _logger;
    private readonly string _procRoot;

    public RuntimeConfigInspector(ILogger<RuntimeConfigInspector>? logger = null, string procRoot = "/proc")
    {
        _logger = logger ?? NullLogger<RuntimeConfigInspector>.Instance;
        _procRoot = procRoot;
    }

    public async Task<RuntimeConfigView> InspectAsync(int processId, CancellationToken cancellationToken = default)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId), "Process id must be positive.");
        }

        var notes = new List<string>();
        AddNoteOnce(notes, "Environment variables are filtered to known runtime prefixes (DOTNET_ / COMPlus_ / ASPNETCORE_ / DOTNET_SYSTEM_); all other process env vars are intentionally omitted as a security boundary.");

        var envVars = await ReadEnvironmentVariablesAsync(processId, notes, cancellationToken).ConfigureAwait(false);
        var envMap = envVars
            .GroupBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Last().Value, StringComparer.OrdinalIgnoreCase);

        var tieredCompilation = BuildTieredCompilationConfig(envMap, notes);
        var appContextSwitches = ReadAppContextSwitches(processId, notes);

        GcConfig? gc = null;
        ThreadPoolConfig? threadPool = null;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attemptNotes = new List<string>();
            try
            {
                using var target = DataTarget.AttachToProcess(processId, suspend: true);
                var clrInfo = target.ClrVersions.FirstOrDefault()
                    ?? throw new InvalidOperationException($"Process {processId} does not expose a CLR runtime.");
                using var runtime = clrInfo.CreateRuntime();

                gc = BuildGcConfig(runtime, envMap);
                threadPool = BuildThreadPoolConfig(runtime, attemptNotes, cancellationToken);
                if (threadPool is not null || attempt == 3)
                {
                    foreach (var note in attemptNotes)
                    {
                        AddNoteOnce(notes, note);
                    }

                    break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Runtime config live attach failed for pid {ProcessId} on attempt {Attempt}", processId, attempt + 1);
                if (attempt == 3)
                {
                    if (OperatingSystem.IsLinux())
                    {
                        AddNoteOnce(notes, "GC / ThreadPool info unavailable: ptrace required for ClrMD live attach.");
                    }
                    else
                    {
                        AddNoteOnce(notes, $"GC / ThreadPool info unavailable: live attach failed ({ex.GetType().Name}).");
                    }
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        }

        return new RuntimeConfigView(
            ProcessId: processId,
            Gc: gc,
            ThreadPool: threadPool,
            TieredCompilation: tieredCompilation,
            EnvVars: envVars,
            AppContextSwitches: appContextSwitches,
            Notes: notes);
    }

    internal static IReadOnlyList<EnvVarEntry> FilterAllowlistedEnvironmentEntries(IEnumerable<string> rawEntries)
    {
        ArgumentNullException.ThrowIfNull(rawEntries);

        var filtered = new List<EnvVarEntry>();
        foreach (var rawEntry in rawEntries)
        {
            if (string.IsNullOrWhiteSpace(rawEntry))
            {
                continue;
            }

            var separatorIndex = rawEntry.IndexOf('=');
            if (separatorIndex <= 0)  // Reject both missing '=' (-1) and empty name (0, e.g., "=VALUE")
            {
                continue;
            }

            var name = rawEntry[..separatorIndex];
            if (!IsAllowlistedEnvironmentVariable(name))
            {
                continue;
            }

            filtered.Add(new EnvVarEntry(name, rawEntry[(separatorIndex + 1)..]));
        }

        filtered.Sort(static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
        return filtered;
    }

    internal static bool IsAllowlistedEnvironmentVariable(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        foreach (var prefix in AllowlistedEnvironmentPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool? ParseBooleanOverride(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim() switch
        {
            "1" => true,
            "0" => false,
            var text when text.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
            var text when text.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
            var text when text.Equals("yes", StringComparison.OrdinalIgnoreCase) => true,
            var text when text.Equals("no", StringComparison.OrdinalIgnoreCase) => false,
            var text when text.Equals("on", StringComparison.OrdinalIgnoreCase) => true,
            var text when text.Equals("off", StringComparison.OrdinalIgnoreCase) => false,
            _ => null,
        };
    }

    private static GcConfig BuildGcConfig(ClrRuntime runtime, IReadOnlyDictionary<string, string> envMap)
    {
        var heap = runtime.Heap;
        var hasBackgroundGc = heap.SubHeaps.Any(static subHeap => subHeap.HasBackgroundGC);
        var concurrent = ResolveBooleanOverride(envMap, ConcurrentGcVariableNames) ?? hasBackgroundGc;
        var lohCompactionMode = ResolveTextOverride(envMap, LohCompactionModeVariableNames);

        return new GcConfig(
            IsServerGc: heap.IsServer,
            IsConcurrent: concurrent,
            IsBackground: hasBackgroundGc,
            HeapCount: heap.SubHeaps.Length,
            LargeObjectHeapCompactionMode: lohCompactionMode);
    }

    private static ThreadPoolConfig? BuildThreadPoolConfig(ClrRuntime runtime, List<string> notes, CancellationToken cancellationToken)
    {
        ClrThreadPool? threadPool;
        try
        {
            threadPool = runtime.ThreadPool;
        }
        catch (Exception ex)
        {
            AddNoteOnce(notes, $"ThreadPool settings unavailable: ClrMD ThreadPool probe failed ({ex.GetType().Name}).");
            threadPool = null;
        }

        if (threadPool is not null)
        {
            bool? hillClimbingEnabled = threadPool.UsingPortableThreadPool
                ? true
                : threadPool.UsingWindowsThreadPool
                    ? false
                    : null;

            return new ThreadPoolConfig(
                MinWorkerThreads: threadPool.MinThreads,
                MaxWorkerThreads: threadPool.MaxThreads,
                MinIocpThreads: threadPool.MinCompletionPorts,
                MaxIocpThreads: threadPool.MaxCompletionPorts,
                HillClimbingEnabled: hillClimbingEnabled);
        }

        var portableThreadPool = TryReadStaticObject(runtime, "System.Threading.PortableThreadPool", "ThreadPoolInstance");
        if (portableThreadPool.IsNull || !portableThreadPool.IsValid)
        {
            if (!runtime.Heap.CanWalkHeap)
            {
                AddNoteOnce(notes, "ThreadPool settings unavailable on this attempt: GC heap was not walkable yet (CanWalkHeap=false).");
                return null;
            }

            portableThreadPool = FindSingletonObjectByTypeName(runtime, "System.Threading.PortableThreadPool", cancellationToken);
        }

        if (portableThreadPool.IsNull || !portableThreadPool.IsValid)
        {
            AddNoteOnce(notes, "ThreadPool settings unavailable: neither ClrMD runtime.ThreadPool nor PortableThreadPool runtime internals were readable.");
            return null;
        }

        AddNoteOnce(notes, "ThreadPool settings were reconstructed from PortableThreadPool runtime internals because ClrMD runtime.ThreadPool was unavailable.");
        return new ThreadPoolConfig(
            MinWorkerThreads: portableThreadPool.TryReadField<short>("_minThreads", out var minThreads) ? minThreads : null,
            MaxWorkerThreads: portableThreadPool.TryReadField<short>("_maxThreads", out var maxThreads) ? maxThreads : null,
            MinIocpThreads: portableThreadPool.TryReadField<short>("_legacy_minIOCompletionThreads", out var minIocp) ? minIocp : null,
            MaxIocpThreads: portableThreadPool.TryReadField<short>("_legacy_maxIOCompletionThreads", out var maxIocp) ? maxIocp : null,
            HillClimbingEnabled: true);
    }

    private IReadOnlyList<AppContextSwitchEntry> ReadAppContextSwitches(int processId, List<string> notes)
    {
        var configPath = TryResolveRuntimeConfigPath(processId);
        if (configPath is null)
        {
            AddNoteOnce(notes, "AppContext switches unavailable: could not locate the target's <app>.runtimeconfig.json next to its main module; appContextSwitches is empty.");
            return Array.Empty<AppContextSwitchEntry>();
        }

        try
        {
            var json = File.ReadAllText(configPath);
            var switches = ParseConfigProperties(json);
            if (switches.Count == 0)
            {
                AddNoteOnce(notes, $"No configProperties present in {configPath}; appContextSwitches is empty.");
            }
            else
            {
                AddNoteOnce(notes, $"AppContext switches were read offline from {configPath} (runtimeOptions.configProperties); post-startup AppContext.SetSwitch overrides are not reflected.");
            }

            return switches;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogDebug(ex, "Failed to read runtimeconfig.json at {ConfigPath} for pid {ProcessId}", configPath, processId);
            AddNoteOnce(notes, $"AppContext switches unavailable: failed to read {configPath} ({ex.GetType().Name}); appContextSwitches is empty.");
            return Array.Empty<AppContextSwitchEntry>();
        }
    }

    /// <summary>
    /// Parses <c>runtimeOptions.configProperties</c> (AppContext switches + runtime knobs) from a
    /// <c>runtimeconfig.json</c> document into a stable, name-sorted list. Values are normalised to
    /// strings (booleans lower-cased) so the LLM sees the same form regardless of JSON kind.
    /// </summary>
    internal static IReadOnlyList<AppContextSwitchEntry> ParseConfigProperties(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<AppContextSwitchEntry>();
        }

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("runtimeOptions", out var runtimeOptions)
            || runtimeOptions.ValueKind != JsonValueKind.Object
            || !runtimeOptions.TryGetProperty("configProperties", out var configProperties)
            || configProperties.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<AppContextSwitchEntry>();
        }

        var entries = new List<AppContextSwitchEntry>();
        foreach (var property in configProperties.EnumerateObject())
        {
            entries.Add(new AppContextSwitchEntry(property.Name, NormalizeConfigValue(property.Value)));
        }

        entries.Sort(static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
        return entries;
    }

    private static string? NormalizeConfigValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Null => null,
        JsonValueKind.Number => value.GetRawText(),
        _ => value.GetRawText(),
    };

    private string? TryResolveRuntimeConfigPath(int processId)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
        {
            return null;
        }

        var candidates = new List<string>();

        // Framework-dependent apps: cmdline is "dotnet /path/App.dll …"; runtimeconfig sits beside App.dll.
        foreach (var dll in EnumerateManagedEntrypointDlls(processId))
        {
            candidates.Add(Path.ChangeExtension(dll, ".runtimeconfig.json"));
        }

        // Self-contained apps: the apphost (MainModule) lives beside App.runtimeconfig.json.
        var mainModule = TryGetMainModuleFileName(processId);
        if (!string.IsNullOrWhiteSpace(mainModule))
        {
            var directory = Path.GetDirectoryName(mainModule);
            var baseName = Path.GetFileNameWithoutExtension(mainModule);
            if (!string.IsNullOrWhiteSpace(directory) && !string.IsNullOrWhiteSpace(baseName))
            {
                candidates.Add(Path.Combine(directory, baseName + ".runtimeconfig.json"));
            }
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private IEnumerable<string> EnumerateManagedEntrypointDlls(int processId)
    {
        if (!OperatingSystem.IsLinux())
        {
            yield break;
        }

        var cmdlinePath = Path.Combine(_procRoot, processId.ToString(CultureInfo.InvariantCulture), "cmdline");
        var cwdPath = Path.Combine(_procRoot, processId.ToString(CultureInfo.InvariantCulture), "cwd");
        string? workingDirectory = null;
        try
        {
            workingDirectory = Directory.Exists(cwdPath) ? cwdPath : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Failed to resolve cwd for pid {ProcessId}", processId);
        }

        string[] args;
        try
        {
            if (!File.Exists(cmdlinePath))
            {
                yield break;
            }

            var raw = File.ReadAllText(cmdlinePath);
            args = raw.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Failed to read cmdline for pid {ProcessId}", processId);
            yield break;
        }

        foreach (var arg in args)
        {
            if (!arg.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Path.IsPathRooted(arg))
            {
                yield return arg;
            }
            else if (workingDirectory is not null)
            {
                yield return Path.Combine(workingDirectory, arg);
            }
        }
    }

    private static string? TryGetMainModuleFileName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static TieredCompilationConfig? BuildTieredCompilationConfig(IReadOnlyDictionary<string, string> envMap, List<string> notes)
    {
        var enabled = ResolveBooleanOverride(envMap, TieredCompilationVariableNames);
        var quickJitEnabled = ResolveBooleanOverride(envMap, QuickJitVariableNames);
        var dynamicPgoEnabled = ResolveBooleanOverride(envMap, TieredPgoVariableNames);

        if (enabled is null && quickJitEnabled is null && dynamicPgoEnabled is null)
        {
            AddNoteOnce(notes, "Tiered compilation settings unavailable: no DOTNET_/COMPlus_ tiered-compilation overrides were present, and post-startup runtime state is not introspectable here.");
            return null;
        }

        AddNoteOnce(notes, "Tiered compilation settings are sourced from startup environment overrides because the runtime does not expose a stable post-startup API for these toggles here.");
        return new TieredCompilationConfig(enabled, quickJitEnabled, dynamicPgoEnabled);
    }

    private async Task<IReadOnlyList<EnvVarEntry>> ReadEnvironmentVariablesAsync(int processId, List<string> notes, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsLinux())
        {
            return await ReadLinuxEnvironmentVariablesAsync(processId, notes, cancellationToken).ConfigureAwait(false);
        }

        if (OperatingSystem.IsWindows())
        {
            AddNoteOnce(notes, "Environment variable inspection is not yet implemented on Windows without an in-process probe; envVars is empty.");
            // SECURITY: When implementing Windows support, the raw entries MUST be passed through
            // FilterAllowlistedEnvironmentEntries() before returning to enforce the security boundary.
            return Array.Empty<EnvVarEntry>();
        }

        AddNoteOnce(notes, $"Environment variable inspection is not supported on {RuntimeInformation.OSDescription}; envVars is empty.");
        return Array.Empty<EnvVarEntry>();
    }

    private async Task<IReadOnlyList<EnvVarEntry>> ReadLinuxEnvironmentVariablesAsync(int processId, List<string> notes, CancellationToken cancellationToken)
    {
        var environPath = Path.Combine(_procRoot, processId.ToString(CultureInfo.InvariantCulture), "environ");
        try
        {
            if (!File.Exists(environPath))
            {
                AddNoteOnce(notes, $"Could not read {environPath}; envVars is empty.");
                return Array.Empty<EnvVarEntry>();
            }

            var bytes = await File.ReadAllBytesAsync(environPath, cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                return Array.Empty<EnvVarEntry>();
            }

            var rawEntries = Encoding.UTF8
                .GetString(bytes)
                .Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return FilterAllowlistedEnvironmentEntries(rawEntries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Failed to read /proc environ for pid {ProcessId}", processId);
            AddNoteOnce(notes, $"Could not read {environPath}; envVars is empty.");
            return Array.Empty<EnvVarEntry>();
        }
    }

    private static bool? ResolveBooleanOverride(IReadOnlyDictionary<string, string> envMap, IEnumerable<string> variableNames)
    {
        foreach (var variableName in variableNames)
        {
            if (envMap.TryGetValue(variableName, out var value))
            {
                return ParseBooleanOverride(value);
            }
        }

        return null;
    }

    private static ClrObject TryReadStaticObject(ClrRuntime runtime, string typeName, string fieldName)
    {
        var type = FindTypeByName(runtime, typeName);
        if (type is null)
        {
            return default;
        }

        var field = type.GetStaticFieldByName(fieldName);
        if (field is null || !field.IsObjectReference)
        {
            return default;
        }

        foreach (var domain in runtime.AppDomains)
        {
            try
            {
                if (!field.IsInitialized(domain))
                {
                    continue;
                }

                var value = field.ReadObject(domain);
                if (!value.IsNull && value.IsValid)
                {
                    return value;
                }
            }
            catch
            {
                // best effort: try the next AppDomain
            }
        }

        return default;
    }

    private static ClrType? FindTypeByName(ClrRuntime runtime, string fullName)
    {
        var seen = new HashSet<ulong>();
        foreach (var module in runtime.EnumerateModules())
        {
            foreach (var (methodTable, _) in module.EnumerateTypeDefToMethodTableMap())
            {
                if (!seen.Add(methodTable))
                {
                    continue;
                }

                ClrType? type;
                try
                {
                    type = runtime.GetTypeByMethodTable(methodTable);
                }
                catch
                {
                    continue;
                }

                if (type?.Name == fullName)
                {
                    return type;
                }
            }
        }

        return null;
    }

    private static ClrObject FindSingletonObjectByTypeName(ClrRuntime runtime, string fullName, CancellationToken cancellationToken)
    {
        foreach (var obj in runtime.Heap.EnumerateObjects())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (obj.Type?.Name == fullName)
            {
                return obj;
            }
        }

        return default;
    }

    private static string? ResolveTextOverride(IReadOnlyDictionary<string, string> envMap, IEnumerable<string> variableNames)
    {
        foreach (var variableName in variableNames)
        {
            if (envMap.TryGetValue(variableName, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static void AddNoteOnce(List<string> notes, string note)
    {
        if (!notes.Contains(note, StringComparer.Ordinal))
        {
            notes.Add(note);
        }
    }
}
