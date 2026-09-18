using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(35));
var token = budget.Token;
var enrich = args is ["enrich"];
var destination = args is ["destination"];
var window = args is ["window"];
if (args is not (["plain"] or ["enrich"] or ["destination"] or ["window"])) throw new ArgumentException("Expected plain, enrich, destination or window.");
using var observer = new HttpObserver(enrich);
using var allListeners = DiagnosticListener.AllListeners.Subscribe(observer);
// This witness requests no data: only the external EventPipe subscriptions enable recording.
using var listener = new ActivityListener
{
    ShouldListenTo = static source => source.Name == "System.Net.Http",
    Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.None,
    SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.None,
    ActivityStopped = static activity => Write(new
    {
        kind = "source", activity.Source.Name, activity.OperationName,
        traceId = activity.TraceId.ToHexString(), spanId = activity.SpanId.ToHexString(),
        startTicks = activity.StartTimeUtc.Ticks, durationTicks = activity.Duration.Ticks,
        activity.IsAllDataRequested, activity.Recorded,
        tags = activity.TagObjects.ToDictionary(static pair => pair.Key,
            static pair => Convert.ToString(pair.Value, CultureInfo.InvariantCulture)),
    }),
};
ActivitySource.AddActivityListener(listener);
using var server = new TcpListener(IPAddress.Loopback, 0);
server.Start();
var port = ((IPEndPoint)server.LocalEndpoint).Port;
using var secondServer = new TcpListener(IPAddress.Loopback, 0);
if (destination) secondServer.Start();
var secondPort = destination ? ((IPEndPoint)secondServer.LocalEndpoint).Port : port;
Write(new { kind = "configuration", runtime = Environment.Version.ToString(), enrich, port, secondPort, destination,
    processId = Environment.ProcessId, listenerSampling = "None", requests = window ? 1 : 4,
    diagnosticSourceVersion = typeof(Activity).Assembly.GetName().Version?.ToString(),
    httpVersion = typeof(HttpClient).Assembly.GetName().Version?.ToString() });
if (await Console.In.ReadLineAsync(token) != "observe") throw new InvalidOperationException("Expected observe.");
await EmitReadiness();
if (await Console.In.ReadLineAsync(token) != "go") throw new InvalidOperationException("Expected go.");
using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
var acceptedCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var releaseWindow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var serving = Task.WhenAll(Serve(server, window ? 1 : destination ? 3 : 4),
    destination ? Serve(secondServer, 1) : Task.CompletedTask);
try
{
    if (window)
    {
        var request = Send("/window", 200);
        if (await Console.In.ReadLineAsync(token) != "late") throw new InvalidOperationException("Expected late.");
        await EmitReadiness();
        if (await Console.In.ReadLineAsync(token) != "release") throw new InvalidOperationException("Expected release.");
        releaseWindow.SetResult();
        await request;
    }
    else
    {
        await Task.WhenAll(Send("/fast", 200), Send("/slow", 200));
        await Send("/unavailable", 503);
        await Send("/cancel", null);
    }
}
finally
{
    await serving;
}
Console.WriteLine("DONE");
if (await Console.In.ReadLineAsync(token) != "quit") throw new InvalidOperationException("Expected quit.");

async Task Send(string path, int? expectedStatus)
{
    using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
    var selectedPort = destination && path == "/slow" ? secondPort : port;
    var uri = destination
        ? $"http://fixture-user:fixture-password@127.0.0.1:{selectedPort}{path}?fixture-secret=query-value#fixture-fragment"
        : $"http://127.0.0.1:{selectedPort}{path}";
    var request = client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
    if (expectedStatus is null)
    {
        await acceptedCancellation.Task.WaitAsync(token);
        await Task.Delay(150, token);
        await cancel.CancelAsync();
    }
    try
    {
        using var response = await request;
        if ((int)response.StatusCode != expectedStatus) throw new InvalidOperationException("Unexpected status.");
        Write(new { kind = "outcome", path, status = (int?)response.StatusCode, cancelled = false });
    }
    catch (OperationCanceledException) when (expectedStatus is null && cancel.IsCancellationRequested && !token.IsCancellationRequested)
    {
        Write(new { kind = "outcome", path, status = (int?)null, cancelled = true });
    }
}

async Task Serve(TcpListener endpoint, int count)
{
    var connections = new List<Task>(4);
    try
    {
        for (var i = 0; i < count; i++) connections.Add(Handle(await endpoint.AcceptTcpClientAsync(token)));
    }
    finally
    {
        await Task.WhenAll(connections);
    }
}

async Task Handle(TcpClient connection)
{
    using (connection)
    {
        var stream = connection.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var first = await reader.ReadLineAsync(token) ?? throw new IOException("Missing request line.");
        var path = first.Split(' ')[1].Split('?')[0];
        while (await reader.ReadLineAsync(token) is { Length: > 0 }) { }
        if (path == "/window") await releaseWindow.Task.WaitAsync(token);
        if (path == "/cancel")
        {
            acceptedCancellation.SetResult();
            var buffer = new byte[1];
            if (await stream.ReadAsync(buffer, token) != 0) throw new IOException("Expected cancelled connection EOF.");
            return;
        }
        await Task.Delay(path == "/slow" ? 250 : 50, token);
        var status = path == "/unavailable" ? "503 Service Unavailable" : "200 OK";
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), token);
    }
}

async Task EmitReadiness()
{
    using var readiness = new ActivitySource("HttpActivityTarget.Readiness");
    for (var i = 0; i < 40; i++)
    {
        using var activity = readiness.StartActivity("ready");
        await Task.Delay(25, token);
    }
}

static void Write<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value));

internal sealed class HttpObserver(bool enrich) : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private IDisposable? _subscription;
    public void OnNext(DiagnosticListener value)
    {
        if (value.Name == "HttpHandlerDiagnosticListener")
            _subscription = value.Subscribe(this, static name => name is "System.Net.Http.HttpRequestOut.Start");
    }

    public void OnNext(KeyValuePair<string, object?> value)
    {
        var request = value.Value?.GetType().GetProperty("Request")?.GetValue(value.Value) as HttpRequestMessage
            ?? throw new InvalidOperationException("Missing DiagnosticSource Request.");
        var activity = Activity.Current ?? throw new InvalidOperationException("Missing HTTP Activity.");
        var uri = request.RequestUri ?? throw new InvalidOperationException("Missing request URI.");
        if (enrich)
        {
            // Explicit test-only enrichment, not OpenTelemetry instrumentation or a product prerequisite.
            activity.SetTag("server.address", uri.Host);
            activity.SetTag("url.full", uri.AbsoluteUri);
            activity.SetTag("http.request.method", request.Method.Method);
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            kind = "diagnosticSource", eventName = value.Key, traceId = activity.TraceId.ToHexString(),
            spanId = activity.SpanId.ToHexString(), host = uri.Host, port = uri.Port, path = uri.AbsolutePath,
            method = request.Method.Method, enrich,
        }));
    }

    public void OnCompleted() { }
    public void OnError(Exception error) => throw new InvalidOperationException("DiagnosticSource observer failed.", error);
    public void Dispose() => _subscription?.Dispose();
}
