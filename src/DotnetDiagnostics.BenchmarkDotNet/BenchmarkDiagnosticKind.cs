namespace DotnetDiagnostics.BenchmarkDotNet;

/// <summary>
/// The diagnostic kinds the <see cref="DotnetDiagnosticsDiagnoser"/> can capture in-process against a
/// benchmark's child process. This is the discoverable, compile-time-checked surface for
/// <see cref="DiagnosticKindAttribute"/> — prefer it over the free-text <c>string</c> overload so a
/// typo is a build error (with IntelliSense) rather than a BenchmarkDotNet validation failure.
/// </summary>
/// <remarks>
/// Every member maps to exactly one <see cref="InProcessDiagnosticCollector.SupportedKinds"/> token
/// via <see cref="BenchmarkDiagnosticKinds.Token"/>; a parity test keeps the enum and the collector in
/// lock-step. Mirrors the repository's existing <c>GatedCaptureKind</c>/<c>GatedCaptureKinds</c> pattern.
/// </remarks>
public enum BenchmarkDiagnosticKind
{
    /// <summary><c>counters</c> — EventCounters snapshot (runtime + host metrics).</summary>
    Counters,

    /// <summary><c>exceptions</c> — first-chance / thrown exception collection.</summary>
    Exceptions,

    /// <summary><c>gc</c> — garbage-collection events (pauses, generations, heap sizes).</summary>
    Gc,

    /// <summary><c>cpu</c> — EventPipe CPU sampler (per-frame self/inclusive cost). CoreCLR only.</summary>
    Cpu,

    /// <summary><c>allocation</c> — allocation sampler (per-type / per-call-site churn).</summary>
    Allocation,

    /// <summary><c>datas</c> — GC "dynamic adaptation to application sizes" (DATAS) telemetry.</summary>
    Datas,

    /// <summary><c>catalog</c> — discovered EventSource provider/event catalog.</summary>
    Catalog,

    /// <summary><c>activities</c> — distributed-tracing Activity/DiagnosticSource events.</summary>
    Activities,

    /// <summary><c>logs</c> — <c>ILogger</c> / <c>Microsoft.Extensions.Logging</c> events.</summary>
    Logs,

    /// <summary><c>jit</c> — JIT compilation events (methods compiled, tiering).</summary>
    Jit,

    /// <summary><c>threadpool</c> — thread-pool worker/IO events (starvation, queue depth).</summary>
    ThreadPool,

    /// <summary><c>contention</c> — managed lock contention events.</summary>
    Contention,

    /// <summary><c>db</c> — database client (ADO.NET / EF Core) command events.</summary>
    Db,

    /// <summary><c>kestrel</c> — Kestrel HTTP server request-pipeline timings.</summary>
    Kestrel,

    /// <summary><c>networking</c> — <c>HttpClient</c> / socket / DNS / TLS activity.</summary>
    Networking,

    /// <summary><c>requests</c> — in-flight ASP.NET Core requests.</summary>
    Requests,

    /// <summary>
    /// <c>gcdump</c> — managed-heap <b>retention</b> snapshot (per-type instance/byte totals) via the
    /// EventPipe GCHeapSnapshot keyword. CoreCLR only; withheld (NotSupported) on NativeAOT children.
    /// </summary>
    GcDump,
}

/// <summary>Token mapping helpers for <see cref="BenchmarkDiagnosticKind"/>.</summary>
public static class BenchmarkDiagnosticKinds
{
    /// <summary>
    /// The canonical <c>collect</c> token for <paramref name="kind"/> (e.g.
    /// <see cref="BenchmarkDiagnosticKind.ThreadPool"/> → <c>threadpool</c>). These tokens are the
    /// single source of truth read by <see cref="InProcessDiagnosticCollector"/>.
    /// </summary>
    public static string Token(BenchmarkDiagnosticKind kind) => kind switch
    {
        BenchmarkDiagnosticKind.Counters => "counters",
        BenchmarkDiagnosticKind.Exceptions => "exceptions",
        BenchmarkDiagnosticKind.Gc => "gc",
        BenchmarkDiagnosticKind.Cpu => "cpu",
        BenchmarkDiagnosticKind.Allocation => "allocation",
        BenchmarkDiagnosticKind.Datas => "datas",
        BenchmarkDiagnosticKind.Catalog => "catalog",
        BenchmarkDiagnosticKind.Activities => "activities",
        BenchmarkDiagnosticKind.Logs => "logs",
        BenchmarkDiagnosticKind.Jit => "jit",
        BenchmarkDiagnosticKind.ThreadPool => "threadpool",
        BenchmarkDiagnosticKind.Contention => "contention",
        BenchmarkDiagnosticKind.Db => "db",
        BenchmarkDiagnosticKind.Kestrel => "kestrel",
        BenchmarkDiagnosticKind.Networking => "networking",
        BenchmarkDiagnosticKind.Requests => "requests",
        BenchmarkDiagnosticKind.GcDump => "gcdump",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown benchmark diagnostic kind."),
    };
}
