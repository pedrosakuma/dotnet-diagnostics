using DotnetDiagnostics.Core.Collection;

namespace DotnetDiagnostics.Core.UseCases;

internal static class DurableCaptureKinds
{
    internal static string Canonical(string kind) => kind switch
    {
        "exceptions" => CollectionHandleKinds.ExceptionSnapshot,
        "crash-guard" => CollectionHandleKinds.CrashGuardSnapshot,
        "gc" => CollectionHandleKinds.GcEvents,
        "datas" => CollectionHandleKinds.GcDatas,
        "event_source" => CollectionHandleKinds.EventSource,
        "catalog" => CollectionHandleKinds.EventCatalog,
        "logs" => CollectionHandleKinds.LogSnapshot,
        "jit" => CollectionHandleKinds.JitSnapshot,
        "threadpool" => CollectionHandleKinds.ThreadPoolSnapshot,
        "contention" => CollectionHandleKinds.ContentionSnapshot,
        "db" => CollectionHandleKinds.DbSnapshot,
        "kestrel" => CollectionHandleKinds.KestrelSnapshot,
        "networking" => CollectionHandleKinds.NetworkingSnapshot,
        "requests" => CollectionHandleKinds.InFlightRequests,
        "startup" => CollectionHandleKinds.StartupSnapshot,
        "cpu" => "cpu-sample",
        "allocation" or "allocations" => "allocation-sample",
        "off_cpu" => SamplerUseCases.OffCpuHandleKind,
        "native-alloc" or "native_alloc" => SamplerUseCases.NativeAllocHandleKind,
        "native-lock-contention" or "native_lock_contention" => SamplerUseCases.NativeLockContentionHandleKind,
        "cpu-efficiency" or "cpu_efficiency" => SamplerUseCases.CpuEfficiencyHandleKind,
        "method-params" => MethodParameterCaptureUseCases.HandleKind,
        _ => kind,
    };
}
