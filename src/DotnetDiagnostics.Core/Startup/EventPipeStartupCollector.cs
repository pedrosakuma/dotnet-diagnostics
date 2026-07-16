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
    internal const int MaxRetainedAssemblyLoads = 1_000;
    internal const int MaxRetainedModuleLoads = 1_000;
    internal const int MaxRetainedDiEvents = 1_000;
    internal const int MaxRetainedTimelineEvents = 2_000;
    private const string UnknownAssembly = "(unknown assembly)";
    private const string UnknownModule = "(unknown module)";
    private static readonly TimeSpan ProcessingDrainBudget = TimeSpan.FromSeconds(5);

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
        // EventStream pipe exists so pre-attach loader/DI events are recorded, not lost in the gap. If
        // resume throws, dispose the just-created session so it is not leaked.
        if (resumeAsync is not null)
        {
            try
            {
                await resumeAsync().ConfigureAwait(false);
            }
            catch
            {
                await EventPipeSessionShutdown.StopSessionAsync(
                    session,
                    ex => _logger.LogDebug(ex, "Stopping startup EventPipe session after resume failure for pid {Pid} failed.", processId))
                    .ConfigureAwait(false);
                session.Dispose();
                throw;
            }
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
        var capture = new StartupCaptureBuffer();
        var sync = new object();

        var processingTask = Task.Run(() =>
        {
            try
            {
                using var source = new EventPipeEventSource(session.EventStream);
                source.Dynamic.All += traceEvent =>
                {
                    lock (sync)
                    {
                        try
                        {
                            switch (traceEvent.ProviderName)
                            {
                                case RuntimeProvider:
                                    HandleRuntimeEvent(traceEvent, capture);
                                    break;
                                case DependencyInjectionProvider:
                                    HandleDependencyInjectionEvent(traceEvent, capture);
                                    break;
                            }
                        }
                        catch (Exception ex)
                        {
                            notes.Add($"Warning: failed to parse {traceEvent.ProviderName}/{traceEvent.EventName}: {ex.GetType().Name}.");
                        }
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
            await EventPipeSessionShutdown.StopAndDrainAsync(
                session,
                processingTask,
                ex => _logger.LogDebug(ex, "Stopping startup EventPipe session for pid {Pid} failed.", processId),
                ProcessingDrainBudget).ConfigureAwait(false);
        }

        List<StartupAssemblyLoad> orderedAssemblies;
        List<StartupModuleLoad> orderedModules;
        List<StartupDiEvent> orderedDiEvents;
        List<StartupTimelineEvent> orderedTimeline;
        List<StartupLoadAggregate> assemblyAggregates;
        List<StartupLoadAggregate> moduleAggregates;
        List<string> snapshotNotes;
        int totalAssemblyLoads;
        int totalModuleLoads;
        int totalTimelineEvents;
        int totalDiEvents;
        int diServiceProviderBuiltCount;
        int diServiceProviderDescriptorsCount;
        int diCallSiteBuiltCount;
        int diServiceResolvedCount;
        int diExpressionTreeGeneratedCount;
        int diDynamicMethodBuiltCount;
        int diServiceRealizationFailedCount;
        bool truncated;
        TimeSpan observedDiActivityDuration;
        lock (sync)
        {
            totalAssemblyLoads = capture.TotalAssemblyLoads;
            totalModuleLoads = capture.TotalModuleLoads;
            totalTimelineEvents = capture.TotalTimelineEvents;
            totalDiEvents = capture.TotalDiEvents;
            diServiceProviderBuiltCount = capture.DiServiceProviderBuiltCount;
            diServiceProviderDescriptorsCount = capture.DiServiceProviderDescriptorsCount;
            diCallSiteBuiltCount = capture.DiCallSiteBuiltCount;
            diServiceResolvedCount = capture.DiServiceResolvedCount;
            diExpressionTreeGeneratedCount = capture.DiExpressionTreeGeneratedCount;
            diDynamicMethodBuiltCount = capture.DiDynamicMethodBuiltCount;
            diServiceRealizationFailedCount = capture.DiServiceRealizationFailedCount;
            truncated = capture.Truncated;
            observedDiActivityDuration = capture.ObservedDiActivityDuration;
            orderedAssemblies = capture.AssemblyLoads
                .OrderBy(static item => item.Timestamp)
                .ThenBy(static item => item.AssemblyName, StringComparer.Ordinal)
                .ToList();
            orderedModules = capture.ModuleLoads
                .OrderBy(static item => item.Timestamp)
                .ThenBy(static item => item.ModuleName, StringComparer.Ordinal)
                .ToList();
            orderedDiEvents = capture.DiEvents
                .OrderBy(static item => item.Timestamp)
                .ThenBy(static item => item.EventName, StringComparer.Ordinal)
                .ToList();
            orderedTimeline = capture.Timeline
                .OrderBy(static item => item.Timestamp)
                .ThenBy(static item => item.Category, StringComparer.Ordinal)
                .ThenBy(static item => item.Name, StringComparer.Ordinal)
                .ToList();
            assemblyAggregates = capture.BuildAssemblyAggregates();
            moduleAggregates = capture.BuildModuleAggregates();
            snapshotNotes = notes.ToList();
        }

        if (totalAssemblyLoads == 0 && totalModuleLoads == 0)
        {
            snapshotNotes.Add("No assembly or module load events were observed in the collection window.");
        }

        if (totalDiEvents == 0)
        {
            snapshotNotes.Add("No Microsoft-Extensions-DependencyInjection events were observed in the collection window.");
        }

        if (truncated)
        {
            snapshotNotes.Add(
                $"Startup capture hit retention caps (assemblies={MaxRetainedAssemblyLoads}, modules={MaxRetainedModuleLoads}, di={MaxRetainedDiEvents}, timeline={MaxRetainedTimelineEvents}). Totals remain exact, but retained event lists/timeline are bounded samples.");
        }
        if (observedDiActivityDuration < TimeSpan.Zero)
        {
            observedDiActivityDuration = TimeSpan.Zero;
        }

        return new StartupSnapshot(
            ProcessId: processId,
            StartedAt: startedAt,
            Duration: duration,
            TotalAssemblyLoads: totalAssemblyLoads,
            TotalModuleLoads: totalModuleLoads,
            TotalTimelineEvents: totalTimelineEvents,
            TotalDiEvents: totalDiEvents,
            DiServiceProviderBuiltCount: diServiceProviderBuiltCount,
            DiServiceProviderDescriptorsCount: diServiceProviderDescriptorsCount,
            DiCallSiteBuiltCount: diCallSiteBuiltCount,
            DiServiceResolvedCount: diServiceResolvedCount,
            DiExpressionTreeGeneratedCount: diExpressionTreeGeneratedCount,
            DiDynamicMethodBuiltCount: diDynamicMethodBuiltCount,
            DiServiceRealizationFailedCount: diServiceRealizationFailedCount,
            ObservedDiActivityDuration: observedDiActivityDuration,
            AssemblyLoads: orderedAssemblies,
            AssemblyAggregates: assemblyAggregates,
            ModuleLoads: orderedModules,
            ModuleAggregates: moduleAggregates,
            DiEvents: orderedDiEvents,
            Timeline: orderedTimeline,
            Truncated: truncated,
            Notes: snapshotNotes.OrderBy(static note => note, StringComparer.Ordinal).ToList());
    }

    private static void HandleRuntimeEvent(
        TraceEvent traceEvent,
        StartupCaptureBuffer capture)
    {
        if (IsAssemblyLoadEvent(traceEvent.EventName))
        {
            var timestamp = ToUtcOffset(traceEvent.TimeStamp);
            var assemblyName = FirstNonEmpty(PayloadString(traceEvent, AssemblyNamePayloads), FirstStringPayload(traceEvent)) ?? UnknownAssembly;
            capture.AddAssembly(new StartupAssemblyLoad(
                timestamp,
                traceEvent.EventName,
                assemblyName,
                PayloadInt64(traceEvent, "AssemblyID")));
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
            capture.AddModule(new StartupModuleLoad(
                timestamp,
                traceEvent.EventName,
                moduleName,
                modulePath,
                PayloadInt64(traceEvent, "ModuleID"),
                PayloadInt64(traceEvent, "AssemblyID")));
        }
    }

    private static void HandleDependencyInjectionEvent(
        TraceEvent traceEvent,
        StartupCaptureBuffer capture)
    {
        if (!IsDependencyInjectionBuildEvent(traceEvent.EventName))
        {
            return;
        }

        var timestamp = ToUtcOffset(traceEvent.TimeStamp);
        var serviceType = PayloadString(traceEvent, ServiceTypePayloads);
        var serviceProviderHashCode = PayloadInt32(traceEvent, "serviceProviderHashCode") ?? PayloadInt32(traceEvent, "ServiceProviderHashCode");
        capture.AddDiEvent(new StartupDiEvent(
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
            PayloadInt32(traceEvent, "chunkCount")));
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

    internal sealed class StartupCaptureBuffer
    {
        private readonly Dictionary<string, RawLoadAggregate> _assembliesByName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RawLoadAggregate> _modulesByName = new(StringComparer.Ordinal);
        private DateTimeOffset? _firstDiEventAt;
        private DateTimeOffset? _lastDiEventAt;

        public List<StartupAssemblyLoad> AssemblyLoads { get; } = new(capacity: MaxRetainedAssemblyLoads);
        public List<StartupModuleLoad> ModuleLoads { get; } = new(capacity: MaxRetainedModuleLoads);
        public List<StartupDiEvent> DiEvents { get; } = new(capacity: MaxRetainedDiEvents);
        public List<StartupTimelineEvent> Timeline { get; } = new(capacity: MaxRetainedTimelineEvents);

        public int TotalAssemblyLoads { get; private set; }
        public int TotalModuleLoads { get; private set; }
        public int TotalTimelineEvents { get; private set; }
        public int TotalDiEvents { get; private set; }
        public int DiServiceProviderBuiltCount { get; private set; }
        public int DiServiceProviderDescriptorsCount { get; private set; }
        public int DiCallSiteBuiltCount { get; private set; }
        public int DiServiceResolvedCount { get; private set; }
        public int DiExpressionTreeGeneratedCount { get; private set; }
        public int DiDynamicMethodBuiltCount { get; private set; }
        public int DiServiceRealizationFailedCount { get; private set; }
        public bool Truncated { get; private set; }

        public TimeSpan ObservedDiActivityDuration =>
            _firstDiEventAt.HasValue && _lastDiEventAt.HasValue
                ? _lastDiEventAt.Value - _firstDiEventAt.Value
                : TimeSpan.Zero;

        public void AddAssembly(StartupAssemblyLoad load)
        {
            TotalAssemblyLoads++;
            RecordAggregate(_assembliesByName, load.AssemblyName, load.Timestamp);
            TryCapture(AssemblyLoads, MaxRetainedAssemblyLoads, load);
            AddTimeline(new StartupTimelineEvent(load.Timestamp, "assembly", load.EventName, load.AssemblyName));
        }

        public void AddModule(StartupModuleLoad load)
        {
            TotalModuleLoads++;
            RecordAggregate(_modulesByName, load.ModuleName, load.Timestamp);
            TryCapture(ModuleLoads, MaxRetainedModuleLoads, load);
            AddTimeline(new StartupTimelineEvent(load.Timestamp, "module", load.EventName, load.ModuleName));
        }

        public void AddDiEvent(StartupDiEvent diEvent)
        {
            TotalDiEvents++;
            _firstDiEventAt ??= diEvent.Timestamp;
            _lastDiEventAt = diEvent.Timestamp;
            switch (diEvent.EventName)
            {
                case "ServiceProviderBuilt":
                    DiServiceProviderBuiltCount++;
                    break;
                case "ServiceProviderDescriptors":
                    DiServiceProviderDescriptorsCount++;
                    break;
                case "CallSiteBuilt":
                    DiCallSiteBuiltCount++;
                    break;
                case "ServiceResolved":
                case "ServiceRealized":
                    DiServiceResolvedCount++;
                    break;
                case "ExpressionTreeGenerated":
                    DiExpressionTreeGeneratedCount++;
                    break;
                case "DynamicMethodBuilt":
                    DiDynamicMethodBuiltCount++;
                    break;
                case "ServiceRealizationFailed":
                    DiServiceRealizationFailedCount++;
                    break;
            }

            TryCapture(DiEvents, MaxRetainedDiEvents, diEvent);
            var name = FirstNonEmpty(diEvent.ServiceType, diEvent.ServiceProviderHashCode?.ToString(CultureInfo.InvariantCulture)) ?? diEvent.EventName;
            AddTimeline(new StartupTimelineEvent(diEvent.Timestamp, "di", diEvent.EventName, name));
        }

        public List<StartupLoadAggregate> BuildAssemblyAggregates() => BuildAggregates(_assembliesByName);

        public List<StartupLoadAggregate> BuildModuleAggregates() => BuildAggregates(_modulesByName);

        private void AddTimeline(StartupTimelineEvent timelineEvent)
        {
            TotalTimelineEvents++;
            TryCapture(Timeline, MaxRetainedTimelineEvents, timelineEvent);
        }

        private void TryCapture<T>(List<T> items, int maxCount, T item)
        {
            if (items.Count >= maxCount)
            {
                Truncated = true;
                return;
            }

            items.Add(item);
        }

        private static void RecordAggregate(
            Dictionary<string, RawLoadAggregate> aggregates,
            string name,
            DateTimeOffset timestamp)
        {
            if (!aggregates.TryGetValue(name, out var aggregate))
            {
                aggregate = new RawLoadAggregate(name, timestamp, timestamp);
                aggregates[name] = aggregate;
            }

            aggregate.Count++;
            if (timestamp < aggregate.FirstSeenAt)
            {
                aggregate.FirstSeenAt = timestamp;
            }

            if (timestamp > aggregate.LastSeenAt)
            {
                aggregate.LastSeenAt = timestamp;
            }
        }

        private static List<StartupLoadAggregate> BuildAggregates(Dictionary<string, RawLoadAggregate> aggregates) =>
            aggregates.Values
                .Select(static aggregate => new StartupLoadAggregate(
                    aggregate.Name,
                    aggregate.Count,
                    aggregate.FirstSeenAt,
                    aggregate.LastSeenAt))
                .OrderByDescending(static aggregate => aggregate.Count)
                .ThenBy(static aggregate => aggregate.Name, StringComparer.Ordinal)
                .ToList();

        private sealed class RawLoadAggregate
        {
            public RawLoadAggregate(string name, DateTimeOffset firstSeenAt, DateTimeOffset lastSeenAt)
            {
                Name = name;
                FirstSeenAt = firstSeenAt;
                LastSeenAt = lastSeenAt;
            }

            public string Name { get; }
            public int Count;
            public DateTimeOffset FirstSeenAt;
            public DateTimeOffset LastSeenAt;
        }
    }
}
