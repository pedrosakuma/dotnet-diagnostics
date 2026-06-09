using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Contention;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.Db;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.EventSources;
using DotnetDiagnostics.Core.Exceptions;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Hosting;
using DotnetDiagnostics.Core.Jit;
using DotnetDiagnostics.Core.Logs;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.ThreadPool;
using DotnetDiagnostics.Core.UseCases;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetDiagnostics.BenchmarkDotNet;

/// <summary>
/// Runs a single dotnet-diagnostics <c>collect</c> kind <b>in-process</b> against a target PID by
/// composing the Core engine (<see cref="DiagnosticCoreServiceRegistration.AddDiagnosticCoreServices"/>)
/// into a private <see cref="ServiceProvider"/> and invoking the matching
/// <see cref="EventCollectionUseCases"/> entry point. The heavy ClrMD/TraceEvent dependencies load
/// in <i>this</i> (the BenchmarkDotNet orchestrator) process, never in the measured child — so the
/// collection does not contaminate the benchmark's timing or allocations.
/// </summary>
internal sealed class InProcessDiagnosticCollector : IDisposable
{
    /// <summary>
    /// The <c>collect</c> kinds the diagnoser can dispatch in-process. Mirrors the CLI's
    /// <c>CollectKinds</c> minus <c>event_source</c>, which needs an explicit provider name and is
    /// not benchmark-relevant.
    /// </summary>
    public static readonly IReadOnlySet<string> SupportedKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "counters",
        "exceptions",
        "gc",
        "datas",
        "catalog",
        "activities",
        "logs",
        "jit",
        "threadpool",
        "contention",
        "db",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lazy<ServiceProvider> _provider = new(BuildProvider, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool IsSupported(string kind) => SupportedKinds.Contains(kind);

    /// <summary>
    /// Collects a single kind against <paramref name="processId"/> and projects the Core envelope
    /// into a <see cref="KindCapture"/> (a serialized JSON artifact plus a one-line headline).
    /// </summary>
    public async Task<KindCapture> CollectAsync(int processId, string kind, int durationSeconds, CancellationToken cancellationToken)
    {
        if (!SupportedKinds.Contains(kind))
        {
            return KindCapture.Unsupported(kind);
        }

        var services = _provider.Value;
        var resolver = services.GetRequiredService<IProcessContextResolver>();
        var handles = services.GetRequiredService<IDiagnosticHandleStore>();

        return kind switch
        {
            "counters" => Materialize(kind, await EventCollectionUseCases.SnapshotCounters(
                services.GetRequiredService<ICounterCollector>(), resolver, handles, processId, durationSeconds, cancellationToken: cancellationToken).ConfigureAwait(false)),
            "exceptions" => Materialize(kind, await EventCollectionUseCases.CollectExceptions(
                services.GetRequiredService<IExceptionCollector>(), resolver, handles, processId, durationSeconds, cancellationToken: cancellationToken).ConfigureAwait(false)),
            "gc" => Materialize(kind, await EventCollectionUseCases.CollectGcEvents(
                services.GetRequiredService<IGcCollector>(), resolver, handles, processId, durationSeconds, cancellationToken: cancellationToken).ConfigureAwait(false)),
            "datas" => Materialize(kind, await EventCollectionUseCases.CollectGcDatas(
                services.GetRequiredService<IGcDatasCollector>(), resolver, handles, processId, durationSeconds, cancellationToken: cancellationToken).ConfigureAwait(false)),
            "catalog" => Materialize(kind, await EventCollectionUseCases.CollectEventCatalog(
                services.GetRequiredService<IEventCatalogCollector>(), resolver, handles, processId, durationSeconds, cancellationToken: cancellationToken).ConfigureAwait(false)),
            "logs" => Materialize(kind, await EventCollectionUseCases.CollectLogs(
                services.GetRequiredService<ILogCollector>(), resolver, handles, processId, durationSeconds, cancellationToken: cancellationToken).ConfigureAwait(false)),
            "jit" => Materialize(kind, await EventCollectionUseCases.CollectJit(
                services.GetRequiredService<IJitCollector>(), resolver, handles, processId, durationSeconds, cancellationToken: cancellationToken).ConfigureAwait(false)),
            "threadpool" => Materialize(kind, await EventCollectionUseCases.CollectThreadPool(
                services.GetRequiredService<IThreadPoolCollector>(), resolver, handles, processId, durationSeconds, cancellationToken: cancellationToken).ConfigureAwait(false)),
            "contention" => Materialize(kind, await EventCollectionUseCases.CollectContention(
                services.GetRequiredService<IContentionCollector>(), resolver, handles, processId, durationSeconds, cancellationToken: cancellationToken).ConfigureAwait(false)),
            "db" => Materialize(kind, await EventCollectionUseCases.CollectDb(
                services.GetRequiredService<IDbCollector>(), resolver, handles, processId, durationSeconds, cancellationToken: cancellationToken).ConfigureAwait(false)),
            "activities" => Materialize(kind, await EventCollectionUseCases.CollectActivities(
                services.GetRequiredService<IActivityCollector>(), resolver, handles, processId, durationSeconds: durationSeconds, cancellationToken: cancellationToken).ConfigureAwait(false)),
            _ => KindCapture.Unsupported(kind),
        };
    }

    private static KindCapture Materialize<T>(string kind, DiagnosticResult<T> result)
    {
        // result has compile-time type DiagnosticResult<T> (concrete per use-case), so the full
        // payload graph is serialized — unlike a System.Text.Json call over a boxed `object`.
        var json = JsonSerializer.Serialize(result, JsonOptions);
        var headline = result.IsError
            ? $"{result.Error!.Kind}: {result.Error.Message}"
            : result.Summary;
        return new KindCapture(kind, result.IsError, result.Summary, headline, json);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // Benchmark diagnosis never opts into sensitive heap values or EventSource allowlists, so
        // the default (safe) SecurityOptions is sufficient. Core stays configuration-free.
        services.AddDiagnosticCoreServices(new SecurityOptions());
        return services.BuildServiceProvider();
    }

    public void Dispose()
    {
        if (_provider.IsValueCreated)
        {
            _provider.Value.Dispose();
        }
    }
}

/// <summary>
/// The projection of a single in-process collection: the Core envelope's summary plus a serialized
/// JSON artifact and a one-line headline suitable for the offenders report.
/// </summary>
internal sealed record KindCapture(string Kind, bool IsError, string Summary, string Headline, string Json)
{
    public static KindCapture Unsupported(string kind) => new(
        kind,
        IsError: true,
        Summary: $"Unsupported collect kind '{kind}'.",
        Headline: $"unsupported kind '{kind}'",
        Json: $"{{ \"error\": \"unsupported collect kind '{kind}'\" }}");
}
