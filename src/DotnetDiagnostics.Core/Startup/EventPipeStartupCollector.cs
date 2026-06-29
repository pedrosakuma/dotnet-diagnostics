using System.Diagnostics.Tracing;
using System.Globalization;
using DotnetDiagnostics.Core.Internal;
using DotnetDiagnostics.Core.Launch;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Core.Startup;

public sealed class EventPipeStartupCollector : IStartupCollector
{
    private const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";
    private const string DependencyInjectionProvider = "Microsoft-Extensions-DependencyInjection";
    private const long LoaderKeyword = 0x8;
    private const string UnknownAssembly = "(unknown assembly)";
    private const string UnknownModule = "(unknown module)";

    private static readonly string[] AssemblyNamePayloads =
    [
        "FullyQualifiedAssemblyName",
        "AssemblyName",
        "Name",
    ];

    private static readonly string[] ModulePathPayloads =
    [
        "ModuleILPath",
        "ModuleNativePath",
        "ModulePath",
        "Path",
    ];

    private static readonly string[] ModuleNamePayloads =
    [
        "ModuleName",
        "Name",
    ];

    private static readonly string[] ServiceTypePayloads =
    [
        "serviceType",
        "ServiceType",
    ];

    private readonly ILogger<EventPipeStartupCollector> _logger;

    public EventPipeStartupCollector(ILogger<EventPipeStartupCollector>? logger = null)
    {
        _logger = logger ?? NullLogger<EventPipeStartupCollector>.Instance;
    }

    public async Task<StartupSnapshot> CollectAsync(
        int processId,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
        }

        var client = new DiagnosticsClient(processId);
        return await CollectCoreAsync(client, processId, duration, coldStart: false, resumeAsync: null, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<StartupSnapshot> CollectColdStartAsync(
        SuspendedTarget target,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
        }

        return await CollectCoreAsync(target.Client, target.ProcessId, duration, coldStart: true, target.ResumeAsync, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<StartupSnapshot> CollectCoreAsync(
        DiagnosticsClient client,
        int processId,
        TimeSpan duration,
        bool coldStart,
        Func<ValueTask>? resumeAsync,
        CancellationToken cancellationToken)
    {
        var providers = new[]
        {
            new EventPipeProvider(RuntimeProvider, EventLevel.Verbose, LoaderKeyword),
            new EventPipeProvider(DependencyInjectionProvider, EventLevel.Verbose, (long)EventKeywords.All),
        };

        var session = await client
            .StartEventPipeSessionWithTimeoutAsync(providers, requestRundown: false, circularBufferMB: 128, TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);

        // Cold start: the session is now armed but the target is still suspended. Resume only after the
        // EventStream pipe exists so pre-attach loader/DI events are recorded, not lost in the gap.
        if (resumeAsync is not null)
        {
            await resumeAsync().ConfigureAwait(false);
        }

        var startedAt = DateTimeOffset.UtcNow;
        var notes = new HashSet<string>(StringComparer.Ordinal);
        if (coldStart)
        {
            notes.Add("Cold-start capture: EventPipe was armed on the suspended reverse-connect diagnostic port (DOTNET_DiagnosticPorts ...,suspend) before the runtime resumed, so static constructors, DI container build, module-init exceptions and startup timings are included. This is the only mode that recovers pre-attach events.");
        }
        else
        {
            notes.Add("This startup collector attaches to an already-running process and only captures loader/DI events emitted during this collection window; events before attach, including most initial cold start, are missed. True cold-start capture requires starting EventPipe before or at process launch via a suspended/reverse-connect startup diagnostic port (e.g. DOTNET_DiagnosticPorts with the 'suspend' modifier); attaching after launch — including the CLI --launch child mode, which waits for the diagnostic endpoint before collecting — does not recover pre-attach events. Use --suspend-startup with --launch for true cold-start capture.");
        }

        notes.Add("Static constructor timing is not exposed as a clean EventPipe event by this collector; no static-constructor duration is inferred.");
        notes.Add("JIT-at-startup is covered by collect_events(kind=\"jit\"); startup does not duplicate JIT events.");
        notes.Add("DependencyInjection ServiceProviderBuilt can be replayed when the provider is enabled for already-built providers; observed DI activity duration is the span between captured DI events, not an exact container-build stopwatch.");
        var assemblies = new List<StartupAssemblyLoad>();
        var modules = new List<StartupModuleLoad>();
        var diEvents = new List<StartupDiEvent>();
        var timeline = new List<StartupTimelineEvent>();

        var processingTask = Task.Run(() =>
        {
            try
            {
                using var source = new EventPipeEventSource(session.EventStream);
                source.Dynamic.All += traceEvent =>
                {
                    try
                    {
                        switch (traceEvent.ProviderName)
                        {
                            case RuntimeProvider:
                                HandleRuntimeEvent(traceEvent, assemblies, modules, timeline);
                                break;
                            case DependencyInjectionProvider:
                                HandleDependencyInjectionEvent(traceEvent, diEvents, timeline);
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        notes.Add($"Warning: failed to parse {traceEvent.ProviderName}/{traceEvent.EventName}: {ex.GetType().Name}.");
                    }
                };

                source.Process();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Startup EventPipe source ended for pid {Pid}.", processId);
            }
        }, cancellationToken);

        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { await session.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
            try { await processingTask.ConfigureAwait(false); } catch (Exception) { }
            session.Dispose();
        }

        if (assemblies.Count == 0 && modules.Count == 0)
        {
            notes.Add("No assembly or module load events were observed in the collection window.");
        }

        if (diEvents.Count == 0)
        {
            notes.Add("No Microsoft-Extensions-DependencyInjection events were observed in the collection window.");
        }

        var orderedAssemblies = assemblies
            .OrderBy(static item => item.Timestamp)
            .ThenBy(static item => item.AssemblyName, StringComparer.Ordinal)
            .ToList();
        var orderedModules = modules
            .OrderBy(static item => item.Timestamp)
            .ThenBy(static item => item.ModuleName, StringComparer.Ordinal)
            .ToList();
        var orderedDiEvents = diEvents
            .OrderBy(static item => item.Timestamp)
            .ThenBy(static item => item.EventName, StringComparer.Ordinal)
            .ToList();
        var orderedTimeline = timeline
            .OrderBy(static item => item.Timestamp)
            .ThenBy(static item => item.Category, StringComparer.Ordinal)
            .ThenBy(static item => item.Name, StringComparer.Ordinal)
            .ToList();

        var observedDiActivityDuration = orderedDiEvents.Count > 1
            ? orderedDiEvents[^1].Timestamp - orderedDiEvents[0].Timestamp
            : TimeSpan.Zero;
        if (observedDiActivityDuration < TimeSpan.Zero)
        {
            observedDiActivityDuration = TimeSpan.Zero;
        }

        return new StartupSnapshot(
            ProcessId: processId,
            StartedAt: startedAt,
            Duration: duration,
            TotalAssemblyLoads: orderedAssemblies.Count,
            TotalModuleLoads: orderedModules.Count,
            TotalDiEvents: orderedDiEvents.Count,
            DiServiceProviderBuiltCount: CountEvents(orderedDiEvents, "ServiceProviderBuilt"),
            DiServiceProviderDescriptorsCount: CountEvents(orderedDiEvents, "ServiceProviderDescriptors"),
            DiCallSiteBuiltCount: CountEvents(orderedDiEvents, "CallSiteBuilt"),
            DiServiceResolvedCount: CountEvents(orderedDiEvents, "ServiceResolved") + CountEvents(orderedDiEvents, "ServiceRealized"),
            DiExpressionTreeGeneratedCount: CountEvents(orderedDiEvents, "ExpressionTreeGenerated"),
            DiDynamicMethodBuiltCount: CountEvents(orderedDiEvents, "DynamicMethodBuilt"),
            DiServiceRealizationFailedCount: CountEvents(orderedDiEvents, "ServiceRealizationFailed"),
            ObservedDiActivityDuration: observedDiActivityDuration,
            AssemblyLoads: orderedAssemblies,
            ModuleLoads: orderedModules,
            DiEvents: orderedDiEvents,
            Timeline: orderedTimeline,
            Notes: notes.OrderBy(static note => note, StringComparer.Ordinal).ToList());
    }

    private static void HandleRuntimeEvent(
        TraceEvent traceEvent,
        List<StartupAssemblyLoad> assemblies,
        List<StartupModuleLoad> modules,
        List<StartupTimelineEvent> timeline)
    {
        if (IsAssemblyLoadEvent(traceEvent.EventName))
        {
            var timestamp = ToUtcOffset(traceEvent.TimeStamp);
            var assemblyName = FirstNonEmpty(PayloadString(traceEvent, AssemblyNamePayloads), FirstStringPayload(traceEvent)) ?? UnknownAssembly;
            var assembly = new StartupAssemblyLoad(
                timestamp,
                traceEvent.EventName,
                assemblyName,
                PayloadInt64(traceEvent, "AssemblyID"));
            assemblies.Add(assembly);
            timeline.Add(new StartupTimelineEvent(timestamp, "assembly", traceEvent.EventName, assembly.AssemblyName));
            return;
        }

        if (IsModuleLoadEvent(traceEvent.EventName))
        {
            var timestamp = ToUtcOffset(traceEvent.TimeStamp);
            var modulePath = PayloadString(traceEvent, ModulePathPayloads);
            var moduleName = FirstNonEmpty(
                PayloadString(traceEvent, ModuleNamePayloads),
                string.IsNullOrWhiteSpace(modulePath) ? null : Path.GetFileName(modulePath),
                FirstStringPayload(traceEvent)) ?? UnknownModule;
            var module = new StartupModuleLoad(
                timestamp,
                traceEvent.EventName,
                moduleName,
                modulePath,
                PayloadInt64(traceEvent, "ModuleID"),
                PayloadInt64(traceEvent, "AssemblyID"));
            modules.Add(module);
            timeline.Add(new StartupTimelineEvent(timestamp, "module", traceEvent.EventName, module.ModuleName));
        }
    }

    private static void HandleDependencyInjectionEvent(
        TraceEvent traceEvent,
        List<StartupDiEvent> diEvents,
        List<StartupTimelineEvent> timeline)
    {
        if (!IsDependencyInjectionBuildEvent(traceEvent.EventName))
        {
            return;
        }

        var timestamp = ToUtcOffset(traceEvent.TimeStamp);
        var serviceType = PayloadString(traceEvent, ServiceTypePayloads);
        var serviceProviderHashCode = PayloadInt32(traceEvent, "serviceProviderHashCode") ?? PayloadInt32(traceEvent, "ServiceProviderHashCode");
        var diEvent = new StartupDiEvent(
            timestamp,
            traceEvent.EventName,
            serviceProviderHashCode,
            serviceType,
            PayloadInt32(traceEvent, "singletonServices"),
            PayloadInt32(traceEvent, "scopedServices"),
            PayloadInt32(traceEvent, "transientServices"),
            PayloadInt32(traceEvent, "closedGenericsServices"),
            PayloadInt32(traceEvent, "openGenericsServices"),
            PayloadInt32(traceEvent, "nodeCount"),
            PayloadInt32(traceEvent, "methodSize"),
            PayloadInt32(traceEvent, "chunkIndex"),
            PayloadInt32(traceEvent, "chunkCount"));
        diEvents.Add(diEvent);

        var name = FirstNonEmpty(serviceType, serviceProviderHashCode?.ToString(CultureInfo.InvariantCulture)) ?? traceEvent.EventName;
        timeline.Add(new StartupTimelineEvent(timestamp, "di", traceEvent.EventName, name));
    }

    private static bool IsAssemblyLoadEvent(string eventName) =>
        eventName.Contains("AssemblyLoad", StringComparison.OrdinalIgnoreCase)
        || eventName.Contains("AssemblyDC", StringComparison.OrdinalIgnoreCase);

    private static bool IsModuleLoadEvent(string eventName) =>
        (eventName.Contains("ModuleLoad", StringComparison.OrdinalIgnoreCase)
            || eventName.Contains("ModuleDC", StringComparison.OrdinalIgnoreCase))
        && !eventName.Contains("Method", StringComparison.OrdinalIgnoreCase);

    private static bool IsDependencyInjectionBuildEvent(string eventName) =>
        eventName.Equals("ServiceProviderBuilt", StringComparison.OrdinalIgnoreCase)
        || eventName.Equals("ServiceProviderDescriptors", StringComparison.OrdinalIgnoreCase)
        || eventName.Equals("CallSiteBuilt", StringComparison.OrdinalIgnoreCase)
        || eventName.Equals("ServiceResolved", StringComparison.OrdinalIgnoreCase)
        || eventName.Equals("ServiceRealized", StringComparison.OrdinalIgnoreCase)
        || eventName.Equals("ExpressionTreeGenerated", StringComparison.OrdinalIgnoreCase)
        || eventName.Equals("DynamicMethodBuilt", StringComparison.OrdinalIgnoreCase)
        || eventName.Equals("ServiceRealizationFailed", StringComparison.OrdinalIgnoreCase);

    private static int CountEvents(IReadOnlyList<StartupDiEvent> events, string eventName) =>
        events.Count(item => item.EventName.Equals(eventName, StringComparison.OrdinalIgnoreCase));

    private static DateTimeOffset ToUtcOffset(DateTime timestamp) =>
        new(timestamp.ToUniversalTime(), TimeSpan.Zero);

    private static string? PayloadString(TraceEvent traceEvent, IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            var value = PayloadByName(traceEvent, name);
            var text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
        }

        return null;
    }

    private static string? FirstStringPayload(TraceEvent traceEvent)
    {
        for (var i = 0; i < traceEvent.PayloadNames.Length; i++)
        {
            var text = Convert.ToString(traceEvent.PayloadValue(i), CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
        }

        return null;
    }

    private static int? PayloadInt32(TraceEvent traceEvent, string name)
    {
        var value = PayloadByName(traceEvent, name);
        if (value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (InvalidCastException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static long? PayloadInt64(TraceEvent traceEvent, string name)
    {
        var value = PayloadByName(traceEvent, name);
        if (value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (InvalidCastException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static object? PayloadByName(TraceEvent traceEvent, string name)
    {
        if (!traceEvent.PayloadNames.Contains(name, StringComparer.Ordinal))
        {
            return null;
        }

        return traceEvent.PayloadByName(name);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}
