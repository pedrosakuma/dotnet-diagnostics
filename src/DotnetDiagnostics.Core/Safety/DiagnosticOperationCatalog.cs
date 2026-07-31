using System.Collections.Immutable;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.EventSources;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.MethodParameters;
using DotnetDiagnostics.Core.OffCpu;
using DotnetDiagnostics.Core.Threads;
using DotnetDiagnostics.Core.UseCases;

namespace DotnetDiagnostics.Core.Safety;

/// <summary>
/// Canonical operation and discriminator names shared by the MCP server, CLI, safety resolver,
/// metadata generation, and parity tests.
/// </summary>
public static class DiagnosticOperationCatalog
{
    public const string InspectProcess = "inspect_process";
    public const string CollectEvents = "collect_events";
    public const string CollectSample = "collect_sample";
    public const string CollectBatch = "collect_batch";
    public const string InspectHeap = "inspect_heap";
    public const string QuerySnapshot = "query_snapshot";
    public const string GetBytes = "get_bytes";
    public const string CollectProcessDump = "collect_process_dump";
    public const string CollectThreadSnapshot = "collect_thread_snapshot";
    public const string CaptureMethodBytes = "capture_method_bytes";
    public const string StartInvestigation = "start_investigation";
    public const string ExportInvestigationSummary = "export_investigation_summary";
    public const string CompareToBaseline = "compare_to_baseline";
    public const string ListOrchestrator = "list_orchestrator";
    public const string AttachToPod = "attach_to_pod";
    public const string DetachFromPod = "detach_from_pod";
    public const string DiscoverAzure = "discover_azure";

    public const string DockerBootstrap = "docker_bootstrap";
    public const string LaunchProcess = "launch_process";
    public const string ShellCompletion = "shell_completion";
    public const string Session = "session";
    public const string ThreadSnapshotCliKind = "thread-snapshot";

    public static IReadOnlyList<string> McpOperations { get; } =
    [
        InspectProcess,
        CollectEvents,
        CollectSample,
        CollectBatch,
        InspectHeap,
        QuerySnapshot,
        GetBytes,
        CollectProcessDump,
        CollectThreadSnapshot,
        CaptureMethodBytes,
        StartInvestigation,
        ExportInvestigationSummary,
        CompareToBaseline,
        ListOrchestrator,
        AttachToPod,
        DetachFromPod,
        DiscoverAzure,
    ];

    public static IReadOnlyList<string> CliOnlyOperations { get; } =
    [
        DockerBootstrap,
        LaunchProcess,
        ShellCompletion,
        Session,
    ];

    public static class InspectProcessViews
    {
        public const string List = "list";
        public const string Info = "info";
        public const string Capabilities = "capabilities";
        public const string Container = "container";
        public const string MemoryTrend = "memory_trend";
        public const string RuntimeConfig = "runtime-config";
        public const string Resources = "resources";
        public const string RequestsNow = "requests-now";
        public const string Triage = "triage";
        public const string Preflight = "preflight";

        public static IReadOnlyList<string> All { get; } =
        [
            List,
            Info,
            Capabilities,
            Container,
            MemoryTrend,
            RuntimeConfig,
            Resources,
            RequestsNow,
            Triage,
            Preflight,
        ];

        public static IReadOnlyList<string> Cli { get; } = [Triage, RuntimeConfig, Container];
    }

    public static class CollectEventsKinds
    {
        public const string Counters = "counters";
        public const string Exceptions = "exceptions";
        public const string CrashGuard = "crash-guard";
        public const string Gc = "gc";
        public const string Datas = "datas";
        public const string Catalog = "catalog";
        public const string EventSource = "event_source";
        public const string Activities = "activities";
        public const string Logs = "logs";
        public const string Jit = "jit";
        public const string ThreadPool = "threadpool";
        public const string Contention = "contention";
        public const string Db = "db";
        public const string Kestrel = "kestrel";
        public const string Networking = "networking";
        public const string Requests = "requests";
        public const string Startup = "startup";
        public const string Sweep = "sweep";
        public const string DistributedTrace = "distributed_trace";
        public const string ReplicaCounters = "replica_counters";

        public static IReadOnlyList<string> All { get; } =
        [
            Counters,
            Exceptions,
            CrashGuard,
            Gc,
            Datas,
            Catalog,
            EventSource,
            Activities,
            Logs,
            Jit,
            ThreadPool,
            Contention,
            Db,
            Kestrel,
            Networking,
            Requests,
            Startup,
            Sweep,
            DistributedTrace,
            ReplicaCounters,
        ];

        public static IReadOnlyList<string> Cli { get; } =
        [
            Counters,
            Exceptions,
            CrashGuard,
            Gc,
            Datas,
            Catalog,
            EventSource,
            Activities,
            Logs,
            Jit,
            ThreadPool,
            Contention,
            Db,
            Kestrel,
            Networking,
            Requests,
            Startup,
            Sweep,
        ];
    }

    public static class CollectSampleKinds
    {
        public const string Cpu = "cpu";
        public const string OffCpu = "off_cpu";
        public const string OffCpuCliAlias = "off-cpu";
        public const string Allocation = "allocation";
        public const string NativeAlloc = "native-alloc";
        public const string MethodParameters = "method-params";

        public static IReadOnlyList<string> All { get; } =
        [
            Cpu,
            OffCpu,
            Allocation,
            NativeAlloc,
            MethodParameters,
        ];

        public static IReadOnlyList<string> Cli { get; } =
        [
            Cpu,
            OffCpu,
            OffCpuCliAlias,
            Allocation,
            NativeAlloc,
        ];
    }

    public static class HeapSources
    {
        public const string Live = "live";
        public const string Dump = "dump";
        public const string GcDump = "gcdump";

        public static IReadOnlyList<string> All { get; } = [Live, Dump, GcDump];
    }

    public static class ByteKinds
    {
        public const string Module = "module";
        public const string Dump = "dump";
        public const string Trace = "trace";
        public const string List = "list";
        public const string Delete = "delete";

        public static IReadOnlyList<string> All { get; } = [Module, Dump, Trace, List, Delete];
        public static IReadOnlyList<string> Cli { get; } = [Module, Dump, Trace];
    }

    public static class ListOrchestratorKinds
    {
        public const string Pods = "pods";
        public const string Investigations = "investigations";
        public const string ExternalProfiles = "external-profiles";

        public static IReadOnlyList<string> All { get; } = [Pods, Investigations, ExternalProfiles];
    }

    public static class DiscoverAzureKinds
    {
        public const string WebApps = "webapps";
        public const string ContainerApps = "containerapps";
        public const string AksClusters = "aksclusters";

        public static IReadOnlyList<string> All { get; } = [WebApps, ContainerApps, AksClusters];
    }

    public static class CollectBatchTools
    {
        public static IReadOnlyList<string> All { get; } = [CollectSample, CollectEvents];
    }

    public static class QuerySnapshotViews
    {
        public const string Diff = "diff";
        public const string Growth = "growth";
        public const string ResolveAddress = "resolve-address";
        public const string FrameVariables = "frame-vars";
        public const string ObjectView = "object";
        public const string GcRoot = "gcroot";
        public const string ObjectSize = "objsize";
        public const string DuplicateStrings = "duplicate-strings";

        public static ImmutableArray<string> All { get; } = BuildQuerySnapshotViews();

        private static ImmutableArray<string> BuildQuerySnapshotViews()
        {
            var views = new List<string>();

            Add(HeapSnapshotQueryDispatcher.ProjectionViews);
            Add([ObjectView, GcRoot, ObjectSize, DuplicateStrings, Diff, Growth]);
            Add(ThreadSnapshotQueryDispatcher.SessionViews);
            Add([ResolveAddress, FrameVariables]);
            Add(OffCpuQueryDispatcher.SessionViews);
            Add(CpuSampleQueryDispatcher.SessionViews);
            Add([Diff]);
            Add(EventCatalogQueryDispatcher.SessionViews);
            Add(GcDatasQueryDispatcher.SessionViews);

            var collectionKinds = new[]
            {
                CollectionHandleKinds.Counters,
                CollectionHandleKinds.ExceptionSnapshot,
                CollectionHandleKinds.CrashGuardSnapshot,
                CollectionHandleKinds.GcEvents,
                CollectionHandleKinds.EventSource,
                CollectionHandleKinds.Activities,
                CollectionHandleKinds.LogSnapshot,
                CollectionHandleKinds.JitSnapshot,
                CollectionHandleKinds.ThreadPoolSnapshot,
                CollectionHandleKinds.ContentionSnapshot,
                CollectionHandleKinds.DbSnapshot,
                CollectionHandleKinds.KestrelSnapshot,
                CollectionHandleKinds.InFlightRequests,
                CollectionHandleKinds.NetworkingSnapshot,
                CollectionHandleKinds.StartupSnapshot,
            };
            foreach (var kind in collectionKinds)
            {
                Add(CollectionQueryDispatcher.ViewsFor(kind));
            }

            return views.ToImmutableArray();

            void Add(IEnumerable<string> candidates)
            {
                foreach (var candidate in candidates)
                {
                    if (!views.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    {
                        views.Add(candidate);
                    }
                }

            }
        }
    }

    public static class QuerySnapshotHandleKinds
    {
        public const string CpuSample = "cpu-sample";
        public const string AllocationSample = "allocation-sample";

        public static IReadOnlyList<string> All { get; } =
        [
            HeapInspectionUseCases.HeapSnapshotKind,
            SamplerUseCases.ThreadSnapshotKind,
            SamplerUseCases.OffCpuHandleKind,
            CpuSample,
            AllocationSample,
            SamplerUseCases.NativeAllocHandleKind,
            CollectionHandleKinds.Counters,
            CollectionHandleKinds.EventCatalog,
            CollectionHandleKinds.GcDatas,
            CollectionHandleKinds.ExceptionSnapshot,
            CollectionHandleKinds.CrashGuardSnapshot,
            CollectionHandleKinds.GcEvents,
            CollectionHandleKinds.EventSource,
            CollectionHandleKinds.Activities,
            CollectionHandleKinds.LogSnapshot,
            CollectionHandleKinds.JitSnapshot,
            CollectionHandleKinds.ThreadPoolSnapshot,
            CollectionHandleKinds.ContentionSnapshot,
            CollectionHandleKinds.DbSnapshot,
            CollectionHandleKinds.KestrelSnapshot,
            CollectionHandleKinds.NetworkingSnapshot,
            CollectionHandleKinds.StartupSnapshot,
            CollectionHandleKinds.InFlightRequests,
            MethodParameterCaptureUseCases.HandleKind,
        ];
    }

    public static IReadOnlyList<string> CliCollectKinds { get; } =
    [
        .. CollectEventsKinds.Cli,
        .. CollectSampleKinds.Cli,
        ThreadSnapshotCliKind,
    ];
}
