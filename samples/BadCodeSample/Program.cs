using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

// BadCodeSample — a deliberately-broken minimal API used to exercise the
// dotnet-diagnostics-mcp tools. Every endpoint triggers a different anti-pattern that
// should be detectable end-to-end via the MCP server (counters, cpu sampling,
// exceptions, GC events, EventSource passthrough, dump).
//
// See docs/bad-code-scenarios.md for the per-endpoint investigation playbook.

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient("slow", c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
});

const string dbConnectionString = "Data Source=file:badcode-db?mode=memory&cache=shared&Pwd=super-secret";
builder.Services.AddSingleton(_ =>
{
    var connection = new SqliteConnection(dbConnectionString);
    connection.Open();

    using var command = connection.CreateCommand();
    command.CommandText = """
        CREATE TABLE IF NOT EXISTS widgets (
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL
        );
        INSERT INTO widgets (id, name)
        VALUES (1, 'alpha')
        ON CONFLICT(id) DO UPDATE SET name = excluded.name;
        """;
    command.ExecuteNonQuery();
    return connection;
});
builder.Services.AddDbContextFactory<BadCodeDbContext>((serviceProvider, options) =>
    options.UseSqlite(serviceProvider.GetRequiredService<SqliteConnection>()));

var app = builder.Build();

var leakedBuffers = new List<byte[]>();
var leakedFiles = new List<FileStream>();
var leakedSockets = new List<LeakedSocketConnection>();
var leakedHandleWindows = new List<(nint[] HandlePointers, byte[][] Payloads)>();
var leakedTimers = new List<Timer>();
var leakedAssemblyLoadContexts = new List<AlcLeakRoot>();
var leakedNativeAllocations = new List<nint>();
var badCodeEndpoints = new[]
{
    "/cpu-burn?ms=2000",
    "/leak?mb=4",
    "/exceptions?count=200",
    "/sync-over-async?n=20",
    "/sync-over-async-fixed?n=20&delaySeconds=3",
    "/culture-lookup?iterations=2000000",
    "/culture-lookup-fixed?iterations=2000000",
    "/lock-contention?threads=32&ms=1500",
    "/loh-alloc?count=20",
    "/slow-http?url=https://httpbin.org/delay/3",
    "/fd-leak?count=64",
    "/socket-leak?count=32&host=loopback",
    "/handle-leak?type=pinned|normal|weak&count=200&seconds=10",
    "/timer-leak?count=50",
    "/alc-leak?count=4",
    "/native-bloat?mb=128",
    "/meter-spam?count=5&kind=counter",
    "/log-spam?count=200&level=warning",
    "/jit-pressure?count=200",
    "/slow-hang?seconds=5",
    "/async-stall?bucket=tcs&seconds=5",
    "/threadpool-starve?blockers=50",
    "/lock-storm?seconds=5&blockers=8",
    "/db-n+1?count=15",
    "/crash?mode=unhandled|stackoverflow|oom",
};
var lockObject = new object();
var lockStormGate = new object();
using var dbActivitySource = new ActivitySource("Microsoft.EntityFrameworkCore");
var meterFactory = app.Services.GetRequiredService<IMeterFactory>();
var meter = meterFactory.Create("BadCodeSample");
var ordersTotal = meter.CreateCounter<long>("orders.total", unit: "{orders}", description: "Synthetic business counter for tests.");
var workDuration = meter.CreateHistogram<double>("work.duration", unit: "ms", description: "Synthetic histogram for meter tests.");
using var loopbackCloser = new LoopbackCloseServer();
loopbackCloser.Start();

app.MapGet("/", () => Results.Ok(new
{
    name = "BadCodeSample",
    endpoints = badCodeEndpoints,
}));

// 1. CPU burn — detect with collect_events(kind="counters") + collect_sample(kind="cpu")
app.MapGet("/cpu-burn", (int? ms) =>
{
    var budget = TimeSpan.FromMilliseconds(ms ?? 2000);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var input = Encoding.UTF8.GetBytes("dotnet-diagnostics-mcp cpu burn payload");
    long iterations = 0;
    while (sw.Elapsed < budget)
    {
        for (var i = 0; i < 10_000; i++)
        {
            _ = SHA256.HashData(input);
            iterations++;
        }
    }
    return Results.Ok(new { iterations, elapsedMs = sw.ElapsedMilliseconds });
});

// 2. Managed memory leak — detect with collect_events(kind="counters") (gc-heap-size, gen2)
//    + collect_events(kind="gc") + collect_process_dump
app.MapGet("/leak", (int? mb) =>
{
    var size = Math.Clamp(mb ?? 4, 1, 64) * 1024 * 1024;
    var buf = new byte[size];
    Random.Shared.NextBytes(buf);
    lock (leakedBuffers)
    {
        leakedBuffers.Add(buf);
    }
    return Results.Ok(new { addedMb = size / (1024 * 1024), totalBuffers = leakedBuffers.Count });
});

// 3. Exception storm — detect with collect_events(kind="counters") + collect_events(kind="exceptions")
app.MapGet("/exceptions", (int? count) =>
{
    var n = Math.Clamp(count ?? 200, 1, 5_000);
    var caught = 0;
    for (var i = 0; i < n; i++)
    {
        try
        {
            _ = int.Parse("not-a-number", CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            caught++;
        }
    }
    return Results.Ok(new { caught });
});

// 4. Sync-over-async / thread pool starvation — detect with counters
//    (threadpool-queue-length, threadpool-thread-count) + collect_sample(kind="cpu")
// The BROKEN version: every fan-out call parks a ThreadPool thread on
// .GetAwaiter().GetResult(). Pass delaySeconds=N to make the downstream a
// deterministic loopback /slow-hang?seconds=N (offline, repeatable starvation);
// omit it to hit the real remote host (production-shaped but flaky).
app.MapGet("/sync-over-async", (IHttpClientFactory http, HttpRequest request, int? n, int? delaySeconds) =>
{
    var clients = Math.Clamp(n ?? 20, 1, 200);
    var target = ResolveSyncOverAsyncTarget(request, delaySeconds);
    var tasks = new List<Task>();
    for (var i = 0; i < clients; i++)
    {
        tasks.Add(Task.Run(() =>
        {
            using var client = http.CreateClient("slow");
            client.Timeout = TimeSpan.FromSeconds(5);
            try
            {
                _ = client.GetAsync(target).GetAwaiter().GetResult();
            }
            catch
            {
            }
        }));
    }
    Task.WaitAll(tasks.ToArray());
    return Results.Ok(new { dispatched = clients, target });
});

// The FIXED version: identical fan-out and downstream, but awaited end-to-end —
// it holds ZERO ThreadPool threads while the calls are in flight, so the same
// load that starves the endpoint above stays healthy here. Use it for a
// deterministic before/after with delaySeconds=N against both endpoints.
app.MapGet("/sync-over-async-fixed", async (IHttpClientFactory http, HttpRequest request, int? n, int? delaySeconds) =>
{
    var clients = Math.Clamp(n ?? 20, 1, 200);
    var target = ResolveSyncOverAsyncTarget(request, delaySeconds);
    var client = http.CreateClient("slow");
    client.Timeout = TimeSpan.FromSeconds(5);
    var tasks = new List<Task>();
    for (var i = 0; i < clients; i++)
    {
        tasks.Add(SafeGetAsync(client, target));
    }
    await Task.WhenAll(tasks);
    return Results.Ok(new { dispatched = clients, target });

    static async Task SafeGetAsync(HttpClient client, string url)
    {
        try
        {
            _ = await client.GetAsync(url);
        }
        catch
        {
        }
    }
});

static string ResolveSyncOverAsyncTarget(HttpRequest request, int? delaySeconds)
    => delaySeconds is int seconds
        ? $"{request.Scheme}://{request.Host}/slow-hang?seconds={Math.Clamp(seconds, 1, 4)}"
        : "https://example.com";

// Culture-aware dictionary lookup — a hot path whose cost is INVISIBLE in the
// endpoint source. The dictionaries are built with different string comparers;
// the "slow" one uses a culture-sensitive comparer, so every lookup pays for
// globalization (System.Globalization.CompareInfo hashing/compare via ICU)
// instead of a plain ordinal hash. The loop below looks identical for both.
var flagKeys = Enumerable.Range(0, 1000)
    .Select(i => $"Feature.Flag.{i:D4}.Enabled")
    .ToArray();
var cultureAwareFlags = new Dictionary<string, bool>(StringComparer.InvariantCultureIgnoreCase);
var ordinalFlags = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
foreach (var flagKey in flagKeys)
{
    cultureAwareFlags[flagKey] = true;
    ordinalFlags[flagKey] = true;
}

static long RunFlagLookups(string[] keys, IReadOnlyDictionary<string, bool> flags, int loops)
{
    var hits = 0L;
    for (var i = 0; i < loops; i++)
    {
        var key = keys[i % keys.Length];
        if (flags.TryGetValue(key, out var enabled) && enabled)
        {
            hits++;
        }
    }
    return hits;
}

app.MapGet("/culture-lookup", (int? iterations) =>
{
    var loops = Math.Clamp(iterations ?? 2_000_000, 1, 50_000_000);
    var hits = RunFlagLookups(flagKeys, cultureAwareFlags, loops);
    return Results.Ok(new { loops, hits, comparer = "InvariantCultureIgnoreCase" });
});

app.MapGet("/culture-lookup-fixed", (int? iterations) =>
{
    var loops = Math.Clamp(iterations ?? 2_000_000, 1, 50_000_000);
    var hits = RunFlagLookups(flagKeys, ordinalFlags, loops);
    return Results.Ok(new { loops, hits, comparer = "OrdinalIgnoreCase" });
});

// 5. Monitor lock contention — detect with counters
//    (monitor-lock-contention-count) + collect_sample(kind="cpu")
app.MapGet("/lock-contention", (int? threads, int? ms) =>
{
    var threadCount = Math.Clamp(threads ?? 32, 2, 256);
    var budget = TimeSpan.FromMilliseconds(Math.Clamp(ms ?? 1500, 100, 30_000));
    var stop = DateTime.UtcNow + budget;
    var tasks = new List<Task>();
    long entered = 0;
    for (var i = 0; i < threadCount; i++)
    {
        tasks.Add(Task.Run(() =>
        {
            while (DateTime.UtcNow < stop)
            {
                lock (lockObject)
                {
                    Interlocked.Increment(ref entered);
                    Thread.SpinWait(5_000);
                }
            }
        }));
    }
    Task.WaitAll(tasks.ToArray());
    return Results.Ok(new { threads = threadCount, entered });
});

// 6. LOH allocation churn — detect with counters (loh-size, gen2-gc-count)
//    + collect_events(kind="gc")
app.MapGet("/loh-alloc", (int? count) =>
{
    var n = Math.Clamp(count ?? 20, 1, 500);
    long allocated = 0;
    for (var i = 0; i < n; i++)
    {
        var buf = new byte[200_000];
        Random.Shared.NextBytes(buf);
        allocated += buf.Length;
    }
    return Results.Ok(new { iterations = n, allocatedBytes = allocated });
});

// 7. Slow outbound HTTP — detect with collect_events(kind="event_source") name=System.Net.Http
app.MapGet("/slow-http", async (IHttpClientFactory http, string? url) =>
{
    using var client = http.CreateClient("slow");
    var target = url ?? "https://httpbin.org/delay/3";
    var sw = System.Diagnostics.Stopwatch.StartNew();
    HttpStatusCode? status = null;
    try
    {
        using var resp = await client.GetAsync(target);
        status = resp.StatusCode;
    }
    catch (Exception ex)
    {
        return Results.Ok(new { url = target, elapsedMs = sw.ElapsedMilliseconds, error = ex.GetType().Name });
    }
    return Results.Ok(new { url = target, elapsedMs = sw.ElapsedMilliseconds, status });
});

// 8. Meter API spam — detect with collect_events(kind="counters", meters=["BadCodeSample"])
app.MapGet("/meter-spam", (int? count, string? kind) =>
{
    var samples = Math.Clamp(count ?? 10, 1, 5_000);
    var mode = string.Equals(kind, "histogram", StringComparison.OrdinalIgnoreCase) ? "histogram" : "counter";
    for (var i = 0; i < samples; i++)
    {
        var tags = new TagList
        {
            { "series", $"series-{i}" },
            { "kind", mode },
        };

        if (mode == "histogram")
        {
            workDuration.Record(10 + i, tags);
        }
        else
        {
            ordersTotal.Add(1, tags);
        }
    }

    return Results.Ok(new { samples, kind = mode });
});

// 9. FD leak — detect with inspect_process(view="resources") (FdCount / nofile fraction)
app.MapGet("/fd-leak", (int? count) =>
{
    var n = Math.Clamp(count ?? 64, 1, 1_024);
    var path = Environment.ProcessPath ?? typeof(Program).Assembly.Location;
    var opened = 0;
    lock (leakedFiles)
    {
        for (var i = 0; i < n; i++)
        {
            leakedFiles.Add(File.OpenRead(path));
            opened++;
        }
    }

    return Results.Ok(new { opened, totalLeaked = leakedFiles.Count, path });
});

// 10. Socket leak / CLOSE_WAIT growth — detect with inspect_process(view="resources")
app.MapGet("/socket-leak", async (int? count, string? host) =>
{
    var n = Math.Clamp(count ?? 32, 1, 256);
    var target = string.IsNullOrWhiteSpace(host) ? "loopback" : host.Trim();
    var leaked = 0;

    for (var i = 0; i < n; i++)
    {
        var connection = target.Equals("loopback", StringComparison.OrdinalIgnoreCase)
            ? await LeakLoopbackSocketAsync(loopbackCloser.Port)
            : await LeakRemoteSocketAsync(target);
        lock (leakedSockets)
        {
            leakedSockets.Add(connection);
        }
        leaked++;
    }

    return Results.Ok(new { leaked, totalLeaked = leakedSockets.Count, host = target, loopbackPort = loopbackCloser.Port });
});

// 11. GCHandle leak window — detect with inspect_heap + query_snapshot(view="gchandles")
app.MapGet("/handle-leak", async (string? type, int? count, int? seconds) =>
{
    var kind = ParseHandleKind(type);
    var n = Math.Clamp(count ?? 200, 1, 5_000);
    var holdSeconds = Math.Clamp(seconds ?? 10, 1, 60);
    var handlePointers = new nint[n];
    var payloads = new byte[n][];

    for (var i = 0; i < n; i++)
    {
        payloads[i] = new byte[1024];
        handlePointers[i] = GCHandle.ToIntPtr(GCHandle.Alloc(payloads[i], kind));
    }

    var window = (HandlePointers: handlePointers, Payloads: payloads);

    try
    {
        lock (leakedHandleWindows)
        {
            leakedHandleWindows.Add(window);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        await Task.Delay(TimeSpan.FromSeconds(holdSeconds));
        return Results.Ok(new { type = kind.ToString(), count = n, seconds = holdSeconds });
    }
    finally
    {
        lock (leakedHandleWindows)
        {
            leakedHandleWindows.Remove(window);
        }

        foreach (var pointer in handlePointers)
        {
            if (pointer != nint.Zero)
            {
                GCHandle.FromIntPtr(pointer).Free();
            }
        }
    }
});

// 12. Timer leak — detect with collect_events(kind="counters") active-timer-count
//     + inspect_heap/query_snapshot(view="timers")
app.MapGet("/timer-leak", (int? count) =>
{
    var n = Math.Clamp(count ?? 50, 1, 5_000);
    lock (leakedTimers)
    {
        for (var i = 0; i < n; i++)
        {
            leakedTimers.Add(new Timer(static _ => { }, null, TimeSpan.FromHours(1), TimeSpan.FromHours(1)));
        }

        return Results.Ok(new { added = n, totalLeaked = leakedTimers.Count });
    }
});

// 13. Collectible AssemblyLoadContext leak — detect with inspect_heap + query_snapshot(view="alc")
app.MapGet("/alc-leak", (int? count) =>
{
    var n = Math.Clamp(count ?? 4, 1, 256);
    var assemblyPath = Assembly.GetExecutingAssembly().Location;
    lock (leakedAssemblyLoadContexts)
    {
        for (var i = 0; i < n; i++)
        {
            var context = new LeakyAssemblyLoadContext($"BadCodeSample.AlcLeak.{Guid.NewGuid():N}", assemblyPath);
            var assembly = context.LoadFromAssemblyPath(assemblyPath);
            var type = assembly.GetType(nameof(LoopbackCloseServer), throwOnError: true)!;
            var instance = Activator.CreateInstance(type, nonPublic: true)!;
            var handler = AlcLeakFixture.Subscribe(instance);
            leakedAssemblyLoadContexts.Add(new AlcLeakRoot(context, assembly, instance, handler));
        }

        return Results.Ok(new { added = n, totalLeaked = leakedAssemblyLoadContexts.Count });
    }
});

// 14. Native/unmanaged RSS growth with a flat managed heap — detect with inspect_process(view="resources")
app.MapGet("/native-bloat", (int? mb) =>
{
    var size = Math.Clamp(mb ?? 128, 1, 512) * 1024 * 1024;
    var pointer = Marshal.AllocHGlobal(size);
    for (var offset = 0; offset < size; offset += Environment.SystemPageSize)
    {
        Marshal.WriteByte(IntPtr.Add(pointer, offset), 0x5A);
    }

    Marshal.WriteByte(IntPtr.Add(pointer, size - 1), 0x5A);

    lock (leakedNativeAllocations)
    {
        leakedNativeAllocations.Add(pointer);
    }

    return Results.Ok(new { addedMb = size / (1024 * 1024), allocations = leakedNativeAllocations.Count });
});

// 14. ILogger warning/error storm — detect with collect_events(kind="logs")
app.MapGet("/log-spam", (ILoggerFactory loggerFactory, int? count, string? level) =>
{
    var n = Math.Clamp(count ?? 200, 1, 5_000);
    var parsedLevel = Enum.TryParse<LogLevel>(level, ignoreCase: true, out var explicitLevel)
        && explicitLevel is >= LogLevel.Trace and <= LogLevel.Critical
        ? explicitLevel
        : LogLevel.Warning;
    var logger = loggerFactory.CreateLogger("BadCodeSample.LogSpam");

    using (logger.BeginScope(
        "UserEmail {UserEmail} Password {Password} ScopeName {ScopeName}",
        "person@example.com",
        "Password=super-secret",
        "BadCode.LogSpam"))
    {
        for (var i = 0; i < n; i++)
        {
            var eventId = new EventId(10_000 + i, $"LogSpam{parsedLevel}");
            if (parsedLevel >= LogLevel.Error)
            {
                logger.Log(parsedLevel, eventId, new InvalidOperationException($"boom-{i}"), "Log spam {Level} #{Index}", parsedLevel, i);
            }
            else
            {
                logger.Log(parsedLevel, eventId, "Log spam {Level} #{Index}", parsedLevel, i);
            }
        }
    }

    return Results.Ok(new { count = n, level = parsedLevel.ToString() });
});

// 13. JIT pressure / cold-start compilation — detect with collect_events(kind="jit")
app.MapGet("/jit-pressure", (int? count) =>
{
    var n = Math.Clamp(count ?? 200, 1, 2_000);
    long checksum = 0;
    for (var i = 0; i < n; i++)
    {
        checksum += CreateAndInvokeJitPressureMethod(i);
    }

    return Results.Ok(new { count = n, checksum });
});

// 14. Slow in-flight request — detect with inspect_process(view="requests-now")
app.MapGet("/slow-hang", async (int? seconds) =>
{
    var delay = TimeSpan.FromSeconds(Math.Clamp(seconds ?? 5, 1, 30));
    await Task.Delay(delay);
    return Results.Ok(new { delayedSeconds = delay.TotalSeconds });
});

// 14. Async continuation stalls — detect with collect_thread_snapshot + query_snapshot(view="async-stalls")
app.MapGet("/async-stall", async (string? bucket, int? seconds) =>
{
    var normalizedBucket = AsyncStallFixture.NormalizeBucket(bucket);
    if (normalizedBucket is null)
    {
        return Results.BadRequest(new
        {
            error = "bucket must be one of: tcs, channel, sync-over-async, semaphore",
        });
    }

    var delay = TimeSpan.FromSeconds(Math.Clamp(seconds ?? 5, 1, 30));
    var waiters = await AsyncStallFixture.RunAsync(normalizedBucket, delay);
    return Results.Ok(new { bucket = normalizedBucket, delayedSeconds = delay.TotalSeconds, waiters });
});

// 15. ThreadPool starvation — detect with collect_events(kind="threadpool")
app.MapGet("/threadpool-starve", (int? blockers) =>
{
    var blockedWorkers = Math.Clamp(blockers ?? 50, 1, 256);
    for (var i = 0; i < blockedWorkers; i++)
    {
        Task.Run(static () => Thread.Sleep(TimeSpan.FromSeconds(15)));
    }

    return Results.Accepted($"/threadpool-starve?blockers={blockedWorkers}", new { blockers = blockedWorkers });
});

// 15. Lock storm — detect with collect_events(kind="contention")
app.MapGet("/lock-storm", (int? seconds, int? blockers) =>
{
    var duration = TimeSpan.FromSeconds(Math.Clamp(seconds ?? 5, 1, 30));
    var contenderCount = Math.Clamp(blockers ?? 8, 2, 128);
    var stopAt = DateTime.UtcNow + duration;
    long completedEntries = 0;
    var tasks = Enumerable.Range(0, contenderCount)
        .Select(_ => Task.Run(() =>
        {
            while (DateTime.UtcNow < stopAt)
            {
                lock (lockStormGate)
                {
                    Interlocked.Increment(ref completedEntries);
                    Thread.Sleep(100);
                }
            }
        }))
        .ToArray();

    Task.WaitAll(tasks);
    return Results.Ok(new { seconds = duration.TotalSeconds, blockers = contenderCount, completedEntries });
});

// 16. DB N+1 query storm — detect with collect_events(kind="db")
app.MapGet("/db-n+1", async (IDbContextFactory<BadCodeDbContext> dbContextFactory, int? count) =>
{
    var n = Math.Clamp(count ?? 15, 1, 250);
    await using var db = await dbContextFactory.CreateDbContextAsync();

    var rows = new List<string>(n);
    for (var i = 0; i < n; i++)
    {
        using var activity = dbActivitySource.StartActivity("database.command", ActivityKind.Client);
        activity?.SetTag("db.statement", "SELECT \"w\".\"Id\", \"w\".\"name\" FROM \"widgets\" AS \"w\" WHERE \"w\".\"Id\" = 1 LIMIT 1");
        activity?.SetTag("db.connection_string", dbConnectionString);
        activity?.SetTag("server.address", "badcode-db");
        activity?.SetTag("db.namespace", "main");

        var widget = await db.Widgets.AsNoTracking().SingleAsync(static widget => widget.Id == 1);
        rows.Add(widget.Name);
    }

    return Results.Ok(new { count = n, rows = rows.Count, sample = rows[0] });
});

// 17. Fatal crash fixture — detect with collect_events(kind="crash-guard")
app.MapGet("/crash", (string? mode) =>
{
    var normalizedMode = mode?.Trim().ToLowerInvariant() switch
    {
        null or "" or "unhandled" => "unhandled",
        "stackoverflow" => "stackoverflow",
        "oom" => "oom",
        _ => null,
    };

    if (normalizedMode is null)
    {
        return Results.BadRequest(new { error = "mode must be one of: unhandled, stackoverflow, oom" });
    }

    StartCrashThread(normalizedMode);
    return Results.Accepted($"/crash?mode={normalizedMode}", new { mode = normalizedMode, delayMs = 500 });
});

app.Run();

static GCHandleType ParseHandleKind(string? type) => type?.Trim().ToLowerInvariant() switch
{
    "normal" => GCHandleType.Normal,
    "weak" => GCHandleType.Weak,
    "pinned" or null or "" => GCHandleType.Pinned,
    _ => throw new BadHttpRequestException("type must be one of: pinned, normal, weak"),
};

static int CreateAndInvokeJitPressureMethod(int seed)
{
    var method = new DynamicMethod($"JitPressureDynamicMethod{seed:D4}", typeof(int), new[] { typeof(int) }, typeof(Program).Module, skipVisibility: true);
    var il = method.GetILGenerator();
    il.Emit(OpCodes.Ldarg_0);
    for (var i = 0; i < 24; i++)
    {
        il.Emit(OpCodes.Ldc_I4, seed + i + 1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldc_I4, (i % 7) + 1);
        il.Emit(OpCodes.Xor);
    }

    il.Emit(OpCodes.Ret);

    var handler = method.CreateDelegate<Func<int, int>>();
    return handler(seed);
}

static void StartCrashThread(string mode)
{
    var thread = new Thread(() =>
    {
        Thread.Sleep(500);
        switch (mode)
        {
            case "stackoverflow":
                _ = CauseStackOverflow(0);
                break;
            case "oom":
                ThrowAfterFlushWindow(new InvalidOperationException(CrashFixtureMessage("BadCodeSample crash fixture simulated an unhandled OOM termination.")));
                break;
            default:
                ThrowAfterFlushWindow(new InvalidOperationException(CrashFixtureMessage("BadCodeSample crash fixture threw an unhandled exception.")));
                break;
        }
    })
    {
        IsBackground = false,
        Name = $"BadCodeSample crash fixture ({mode})",
    };
    thread.Start();
}

[MethodImpl(MethodImplOptions.NoInlining)]
static int CauseStackOverflow(int depth)
    => depth + CauseStackOverflow(depth + 1);

static string CrashFixtureMessage(string headline)
    => headline + Environment.NewLine + Environment.StackTrace;

static void ThrowAfterFlushWindow(Exception exception)
{
    try
    {
        throw exception;
    }
    catch (Exception captured)
    {
        Thread.Sleep(1000);
        ExceptionDispatchInfo.Capture(captured).Throw();
    }
}

static async Task<LeakedSocketConnection> LeakLoopbackSocketAsync(int port)
{
    var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, port);
    return await CompleteCloseWaitHandshakeAsync(client, "localhost", "/");
}

static async Task<LeakedSocketConnection> LeakRemoteSocketAsync(string host)
{
    string requestHost;
    string requestPath;
    int port;

    if (Uri.TryCreate(host, UriKind.Absolute, out var absolute) && absolute.Scheme == Uri.UriSchemeHttp)
    {
        requestHost = absolute.Host;
        requestPath = string.IsNullOrEmpty(absolute.PathAndQuery) ? "/" : absolute.PathAndQuery;
        port = absolute.Port;
    }
    else
    {
        requestHost = host;
        requestPath = "/get";
        port = 80;
    }

    var client = new TcpClient();
    await client.ConnectAsync(requestHost, port);
    return await CompleteCloseWaitHandshakeAsync(client, requestHost, requestPath);
}

static async Task<LeakedSocketConnection> CompleteCloseWaitHandshakeAsync(TcpClient client, string host, string path)
{
    var stream = client.GetStream();
    var request = Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n");
    await stream.WriteAsync(request);

    var buffer = new byte[512];
    while (true)
    {
        var read = await stream.ReadAsync(buffer);
        if (read == 0)
        {
            break;
        }
    }

    return new LeakedSocketConnection(client, stream);
}

sealed class LeakedSocketConnection(TcpClient client, NetworkStream stream)
{
    public TcpClient Client { get; } = client;
    public NetworkStream Stream { get; } = stream;
}

sealed record AlcLeakRoot(
    AssemblyLoadContext Context,
    Assembly Assembly,
    object Instance,
    EventHandler Handler);

sealed class LeakyAssemblyLoadContext(string name, string assemblyPath) : AssemblyLoadContext(name, isCollectible: true)
{
    private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
}

static class AlcLeakFixture
{
    private static readonly List<EventHandler> LeakedHandlers = new();

    public static EventHandler Subscribe(object alcInstance)
    {
        EventHandler handler = (_, _) => GC.KeepAlive(alcInstance);
        LeakedHandlers.Add(handler);
        return handler;
    }
}

static class AsyncStallFixture
{
    public static string? NormalizeBucket(string? bucket)
        => bucket?.Trim().ToLowerInvariant() switch
        {
            "tcs" => "tcs",
            "channel" => "channel",
            "sync-over-async" => "sync-over-async",
            "semaphore" => "semaphore",
            _ => null,
        };

    public static Task<int> RunAsync(string bucket, TimeSpan delay)
        => bucket switch
        {
            "tcs" => RunTcsAsync(delay),
            "channel" => RunChannelAsync(delay),
            "sync-over-async" => RunSyncOverAsyncAsync(delay),
            "semaphore" => RunSemaphoreAsync(delay),
            _ => Task.FromResult(0),
        };

    private static async Task<int> RunTcsAsync(TimeSpan delay)
    {
        var completions = Enumerable.Range(0, 16)
            .Select(_ => new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var waiters = completions
            .Select(tcs => Task.Run(() => BlockOnTaskCompletionSourceAsync(tcs)))
            .ToArray();

        await Task.Delay(delay).ConfigureAwait(false);
        foreach (var tcs in completions)
        {
            tcs.TrySetResult(42);
        }

        await Task.WhenAll(waiters).ConfigureAwait(false);
        return waiters.Length;
    }

    private static async Task<int> RunChannelAsync(TimeSpan delay)
    {
        var channels = new List<Channel<int>>();
        var waiters = new List<Task>();
        for (var i = 0; i < 16; i++)
        {
            var channel = Channel.CreateUnbounded<int>();
            channels.Add(channel);
            waiters.Add(Task.Run(async () => await AwaitChannelAsync(channel.Reader).ConfigureAwait(false)));
        }

        await Task.Delay(delay).ConfigureAwait(false);
        foreach (var channel in channels)
        {
            channel.Writer.TryWrite(1);
        }

        await Task.WhenAll(waiters).ConfigureAwait(false);
        return waiters.Count;
    }

    private static async Task<int> RunSemaphoreAsync(TimeSpan delay)
    {
        var semaphores = new List<SemaphoreSlim>();
        var waiters = new List<Task>();
        for (var i = 0; i < 16; i++)
        {
            var semaphore = new SemaphoreSlim(0, 1);
            semaphores.Add(semaphore);
            waiters.Add(Task.Run(async () => await AwaitSemaphoreAsync(semaphore).ConfigureAwait(false)));
        }

        await Task.Delay(delay).ConfigureAwait(false);
        foreach (var semaphore in semaphores)
        {
            semaphore.Release();
        }

        await Task.WhenAll(waiters).ConfigureAwait(false);
        foreach (var semaphore in semaphores)
        {
            semaphore.Dispose();
        }

        return waiters.Count;
    }

    private static async Task<int> RunSyncOverAsyncAsync(TimeSpan delay)
    {
        var blockers = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => BlockOnTaskResult(delay)))
            .ToArray();
        await Task.WhenAll(blockers).ConfigureAwait(false);
        return blockers.Length;
    }

    private static int BlockOnTaskCompletionSourceAsync(TaskCompletionSource<int> tcs)
        => AwaitTaskCompletionSourceAsync(tcs).GetAwaiter().GetResult();

    private static async Task<int> AwaitTaskCompletionSourceAsync(TaskCompletionSource<int> tcs)
        => await tcs.Task.ConfigureAwait(false);

    private static async Task AwaitChannelAsync(ChannelReader<int> reader)
    {
        _ = await reader.WaitToReadAsync().ConfigureAwait(false);
    }

    private static async Task AwaitSemaphoreAsync(SemaphoreSlim semaphore)
    {
        await semaphore.WaitAsync().ConfigureAwait(false);
    }

    private static int BlockOnTaskResult(TimeSpan delay)
        => Task.Run(async () =>
        {
            await Task.Delay(delay).ConfigureAwait(false);
            return 42;
        }).Result;
}

sealed class LoopbackCloseServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            _acceptLoop?.GetAwaiter().GetResult();
        }
        catch
        {
        }
        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            using var stream = client.GetStream();
                            var buffer = new byte[256];
                            _ = await stream.ReadAsync(buffer, _cts.Token);
                        }
                        catch
                        {
                        }
                    }
                }, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }
}
